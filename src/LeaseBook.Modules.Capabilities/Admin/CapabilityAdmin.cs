using System.Globalization;
using System.Text.Json;
using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Capabilities.Domain;
using LeaseBook.SharedKernel;
using Microsoft.EntityFrameworkCore;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Modules.Capabilities.Admin;

/// <summary>
/// The write path for platform capability state. See <see cref="ICapabilityAdmin"/> for the contract;
/// this file is the one place the facts behind it live.
/// <para>
/// The shape every member shares is <see cref="ApplyAsync"/>: open platform scope, take the timestamp
/// from Postgres, perform the mutation, append the audit row, one <c>SaveChanges</c>, then
/// <c>NOTIFY</c> — all inside one transaction. Each member supplies only the mutation.
/// </para>
/// </summary>
internal sealed class CapabilityAdmin(
    DbContext db, IPlatformScope platform, IOrgMembership orgMembership) : ICapabilityAdmin
{
    public Task<DateTime> EnableFlagAsync(string capability, string actor, CancellationToken ct = default) =>
        ApplyAsync(capability, orgId: null, actor, (now, token) => WriteFlagAsync(capability, true, now, actor, token), ct);

    public Task<DateTime> DisableFlagAsync(string capability, string actor, CancellationToken ct = default) =>
        ApplyAsync(capability, orgId: null, actor, (now, token) => WriteFlagAsync(capability, false, now, actor, token), ct);

    public Task<DateTime> ClearFlagAsync(string capability, string actor, CancellationToken ct = default) =>
        ApplyAsync(capability, orgId: null, actor, (_, token) => ClearFlagAsync(capability, token), ct);

    public Task<DateTime> GrantAsync(
        string capability, Guid orgId, string actor, CancellationToken ct = default) =>
        ApplyAsync(capability, orgId, actor,
            (now, _) => Task.FromResult(WriteEntitlement(capability, orgId, true, now, actor)), ct);

    public Task<DateTime> RevokeAsync(
        string capability, Guid orgId, string actor, CancellationToken ct = default) =>
        ApplyAsync(capability, orgId, actor,
            (now, _) => Task.FromResult(WriteEntitlement(capability, orgId, false, now, actor)), ct);

    public Task<DateTime> AddToCohortAsync(
        string capability, Guid orgId, Guid? userId, string actor, CancellationToken ct = default) =>
        ApplyAsync(capability, orgId, actor,
            (now, token) => WriteCohortAsync(capability, orgId, userId, now, actor, token), ct);

    public Task<DateTime> RemoveFromCohortAsync(
        string capability, Guid orgId, Guid? userId, string actor, CancellationToken ct = default) =>
        ApplyAsync(capability, orgId, actor,
            (_, token) => RemoveCohortAsync(capability, orgId, userId, token), ct);

    /// <summary>
    /// The body of one platform-scoped transaction: perform the mutation, write the audit row, then
    /// <c>NOTIFY</c>. All of it commits or rolls back together.
    /// </summary>
    /// <returns>The transaction timestamp every row it wrote carries.</returns>
    private Task<DateTime> ApplyAsync(
        string capability,
        Guid? orgId,
        string actor,
        Func<DateTime, CancellationToken, Task<(string Action, string Detail)>> mutate,
        CancellationToken ct)
    {
        RequireWritableCapability(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        // The scope is opened here, not by the caller. It is the fact most costly to forget — without
        // app.platform, RLS filters the write to zero rows rather than raising — and opening it here
        // also means this path cannot run inside a request transaction, because a scoped unit of work
        // refuses to nest. See the note on ICapabilityAdmin.
        return platform.RunAsync(async () =>
        {
            // One timestamp for every row this transaction writes, read from POSTGRES rather than the
            // app clock. now() is transaction start time, so effective_at can never land ahead of the
            // clock a later resolver compares it against — the resolver's `effective_at <= now()`
            // makes a future-dated row pending, and clock skew would otherwise make a grant invisible
            // for the length of that skew with nothing to show for it.
            var now = await db.Database.SqlQuery<DateTime>($"""SELECT now() AS "Value" """).SingleAsync(ct);

            var (auditAction, detail) = await mutate(now, ct);

            db.Add(new PlatformAuditEvent
            {
                Id = UuidV7.NewId(),
                OccurredAt = now,
                Actor = actor,
                Action = auditAction,
                Capability = capability,
                OrgId = orgId,
                DetailJson = detail,
            });

            // ONE SaveChanges for the state row and its audit row: they are the same fact, and EF
            // sends them in one batch inside the scope's transaction. A failure on either — the
            // entitlements uniqueness index, the FK to orgs — takes both down, which is the atomicity
            // the audit trail is worth nothing without.
            await db.SaveChangesAsync(ct);

            // Inside the transaction: Postgres queues notifications and delivers them after commit,
            // so a listener woken by this is guaranteed to see the row it was told about. Issued after
            // the commit instead, that ordering guarantee is gone and the race returns.
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_notify({CapabilityNotifications.Channel}, {capability})", ct);

            return now;
        }, ct);
    }

    /// <summary>
    /// The invariant half of the registry check. The CLI verb refuses the same two cases earlier and
    /// with a better message — it can list the known names at the moment of the typo — but that check
    /// belongs to one entry point, and this rule has to hold for every caller.
    /// </summary>
    private static void RequireWritableCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        if (!CapabilityCatalog.TryGet(capability, out var entry))
        {
            throw new CapabilityRefusedException(
                $"capabilities: '{capability}' is not in the capability registry, so no state row and " +
                "no audit event were written. State is only ever written for a capability the code " +
                "declares; a row naming an unknown capability would be inert and would fail startup " +
                "validation for every replica.");
        }

        if (entry.IsFixture)
        {
            throw new CapabilityRefusedException(
                $"capabilities: '{capability}' is a fixture capability and is exempt from operator " +
                "control, so nothing was written. Fixtures exist to keep the registry's own gates " +
                "honest and must resolve identically everywhere.");
        }
    }

    /// <summary>
    /// Upserts the flag through EF rather than <c>INSERT … ON CONFLICT</c>, because an organization-plane
    /// UPDATE here is fail-closed but SILENT: RLS filters the target rows through the write policy's
    /// <c>USING</c>, so the statement succeeds affecting zero rows and only INSERT raises 42501. EF's
    /// update path compares the affected-row count against what it expected and throws
    /// <see cref="DbUpdateConcurrencyException"/> on the mismatch, turning that silence into a failure
    /// without a hand-written row-count assertion. Any raw upsert here would have to assert the count.
    /// <para>
    /// The previous value is captured because a flag is MUTABLE state: unlike an entitlement, whose
    /// history is the table, a flag's prior value is destroyed by the write. If the audit row does not
    /// carry it, it is gone.
    /// </para>
    /// </summary>
    private async Task<(string Action, string Detail)> WriteFlagAsync(
        string capability, bool enabled, DateTime now, string actor, CancellationToken ct)
    {
        var flag = await db.Set<FeatureFlag>().SingleOrDefaultAsync(f => f.Name == capability, ct);
        bool? previous = flag?.Enabled;

        if (flag is null)
        {
            db.Add(new FeatureFlag
            {
                Name = capability,
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

    private async Task<(string Action, string Detail)> ClearFlagAsync(string capability, CancellationToken ct)
    {
        var flag = await db.Set<FeatureFlag>().SingleOrDefaultAsync(f => f.Name == capability, ct);

        if (flag is null)
        {
            throw new CapabilityRefusedException(
                $"capabilities: no explicit flag override exists for '{capability}', so nothing was cleared " +
                "and nothing was recorded. Resolution is already using cohort state and the registry default.");
        }

        var previous = flag.Enabled;
        db.Remove(flag);

        return ("flag.clear", Json(new Dictionary<string, object?> { ["previous"] = previous }));
    }

    /// <summary>
    /// Appends one grant EVENT. There is no <c>revoked_at</c> to update: a revoke is a new row with
    /// <c>granted = false</c>, and current state is the latest row per <c>(org, capability)</c>. The
    /// table has no UPDATE or DELETE grant in either plane, so an append is the only shape available.
    /// </summary>
    private (string Action, string Detail) WriteEntitlement(
        string capability, Guid orgId, bool granted, DateTime now, string actor)
    {
        db.Add(new Entitlement
        {
            Id = UuidV7.NewId(),
            OrgId = orgId,
            Capability = capability,
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

    private async Task<(string Action, string Detail)> WriteCohortAsync(
        string capability, Guid orgId, Guid? userId, DateTime now, string actor, CancellationToken ct)
    {
        if (userId is { } user && !await orgMembership.IsUserInOrgAsync(user, orgId, ct))
        {
            // asp_net_users is RLS-exempt — login precedes organization context — so nothing in the database
            // stops a rule naming another organization's user. Both the id and the org are load-bearing.
            throw new CapabilityRefusedException(
                $"capabilities: user {user} does not exist in org {orgId}, so no cohort rule " +
                "and no audit event were written. asp_net_users is RLS-exempt; cohort adds must " +
                "validate the user against the explicitly named org.");
        }

        db.Add(new CapabilityCohort
        {
            Id = UuidV7.NewId(),
            Capability = capability,
            OrgId = orgId,
            UserId = userId,
            AddedAt = now,
            AddedBy = actor,
        });

        return ("cohort.add", Json(new Dictionary<string, object?> { ["user_id"] = userId?.ToString() }));
    }

    /// <summary>
    /// The exact inverse of <see cref="WriteCohortAsync"/>: it removes the rule an add with the same
    /// arguments would have created — the org-wide rule (<c>user_id IS NULL</c>) when no user was
    /// given, that user's rule when one was.
    /// <para>
    /// EF loads the rows and deletes them by key, so a delete filtered to zero rows by RLS raises
    /// <see cref="DbUpdateConcurrencyException"/> instead of succeeding silently — the same reason the
    /// flag toggle avoids raw SQL. A request matching NOTHING is refused before anything is written,
    /// because the likely cause is a mistyped org, and "removed 0 rules" reported as success is how an
    /// operator concludes a cohort is gone when it is not.
    /// </para>
    /// </summary>
    private async Task<(string Action, string Detail)> RemoveCohortAsync(
        string capability, Guid orgId, Guid? userId, CancellationToken ct)
    {
        var query = db.Set<CapabilityCohort>()
            .Where(c => c.Capability == capability && c.OrgId == orgId);

        // Two branches rather than `c.UserId == userId`: with a null parameter that comparison
        // translates to `user_id = NULL`, which matches nothing, so a bare org would silently remove
        // no rows instead of the org-wide rule it named.
        query = userId is { } user
            ? query.Where(c => c.UserId == user)
            : query.Where(c => c.UserId == null);

        var rows = await query.ToListAsync(ct);
        if (rows.Count == 0)
        {
            var scope = userId is { } named
                ? $"user {named} in org {orgId}"
                : $"org {orgId} (org-wide)";

            throw new CapabilityRefusedException(
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
                ["user_id"] = userId?.ToString(),
                ["removed"] = rows.Count,
            }));
    }

    private static string Json(Dictionary<string, object?> detail) => JsonSerializer.Serialize(detail);
}
