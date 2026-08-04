using System.Globalization;
using System.Text.Json;
using LeaseBook.Modules.Capabilities.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Adapters;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Web.Capabilities;

/// <summary>
/// Executes a parsed <see cref="CapabilitiesAction"/> (ADR-028). This is the only write surface for
/// capability state — there is no endpoint and no UI — and every mutation it performs lands with a
/// <c>platform_audit_events</c> row in the SAME transaction, so the platform audit trail exists from
/// the first write rather than being retrofitted later.
/// <para>
/// <b>Everything mutating runs inside <see cref="PlatformScopedExecutor"/>.</b> It is the single call
/// site that sets <c>app.platform</c> (<c>PlatformScopeCallSiteTests</c> enforces exactly one), and
/// without it the writes here do not fail loudly in any uniform way: an INSERT raises 42501, but an
/// UPDATE or DELETE on <c>feature_flags</c> is filtered by the write policy's USING and simply
/// affects <b>zero rows</b>. That silence is why the EF write path is used for the flag toggle — EF
/// turns a zero-row UPDATE into <see cref="DbUpdateConcurrencyException"/> — and why any raw
/// statement here would have to assert its own affected-row count.
/// </para>
/// <para>
/// <b><c>NOTIFY</c> is issued inside that same transaction.</b> Postgres queues notifications and
/// delivers them after commit, preserving order, so a listener can never be woken before the row it
/// must read is visible. Issued after the commit instead, the race is back. See
/// <see cref="CapabilityNotificationListener"/>, which owns the channel name.
/// </para>
/// </summary>
public static class CapabilitiesCommand
{
    /// <summary>
    /// Runs one invocation and returns the process exit code: 0 on success, 1 on any parse or write
    /// failure.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!CapabilitiesVerb.TryResolve(args, out var action, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var ct = CancellationToken.None;

        // A scope of our own: this runs from the root provider, where no request or job scope exists.
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();

        if (action.Kind == CapabilitiesActionKind.List)
        {
            await ListAsync(db, action, ct);
            return 0;
        }

        var executor = scope.ServiceProvider.GetRequiredService<PlatformScopedExecutor>();

