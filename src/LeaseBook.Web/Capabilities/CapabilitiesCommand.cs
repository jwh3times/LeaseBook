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
/// <b>It is also the only READ surface for three of the four tables</b>, which is why
/// <see cref="ListAsync"/> reports entitlement and cohort state and not just flags: an operator told
/// to go and check whether a grant landed must have something that can answer that.
/// </para>
/// <para>
/// <b>Everything mutating runs inside <see cref="PlatformScopedExecutor"/>.</b> It is the single call
/// site that sets <c>app.platform</c> (<c>PlatformScopeCallSiteTests</c> enforces exactly one), and
/// without it the writes here do not fail loudly in any uniform way: an INSERT raises 42501, but an
/// UPDATE or DELETE on <c>feature_flags</c> is filtered by the write policy's USING and simply
/// affects <b>zero rows</b>. That silence is why the EF write path is used for the flag toggle and the
/// cohort removal — EF turns a zero-row UPDATE or DELETE into
/// <see cref="DbUpdateConcurrencyException"/> — and why any raw statement here would have to assert
/// its own affected-row count.
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
    /// Environment variable naming the human or system accountable for a change. See
    /// <see cref="Actor"/>.
    /// </summary>
    public const string OperatorVariable = "LEASEBOOK_OPERATOR";

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
        var executor = scope.ServiceProvider.GetRequiredService<PlatformScopedExecutor>();

        // Read ONCE per invocation, then threaded through every row this command writes. The value is
        // an environment read; taking it separately for the state row and its audit row would let the
        // two disagree, and both are append-only, so a mismatch could never be corrected afterwards.
        var actor = Actor;

        try
        {
            // Inside the try like every other path: `list` reads entitlements and capability_cohorts,
            // which are platform-gated, so a 42501 here deserves the same explanation a write gets
            // rather than a raw stack. It was outside while the listing read only feature_flags.
            if (action.Kind == CapabilitiesActionKind.List)
            {
                await executor.RunAsync(() => ListAsync(db, action, ct), ct);
                return 0;
            }

            var applied = default(DateTime);
            await executor.RunAsync(async () => applied = await ApplyAsync(db, action, actor, ct), ct);

            // Printed AFTER the transaction commits, never inside it. A success line followed by a
            // commit-time stack trace would tell the operator the opposite of what happened.
            Console.WriteLine(Summarize(action, applied, actor));
        }
        catch (CapabilitiesRefusalException refusal)
        {
            // A deliberate in-transaction refusal: nothing to remove, or an org that does not exist.
            // Throwing is what rolled the transaction back, so no state row and no audit row survive.
            Console.Error.WriteLine(refusal.Message);
            return 1;
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
    /// authenticated principal — locally an engineer's shell, in production the capabilities ACA job
    /// (ADR-027) — so there is no <c>asp_net_users</c> row to point at, and inventing one would put a
    /// fiction in an append-only audit trail. Whether a platform admin eventually lives in
    /// <c>asp_net_users</c> under a new role or in a separate store is Project 2's question.
    /// </para>
    /// <para>
    /// <b><see cref="OperatorVariable"/> is what makes production rows attributable.</b> Process
    /// identity alone degrades to <c>cli:root@&lt;ephemeral-container-id&gt;</c> for every row the ACA
    /// job writes, which attributes a change to nobody. The job sets the variable to the human or
    /// system on the hook; the process identity is still appended, because the two answer different
    /// questions — who decided, versus where it ran from. These rows are append-only, so nothing
    /// written under the weaker convention can ever be corrected; that is why the variable exists from
    /// the first write rather than being added once someone notices.
    /// </para>
    /// <para>
    /// Recomputed per read rather than cached in a static, so a value exported after process start
    /// still applies and no test needs a static reset.
    /// </para>
    /// </summary>
    public static string Actor => BuildActor(Environment.GetEnvironmentVariable(OperatorVariable));

    /// <summary>The pure half of <see cref="Actor"/>, so the convention is testable without env state.</summary>
    public static string BuildActor(string? configuredOperator)
    {
        static string OrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

        var process = $"cli:{OrUnknown(Environment.UserName)}@{OrUnknown(Environment.MachineName)}";

        return string.IsNullOrWhiteSpace(configuredOperator)
            ? process
            : $"operator:{configuredOperator.Trim()} ({process})";
    }

    // ── Read ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints the registry joined to its stored state: the flag, plus how many orgs hold a live
    /// entitlement and how many cohort rules exist. With <c>--org</c> it adds that tenant's own
    /// entitlement and cohort state; with <c>--stale</c> it appends the age report
    /// (<see cref="ReportAgeAsync"/>).
    /// <para>
    /// <b>Platform-scoped, and it has to be.</b> <c>feature_flags</c> alone would not need it
    /// (<c>feature_flags_read</c> is <c>FOR SELECT USING (true)</c>, which is why
    /// <c>CapabilityRegistryValidator</c> reads it bare), but <c>entitlements</c> and
    /// <c>capability_cohorts</c> are visible across orgs only under <c>app.platform</c> — without it
    /// their <c>_org_read</c> policy filters to zero rows and this listing would report "nobody is
    /// entitled to anything" with no error anywhere. Writing no audit row is correct: a read changes
    /// nothing.
    /// </para>
    /// <para>
    /// Entitlement state is the LATEST event per <c>(org, capability)</c>, ordered exactly as
    /// <c>CapabilityStateReader</c> orders it — <c>effective_at DESC, granted ASC, id DESC</c>, under
    /// <c>effective_at &lt;= now()</c> — so the listing and the resolver can never disagree about what
    /// is live. A future-dated row is pending in both.
    /// </para>
    /// </summary>
    private static async Task ListAsync(DbContext db, CapabilitiesAction action, CancellationToken ct)
    {
        // Before anything is printed, so a bad --org fails cleanly instead of emitting a valid
        // deployment-wide table and then erroring — see AssertOrgExistsAsync for why it is checked.
        if (action.OrgId is { } target)
        {
            await AssertOrgExistsAsync(db, target, ct);
        }

        var flags = await db.Set<FeatureFlag>().AsNoTracking().ToDictionaryAsync(
            f => f.Name, StringComparer.Ordinal, ct);

        var entitled = (await db.Database
            .SqlQuery<CapabilityCount>(
                $"""
                 SELECT capability, count(*) AS count
                 FROM (
                     SELECT DISTINCT ON (org_id, capability) capability, granted
                     FROM entitlements
                     WHERE effective_at <= now()
                     ORDER BY org_id, capability, effective_at DESC, granted ASC, id DESC
                 ) latest
                 WHERE granted
                 GROUP BY capability
                 """)
            .ToListAsync(ct))
            .ToDictionary(r => r.Capability, r => r.Count, StringComparer.Ordinal);

        var cohorts = (await db.Database
            .SqlQuery<CapabilityCount>(
                $"SELECT capability, count(*) AS count FROM capability_cohorts GROUP BY capability")
            .ToListAsync(ct))
            .ToDictionary(r => r.Capability, r => r.Count, StringComparer.Ordinal);

        Console.WriteLine(
            $"{"CAPABILITY",-26} {"FLAG",-9} {"DEFAULT",-8} {"GRANT",-6} {"MONEY",-7} " +
            $"{"ENTITLED",-9} {"COHORTS",-8} UPDATED");

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
                $"{(capability.RequiresGrant ? "yes" : "no"),-6} {money,-7} " +
                $"{entitled.GetValueOrDefault(capability.Name),-9} " +
                $"{cohorts.GetValueOrDefault(capability.Name),-8} {updated}");
        }

        if (action.OrgId is { } orgId)
        {
            await ListForOrgAsync(db, orgId, ct);
        }

        if (action.Stale)
        {
            await ReportAgeAsync(ct);
        }
    }

    /// <summary>
    /// The age half of the listing (ADR-028): when each capability entered the registry, how old it is,
    /// and whether a money-path one has outlived <see cref="CapabilityAge.PolicyWindow"/>.
    /// <para>
    /// <b>The same verdict CI enforces, from the same code.</b> <c>CapabilityAgeTests</c> fails the
    /// build on exactly what this prints as STALE — both call <see cref="CapabilityAge.IsStale"/> — so
    /// an operator can never be told "within window" by the tool that CI is about to contradict.
    /// </para>
    /// <para>
    /// <b>Age comes from git history, so it is UNKNOWN wherever the source tree or history is not.</b>
    /// That includes the production ACA job (ADR-027), which runs from an image. The unavailable case is
    /// stated in full, at the top, before any row is printed: a staleness report that renders an empty
    /// or dashed column reads as "nothing is stale", which is the one conclusion this must never invite.
    /// </para>
    /// </summary>
    private static async Task ReportAgeAsync(CancellationToken ct)
    {
        var report = await CapabilityAge.ResolveAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var window = (int)CapabilityAge.PolicyWindow.TotalDays;

        Console.WriteLine();
        Console.WriteLine($"capability age (money-path policy window: {window} days):");

        if (!report.IsAvailable)
        {
            Console.WriteLine(
                $"  UNKNOWN — {report.UnavailableReason} Nothing below is a staleness verdict; read " +
                "every age as unknown rather than as fresh.");
        }

        Console.WriteLine($"  {"CAPABILITY",-26} {"INTRODUCED",-12} {"AGE",-9} POLICY");

        var stale = new List<string>();

        foreach (var capability in CapabilityCatalog.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var introduced = report.IntroducedAt.TryGetValue(capability.Name, out var when)
                ? (DateTimeOffset?)when
                : null;

            var age = introduced is { } at ? $"{(now - at).Days}d" : "unknown";
            var date = introduced is { } start
                ? start.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "unknown";

            string policy;
            if (!capability.IsMoneyPath)
            {
                policy = "n/a (not money-path)";
            }
            else if (capability.IsFixture)
            {
                // The exemption is stated on the row rather than left blank: a money-path entry showing
                // no verdict at all is indistinguishable from a gate that failed to evaluate it.
                policy = "exempt (Capability.IsFixture)";
            }
            else if (introduced is not { } known)
            {
                policy = "UNKNOWN (age unreadable — not a pass)";
            }
            else if (CapabilityAge.IsStale(capability, known, now))
            {
                policy = $"STALE — past {window} days";
                stale.Add($"{capability.Name} ({(now - known).Days} days old)");
            }
            else
            {
                policy = $"ok ({window - (now - known).Days}d left)";
            }

            Console.WriteLine($"  {capability.Name,-26} {date,-12} {age,-9} {policy}");
        }

        Console.WriteLine();

        if (stale.Count > 0)
        {
            Console.WriteLine(
                $"capabilities: {stale.Count} money-path capability(ies) past the {window}-day window: " +
                string.Join(", ", stale) + ". These are short-lived by policy — each one is standing " +
                "risk on the books. Delete the capability and its gate, or record the extension in " +
                "ADR-028. CI fails on this (CapabilityAgeTests).");
        }
        else if (report.IsAvailable)
        {
            Console.WriteLine(
                $"capabilities: no money-path capability is past the {window}-day window.");
        }
    }

    /// <summary>
    /// One tenant's own state. This is the view the entitlement-collision message sends an operator
    /// to: it answers "did the earlier grant land, and when", which the flag table cannot.
    /// </summary>
    private static async Task ListForOrgAsync(DbContext db, Guid orgId, CancellationToken ct)
    {
        var entitlements = (await db.Database
            .SqlQuery<OrgEntitlementRow>(
                $"""
                 SELECT DISTINCT ON (capability) capability, granted, effective_at, actor
                 FROM entitlements
                 WHERE org_id = {orgId}
                   AND effective_at <= now()
                 ORDER BY capability, effective_at DESC, granted ASC, id DESC
                 """)
            .ToListAsync(ct))
            .ToDictionary(r => r.Capability, StringComparer.Ordinal);

        var rules = await db.Database
            .SqlQuery<OrgCohortRow>(
                $"""
                 SELECT capability, user_id
                 FROM capability_cohorts
                 WHERE org_id = {orgId}
                 ORDER BY capability, user_id NULLS FIRST
                 """)
            .ToListAsync(ct);

        Console.WriteLine();
        Console.WriteLine($"org {orgId}:");

        foreach (var capability in CapabilityCatalog.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var entitlement = entitlements.GetValueOrDefault(capability.Name) is { } row
                ? $"{(row.Granted ? "granted" : "revoked")} {row.EffectiveAt.ToUniversalTime():u} by {row.Actor}"
                : "(no entitlement event)";

            var mine = rules
                .Where(r => string.Equals(r.Capability, capability.Name, StringComparison.Ordinal))
                .ToList();
            var cohort = mine.Count == 0
                ? "none"
                : string.Join(", ", mine.Select(r => r.UserId is { } user ? $"user {user}" : "org-wide"));

            Console.WriteLine($"  {capability.Name,-26} entitlement: {entitlement}");
            Console.WriteLine($"  {string.Empty,-26} cohort:      {cohort}");
        }
    }

    /// <summary>
    /// Refuses a listing for an org that is not there.
    /// <para>
    /// <b>This is the read path's version of the FK the write paths get for free.</b> A capability
    /// with no rows for a real org prints "(no entitlement event)" — which is also exactly what a
    /// MISTYPED org id would print, for every capability at once. Since this listing is the remedy the
    /// entitlement-collision message names, a confidently wrong answer here would reopen the very loop
    /// that remedy closes: the operator reads "the grant did not land" and re-issues a grant that in
    /// fact succeeded. Wording mirrors the FK-violation branch in <see cref="Describe"/> on purpose,
    /// so the same mistake reads the same way whichever verb surfaced it.
    /// </para>
    /// <para>
    /// <c>orgs</c> is global-class — no <c>org_id</c>, no RLS, the org IS the tenant — so this read
    /// needs no context of its own and cannot itself return a misleading empty.
    /// </para>
    /// </summary>
    private static async Task AssertOrgExistsAsync(DbContext db, Guid orgId, CancellationToken ct)
    {
        var exists = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM orgs WHERE id = {orgId}""")
            .AnyAsync(ct);

        if (!exists)
        {
            throw new CapabilitiesRefusalException(
                $"capabilities: org {orgId} does not exist, so there is nothing to list for it. An org " +
                "with no capability rows and an org that is not there both look like " +
                "'(no entitlement event)', and reporting the second as the first is how a grant that " +
                "landed gets issued twice. Pass a real tenant id, or one of 'demo', 'cutover', 'load', " +
                "or 'scenario'.");
        }
    }

    // ── Writes ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The body of one platform-scoped transaction: read what is being replaced, write the state row,
    /// write the audit row, then <c>NOTIFY</c>. All four steps commit or roll back together.
    /// </summary>
    /// <param name="actor">
    /// Read once by the caller and threaded through, so the state row and its audit row carry the
    /// same identity structurally rather than by both happening to read the environment.
    /// </param>
    /// <returns>The transaction timestamp every row it wrote carries.</returns>
    private static async Task<DateTime> ApplyAsync(
        DbContext db, CapabilitiesAction action, string actor, CancellationToken ct)
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
                await WriteFlagAsync(db, action, now, actor, ct),
            CapabilitiesActionKind.Grant or CapabilitiesActionKind.Revoke =>
                WriteEntitlement(db, action, now, actor),
            CapabilitiesActionKind.CohortAdd => WriteCohort(db, action, now, actor),
            CapabilitiesActionKind.CohortRemove => await RemoveCohortAsync(db, action, ct),
            _ => throw new InvalidOperationException($"Unhandled action kind {action.Kind}."),
        };

        db.Add(new PlatformAuditEvent
        {
            Id = UuidV7.NewId(),
            OccurredAt = now,
            Actor = actor,
            Action = auditAction,
            Capability = capability,
            OrgId = action.OrgId,
            DetailJson = detail,
        });

        // ONE SaveChanges for the state row and its audit row: they are the same fact, and EF sends
        // them in one batch inside the executor's transaction. A failure on either — the entitlements
        // uniqueness index, the FK to orgs — takes both down, which is the atomicity the audit trail
        // is worth nothing without. CapabilitiesCommandTests asserts the two rows share an xmin,
        // which is the evidence that discriminates same-transaction from write-then-audit-separately.
        await db.SaveChangesAsync(ct);

        // Inside the transaction. See the class remarks.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_notify({CapabilityNotificationListener.Channel}, {capability})", ct);

        return now;
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
        DbContext db, CapabilitiesAction action, DateTime now, string actor, CancellationToken ct)
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
                UpdatedBy = actor,
            });
        }
        else
        {
            flag.Enabled = enabled;
            flag.UpdatedAt = now;
            flag.UpdatedBy = actor;
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
        DbContext db, CapabilitiesAction action, DateTime now, string actor)
    {
        var granted = action.Kind == CapabilitiesActionKind.Grant;

        db.Add(new Entitlement
        {
            Id = UuidV7.NewId(),
            OrgId = action.OrgId!.Value,
            Capability = action.Capability!,
            Granted = granted,
            EffectiveAt = now,
            Actor = actor,
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
        DbContext db, CapabilitiesAction action, DateTime now, string actor)
    {
        db.Add(new CapabilityCohort
        {
            Id = UuidV7.NewId(),
            Capability = action.Capability!,
            OrgId = action.OrgId!.Value,
            UserId = action.UserId,
            AddedAt = now,
            AddedBy = actor,
        });

        return (
            "cohort.add",
            Json(new Dictionary<string, object?>
            {
                ["user_id"] = action.UserId?.ToString(),
            }));
    }

    /// <summary>
    /// The exact inverse of <see cref="WriteCohort"/>: it removes the rule an <c>add</c> with the same
    /// arguments would have created — the org-wide rule (<c>user_id IS NULL</c>) when no
    /// <c>--user</c> was given, that user's rule when one was.
    /// <para>
    /// <b>This exists because <c>capability_cohorts</c> is the one platform table with no natural
    /// inverse and no uniqueness constraint.</b> Without it the CLI could create state it could
    /// neither show nor undo: a fat-fingered <c>cohort add</c> would silently duplicate and stay
    /// there. The table deliberately keeps its UPDATE/DELETE grants (membership is mutable by design,
    /// unlike entitlements), so this is an ordinary delete rather than a compensating event.
    /// </para>
    /// <para>
    /// EF loads the rows and deletes them by key, so a delete filtered to zero rows by RLS raises
    /// <see cref="DbUpdateConcurrencyException"/> instead of succeeding silently — the same reason the
    /// flag toggle avoids raw SQL. A request matching NOTHING is refused before anything is written,
    /// because the likely cause is a mistyped org, and "removed 0 rules" reported as success is how an
    /// operator concludes a cohort is gone when it is not.
    /// </para>
    /// </summary>
    private static async Task<(string Action, string Detail)> RemoveCohortAsync(
        DbContext db, CapabilitiesAction action, CancellationToken ct)
    {
        var capability = action.Capability!;
        var orgId = action.OrgId!.Value;

        var query = db.Set<CapabilityCohort>()
            .Where(c => c.Capability == capability && c.OrgId == orgId);

        // Two branches rather than `c.UserId == action.UserId`: with a null parameter that comparison
        // translates to `user_id = NULL`, which matches nothing, so a bare --org would silently remove
        // no rows instead of the org-wide rule it named.
        query = action.UserId is { } user
            ? query.Where(c => c.UserId == user)
            : query.Where(c => c.UserId == null);

        var rows = await query.ToListAsync(ct);
        if (rows.Count == 0)
        {
            var scope = action.UserId is { } named
                ? $"user {named} in org {orgId}"
                : $"org {orgId} (org-wide)";

            throw new CapabilitiesRefusalException(
                $"capabilities: no '{capability}' cohort rule exists for {scope}, so nothing was " +
                "removed and nothing was recorded. `cohort remove` is the exact inverse of `cohort " +
                "add`: without --user it targets the org-wide rule only. Run " +
                $"`capabilities list --org {orgId}` to see the rules that do exist.");
        }

        db.RemoveRange(rows);

        return (
            "cohort.remove",
            Json(new Dictionary<string, object?>
            {
                ["user_id"] = action.UserId?.ToString(),
                ["removed"] = rows.Count,
            }));
    }

    // ── Reporting ───────────────────────────────────────────────────────────────────────────────

    private static string Summarize(CapabilitiesAction action, DateTime now, string actor)
    {
        var capability = action.Capability;
        var org = action.OrgId;

        return action.Kind switch
        {
            CapabilitiesActionKind.FlagEnable =>
                $"capabilities: flag '{capability}' is now ENABLED deployment-wide (by {actor}).",
            CapabilitiesActionKind.FlagDisable =>
                $"capabilities: flag '{capability}' is now KILLED deployment-wide (by {actor}). An " +
                "explicit kill beats a cohort match; deleting the row would restore the registry default.",
            CapabilitiesActionKind.Grant =>
                $"capabilities: granted '{capability}' to org {org} effective {now.ToUniversalTime():u}.",
            CapabilitiesActionKind.Revoke =>
                $"capabilities: revoked '{capability}' from org {org} effective {now.ToUniversalTime():u} " +
                "(appended as a new event — entitlements are append-only).",
            CapabilitiesActionKind.CohortAdd => action.UserId is { } user
                ? $"capabilities: added user {user} in org {org} to the '{capability}' cohort."
                : $"capabilities: added org {org} to the '{capability}' cohort.",
            CapabilitiesActionKind.CohortRemove => action.UserId is { } removed
                ? $"capabilities: removed user {removed} in org {org} from the '{capability}' cohort."
                : $"capabilities: removed org {org}'s org-wide '{capability}' cohort rule.",
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
        // Checked BEFORE the Postgres unwrap, because a zero-row UPDATE/DELETE produces no server
        // error at all — there is nothing underneath to match on. This is the W3 case (RLS filtering
        // a write to zero rows, silently) surfacing as the one exception EF raises for it. Relying on
        // the batch's audit INSERT to raise 42501 first would be relying on EF's statement ordering.
        if (Find<DbUpdateConcurrencyException>(exception) is not null)
        {
            return
                "capabilities: the write matched no rows and was rolled back. On the platform tables " +
                "that means RLS filtered it out rather than rejecting it — an UPDATE or DELETE without " +
                "app.platform succeeds affecting zero rows instead of raising, which is exactly the " +
                "silence EF turns into this error. Nothing was written, including the audit row. If " +
                "this reproduces, the platform escape is not opening (PlatformScopedExecutor).";
        }

        var postgres = Find<PostgresException>(exception);
        if (postgres is null)
        {
            return null;
        }

        return postgres.SqlState switch
        {
            // 23505 on the entitlements uniqueness index: two grant events for one (org, capability)
            // at the same instant. `id` is NOT a usable tie-break (UUIDv7's low bits are random), so
            // the resolver could not order them — the index rejects the second rather than leaving
            // "current state" undefined. In practice this is a double-invocation, and the remedy names
            // the per-org listing because the flag table cannot answer whether the first one landed.
            PostgresErrorCodes.UniqueViolation
                when postgres.ConstraintName == "ux_entitlements_org_capability_effective_at" =>
                "capabilities: an entitlement event for this org and capability already exists at this " +
                "exact instant, so this one was rejected and nothing was written. Two events at one " +
                "timestamp have no defined order, which would leave current entitlement state " +
                "ambiguous. This is almost always a command run twice — run " +
                "`capabilities list --org <id>` to see whether the earlier one landed, and re-run only " +
                "if it really is a second change.",

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
    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static string Json(Dictionary<string, object?> detail) => JsonSerializer.Serialize(detail);

    /// <summary>
    /// A refusal decided inside the transaction, after a read the parser could not perform. Throwing
    /// is what rolls the transaction back, so a refused command leaves no state row and no audit row.
    /// </summary>
    private sealed class CapabilitiesRefusalException(string message) : Exception(message);

    private sealed record CapabilityCount(string Capability, long Count);

    private sealed record OrgEntitlementRow(string Capability, bool Granted, DateTime EffectiveAt, string Actor);

    private sealed record OrgCohortRow(string Capability, Guid? UserId);
}