        try
        {
            await executor.RunAsync(() => ApplyAsync(db, action, ct), ct);
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            // A known, operator-actionable database rejection. Anything else propagates with its
            // stack: an unexpected failure on the platform plane is not something to summarize away.
            Console.Error.WriteLine(message);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// The operator identity recorded on every row this verb writes.
    /// <para>
    /// <b>Not a user id, deliberately.</b> This process runs as <c>leasebook_app</c> with no
    /// authenticated principal — locally it is an engineer's shell, in production it is the
    /// capabilities ACA job (ADR-027) — so there is no <c>asp_net_users</c> row to point at and
    /// inventing one would put a fiction in an append-only audit trail. Recorded instead is what is
    /// actually true: this verb, that OS user, that machine. Whether a platform admin eventually
    /// lives in <c>asp_net_users</c> under a new role or in a separate store is Project 2's question;
    /// the <c>cli:</c> prefix is what lets those rows be told apart afterwards.
    /// </para>
    /// </summary>
    public static string Actor { get; } = BuildActor();

    private static string BuildActor()
    {
        static string OrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

        return $"cli:{OrUnknown(Environment.UserName)}@{OrUnknown(Environment.MachineName)}";
    }

    // ── Read ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints the registry joined to its stored flag state.
    /// <para>
    /// <b>No platform scope, and no audit row.</b> <c>feature_flags</c> carries
    /// <c>feature_flags_read — FOR SELECT USING (true)</c>, so a context-free read returns every row;
    /// opening the seam's only privilege escape to read a table that needs no escape would be
    /// gratuitous. <c>CapabilityRegistryValidator</c> reads it the same way for the same reason. And
    /// a read changes nothing, so there is nothing for the audit trail to record.
    /// </para>
    /// </summary>
    private static async Task ListAsync(DbContext db, CapabilitiesAction action, CancellationToken ct)
    {
        var flags = await db.Set<FeatureFlag>().AsNoTracking().ToDictionaryAsync(
            f => f.Name, StringComparer.Ordinal, ct);

        Console.WriteLine($"{"CAPABILITY",-26} {"FLAG",-9} {"DEFAULT",-8} {"GRANT",-6} {"MONEY",-7} UPDATED");

        foreach (var capability in CapabilityCatalog.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            flags.TryGetValue(capability.Name, out var flag);

            // "(none)" is not cosmetic: an ABSENT row and an explicit `enabled = false` resolve
            // differently — a cohort ORs over an absent row but never over an explicit kill.
            var state = flag is null ? "(none)" : flag.Enabled ? "enabled" : "killed";
            var updated = flag is null
                ? "—"
                : $"{flag.UpdatedAt.ToUniversalTime():u} by {flag.UpdatedBy}";
            var money = capability.IsMoneyPath ? capability.IsFixture ? "fixture" : "yes" : "no";

            Console.WriteLine(
                $"{capability.Name,-26} {state,-9} " +
                $"{(capability.DefaultEnabled ? "on" : "off"),-8} " +
                $"{(capability.RequiresGrant ? "yes" : "no"),-6} {money,-7} {updated}");
        }

        if (action.Stale)
        {
            // Parsed here so Task 13 adds behavior rather than grammar, but reported as unavailable
            // rather than silently ignored — an operator must never read an empty stale report as
            // "nothing is stale".
            Console.WriteLine();
            Console.WriteLine(
                "capabilities: --stale is parsed but age reporting is not available yet (it arrives " +
                "with the money-path age gate). Treat this listing as complete and the staleness " +
                "column as unknown.");
        }
    }

    // ── Writes ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The body of one platform-scoped transaction: read what is being replaced, write the state row,
    /// write the audit row, then <c>NOTIFY</c>. All four steps commit or roll back together.
    /// </summary>
    private static async Task ApplyAsync(DbContext db, CapabilitiesAction action, CancellationToken ct)
    {
        var capability = action.Capability!;

        // One timestamp for every row this transaction writes, read from POSTGRES rather than the
        // app clock. `now()` is transaction start time, so effective_at can never land ahead of the
        // clock a later resolver compares it against — the resolver's `effective_at <= now()` makes a
        // future-dated row pending, and app/database clock skew would otherwise make a grant
        // invisible for the length of that skew with nothing to show for it.
        var now = await db.Database.SqlQuery<DateTime>($"""SELECT now() AS "Value" """).SingleAsync(ct);

        var (auditAction, detail) = action.Kind switch
        {
            CapabilitiesActionKind.FlagEnable or CapabilitiesActionKind.FlagDisable =>
                await WriteFlagAsync(db, action, now, ct),
            CapabilitiesActionKind.Grant or CapabilitiesActionKind.Revoke =>
                WriteEntitlement(db, action, now),
            CapabilitiesActionKind.CohortAdd => WriteCohort(db, action, now),
            _ => throw new InvalidOperationException($"Unhandled action kind {action.Kind}."),
        };

        db.Add(new PlatformAuditEvent
        {
            Id = UuidV7.NewId(),
            OccurredAt = now,
            Actor = Actor,
            Action = auditAction,
            Capability = capability,
            OrgId = action.OrgId,
            DetailJson = detail,
        });

        // ONE SaveChanges for the state row and its audit row: they are the same fact, and EF sends
        // them in one batch inside the executor's transaction. A failure on either — the entitlements
        // uniqueness index, the FK to orgs — takes both down, which is the atomicity the audit trail
        // is worth nothing without.
        await db.SaveChangesAsync(ct);

        // Inside the transaction. See the class remarks.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_notify({CapabilityNotificationListener.Channel}, {capability})", ct);

        Console.WriteLine(Summarize(action, now));
    }

    /// <summary>
    /// Upserts the flag through EF rather than <c>INSERT … ON CONFLICT</c>.
    /// <para>
    /// <b>Because a tenant-plane UPDATE here is fail-closed but SILENT.</b> RLS filters the target
    /// rows through the write policy's <c>USING</c>, so the statement succeeds affecting zero rows;
    /// only INSERT raises 42501. EF's update path compares the affected-row count against what it
    /// expected and throws <see cref="DbUpdateConcurrencyException"/> on the mismatch, which turns
    /// that silence into a failure without a hand-written row-count assertion. Any raw
    /// <c>ExecuteSql</c> upsert here would have to assert the count itself.
    /// </para>
    /// <para>
    /// The previous value is captured because a flag is MUTABLE state: unlike an entitlement, whose
    /// history is the table, a flag's prior value is destroyed by the write. If the audit row does
    /// not carry it, it is gone.
    /// </para>
    /// </summary>
    private static async Task<(string Action, string Detail)> WriteFlagAsync(
        DbContext db, CapabilitiesAction action, DateTime now, CancellationToken ct)
    {
        var enabled = action.Kind == CapabilitiesActionKind.FlagEnable;
        var name = action.Capability!;

        var flag = await db.Set<FeatureFlag>().SingleOrDefaultAsync(f => f.Name == name, ct);
        bool? previous = flag?.Enabled;

        if (flag is null)
        {
            db.Add(new FeatureFlag
            {
                Name = name,
                Enabled = enabled,
                UpdatedAt = now,
                UpdatedBy = Actor,
            });
        }
        else
        {
            flag.Enabled = enabled;
            flag.UpdatedAt = now;
            flag.UpdatedBy = Actor;
        }

        return (
            enabled ? "flag.enable" : "flag.disable",
            Json(new Dictionary<string, object?>
            {
                ["enabled"] = enabled,
                // null means there was no row at all, which resolves as the registry default rather
                // than as an explicit kill — a distinction the resolution order turns on.
                ["previous"] = previous,
            }));
    }

    /// <summary>
    /// Appends one grant EVENT. There is no <c>revoked_at</c> to update: a revoke is a new row with
    /// <c>granted = false</c>, and current state is the latest row per <c>(org, capability)</c>. The
    /// table has no UPDATE or DELETE grant in either plane, so an append is the only shape available
    /// — and re-running a grant appends another event rather than being a no-op, because the audit
    /// trail records what an operator DID, not the diff it happened to produce.
    /// </summary>
    private static (string Action, string Detail) WriteEntitlement(
        DbContext db, CapabilitiesAction action, DateTime now)
    {
        var granted = action.Kind == CapabilitiesActionKind.Grant;

        db.Add(new Entitlement
        {
            Id = UuidV7.NewId(),
            OrgId = action.OrgId!.Value,
            Capability = action.Capability!,
            Granted = granted,
            EffectiveAt = now,
            Actor = Actor,
        });

        return (
            granted ? "entitlement.grant" : "entitlement.revoke",
            Json(new Dictionary<string, object?>
            {
                ["granted"] = granted,
                ["effective_at"] = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            }));
    }

    private static (string Action, string Detail) WriteCohort(
        DbContext db, CapabilitiesAction action, DateTime now)
    {
        db.Add(new CapabilityCohort
        {
            Id = UuidV7.NewId(),
            Capability = action.Capability!,
            OrgId = action.OrgId!.Value,
            UserId = action.UserId,
            AddedAt = now,
            AddedBy = Actor,
        });

        return (
            "cohort.add",
            Json(new Dictionary<string, object?>
            {
                ["user_id"] = action.UserId?.ToString(),
            }));
    }

    // ── Reporting ───────────────────────────────────────────────────────────────────────────────

    private static string Summarize(CapabilitiesAction action, DateTime now)
    {
        var capability = action.Capability;
        var org = action.OrgId;

        return action.Kind switch
        {
            CapabilitiesActionKind.FlagEnable =>
                $"capabilities: flag '{capability}' is now ENABLED deployment-wide (by {Actor}).",
            CapabilitiesActionKind.FlagDisable =>
                $"capabilities: flag '{capability}' is now KILLED deployment-wide (by {Actor}). An " +
                "explicit kill beats a cohort match; deleting the row would restore the registry default.",
            CapabilitiesActionKind.Grant =>
                $"capabilities: granted '{capability}' to org {org} effective {now.ToUniversalTime():u}.",
            CapabilitiesActionKind.Revoke =>
                $"capabilities: revoked '{capability}' from org {org} effective {now.ToUniversalTime():u} " +
                "(appended as a new event — entitlements are append-only).",
            CapabilitiesActionKind.CohortAdd => action.UserId is { } user
                ? $"capabilities: added user {user} in org {org} to the '{capability}' cohort."
                : $"capabilities: added org {org} to the '{capability}' cohort.",
            _ => $"capabilities: {action.Kind} applied.",
        };
    }

    /// <summary>
    /// Maps the database rejections an operator can actually act on onto a message that says what to
    /// do. Anything unrecognized returns null and propagates — swallowing an unexpected failure on
    /// the platform plane would be worse than a stack trace.
    /// </summary>
    private static string? Describe(Exception exception)
    {
        var postgres = Unwrap(exception);
        if (postgres is null)
        {
            return null;
        }

        return postgres.SqlState switch
        {
            // 23505 on the entitlements uniqueness index: two grant events for one (org, capability)
            // at the same instant. `id` is NOT a usable tie-break (UUIDv7's low bits are random), so
            // the resolver could not order them — the index rejects the second rather than leaving
            // "current state" undefined. In practice this is a double-invocation; re-run it.
            PostgresErrorCodes.UniqueViolation
                when postgres.ConstraintName == "ux_entitlements_org_capability_effective_at" =>
                "capabilities: an entitlement event for this org and capability already exists at this " +
                "exact instant, so this one was rejected and nothing was written. Two events at one " +
                "timestamp have no defined order, which would leave current entitlement state " +
                "ambiguous. This is almost always a command run twice — check `capabilities list` and " +
                "re-run if it really is a second change.",

            PostgresErrorCodes.ForeignKeyViolation =>
                "capabilities: that org does not exist. Entitlements and cohort rules reference `orgs`, " +
                "so the id has to name a real tenant — check it with `check-invariants --all` or pass " +
                "one of 'demo', 'cutover', 'load', or 'scenario'.",

            PostgresErrorCodes.InsufficientPrivilege =>
                "capabilities: the database refused the write (42501). Platform-plane writes require " +
                "app.platform, which only PlatformScopedExecutor sets — if this verb reached here, the " +
                "escape did not open. " + postgres.MessageText,

            _ => null,
        };
    }

    /// <summary>
    /// EF wraps provider failures (in <see cref="DbUpdateException"/>, and transient ones in an
    /// <see cref="InvalidOperationException"/>), so the chain is walked rather than the top frame
    /// matched — the same trap <c>CapabilityRegistryValidator.IsUnreachable</c> documents.
    /// </summary>
    private static PostgresException? Unwrap(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private static string Json(Dictionary<string, object?> detail) => JsonSerializer.Serialize(detail);
}
