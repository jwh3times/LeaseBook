using LeaseBook.Modules.Capabilities.Contracts;
using Microsoft.EntityFrameworkCore;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Modules.Capabilities.Resolution;

/// <summary>
/// Reads the three capability tables in one pass and resolves a complete
/// <see cref="CapabilitySet"/> for a single <c>(org, user)</c> pair.
/// <para>
/// <b>It runs in either plane, and that is the point.</b> Under platform scope it is the body of the
/// out-of-band cache refresh. Without platform scope it still works for one org's OWN data —
/// <c>feature_flags</c> is globally readable, and <c>entitlements</c>/<c>capability_cohorts</c> admit
/// own-org reads through their <c>_org_read</c> policy under the ambient <c>app.org_id</c>. That is
/// what lets Task 6 resolve a money-path capability durably inside the request transaction, where
/// <see cref="IPlatformScope"/> cannot go (it opens its own transaction).
/// </para>
/// <para>
/// Every query carries an explicit <c>WHERE org_id = …</c>. The <c>_org_read</c> predicate is an
/// <c>OR</c> against a GUC, which is not sargable — Postgres applies it as a filter, never as an index
/// condition — so RLS contributes no selectivity here and narrowing has to be written out.
/// </para>
/// </summary>
public sealed class CapabilityStateReader(DbContext db)
{
    /// <summary>
    /// Resolves in whichever plane the ambient transaction is already in. See
    /// <see cref="ReadAsync(Guid, Guid?, bool, CancellationToken)"/> for the platform-scoped form.
    /// </summary>
    public Task<CapabilitySet> ReadAsync(Guid orgId, Guid? userId, CancellationToken ct) =>
        ReadAsync(orgId, userId, requirePlatformScope: false, ct);

    /// <param name="requirePlatformScope">
    /// True for out-of-band work that must be running under <c>app.platform</c>. A tenant-plane read
    /// with no org context does not raise — it silently returns zero rows, which an evaluator would
    /// map to "no entitlement" and therefore "off", disabling paid features with no error anywhere.
    /// Asserting the GUC turns that into a throw, mirroring the rule that a background job with
    /// missing org context fails rather than returning empty.
    /// </param>
    public async Task<CapabilitySet> ReadAsync(
        Guid orgId, Guid? userId, bool requirePlatformScope, CancellationToken ct)
    {
        if (orgId == Guid.Empty)
        {
            throw new ArgumentException(
                "Capability resolution requires a non-empty org id — resolving with no org context " +
                "would read zero entitlement rows and silently answer 'off' for every paid capability.",
                nameof(orgId));
        }

        if (requirePlatformScope)
        {
            await AssertPlatformScopeAsync(ct);
        }

        // feature_flags has no org_id — a flag is a property of the deployment. Read whole: it holds
        // one row per flagged capability, and rows for names no longer in the registry are ignored
        // below rather than being an error (a retired capability must not break a running host).
        var flags = await db.Database
            .SqlQuery<FlagRow>($"SELECT name, enabled FROM feature_flags")
            .ToListAsync(ct);

        // Current entitlement state is the LATEST row per (org, capability): the table is append-only
        // grant events, so "current" is a window read, not a column.
        //
        // The tie-break is fail-closed on purpose. `granted ASC` puts a revoke (false) ahead of a
        // grant (true) at the same effective_at, so a revoke wins any residual tie — the same
        // fail-closed principle the tenancy model uses everywhere. `id DESC` is only there to make
        // the ordering total for SQL's sake; it is NOT the semantic tie-break, because
        // Guid.CreateVersion7 carries random low bits and ids minted in the same millisecond sort
        // arbitrarily. ux_entitlements_org_capability_effective_at makes an exact tie impossible
        // today; the ordering stays fail-closed regardless of that index.
        var entitlements = await db.Database
            .SqlQuery<EntitlementRow>(
                $"""
                 SELECT DISTINCT ON (capability) capability, granted
                 FROM entitlements
                 WHERE org_id = {orgId}
                 ORDER BY capability, effective_at DESC, granted ASC, id DESC
                 """)
            .ToListAsync(ct);

        var cohorts = await ReadCohortsAsync(orgId, userId, ct);

        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        var flagsByName = flags.ToDictionary(f => f.Name, f => f.Enabled, StringComparer.Ordinal);
        var grantsByName = entitlements.ToDictionary(e => e.Capability, e => e.Granted, StringComparer.Ordinal);
        var cohortNames = cohorts.ToHashSet(StringComparer.Ordinal);

        // Registry-driven, not row-driven: CapabilitySet.From asserts completeness, so every
        // capability in source code must get a value even when it has no row anywhere.
        foreach (var capability in CapabilityCatalog.All)
        {
            var state = new CapabilityState(
                FlagEnabled: flagsByName.TryGetValue(capability.Name, out var enabled) ? enabled : null,
                HasGrant: grantsByName.TryGetValue(capability.Name, out var granted) && granted,
                CohortMatch: cohortNames.Contains(capability.Name));

            values[capability.Name] = CapabilityResolver.Resolve(capability, state);
        }

        return CapabilitySet.From(values, CapabilityVersion.Compute(values));
    }

    /// <summary>
    /// An org-level cohort row (<c>user_id IS NULL</c>) always applies. A user-level row applies only
    /// when there is an authenticated user — with none (Hangfire, the CLI, InvariantSweepRunner) it is
    /// deterministically NO match, never a null-propagating maybe. The two cases are separate SQL
    /// rather than one statement with a nullable parameter so that intent is readable at the call
    /// site instead of hiding in three-valued logic.
    /// </summary>
    private async Task<List<string>> ReadCohortsAsync(Guid orgId, Guid? userId, CancellationToken ct)
    {
        if (userId is { } user)
        {
            return await db.Database
                .SqlQuery<string>(
                    $"""
                     SELECT DISTINCT capability AS "Value"
                     FROM capability_cohorts
                     WHERE org_id = {orgId}
                       AND (user_id IS NULL OR user_id = {user})
                     """)
                .ToListAsync(ct);
        }

        return await db.Database
            .SqlQuery<string>(
                $"""
                 SELECT DISTINCT capability AS "Value"
                 FROM capability_cohorts
                 WHERE org_id = {orgId}
                   AND user_id IS NULL
                 """)
            .ToListAsync(ct);
    }

    private async Task AssertPlatformScopeAsync(CancellationToken ct)
    {
        var scoped = await db.Database
            .SqlQuery<bool>(
                $"""SELECT COALESCE(current_setting('app.platform', true) = 'on', false) AS "Value" """)
            .SingleAsync(ct);

        if (!scoped)
        {
            throw new InvalidOperationException(
                "The capability refresh must run under platform scope. Without it the read returns " +
                "zero rows instead of raising, and every paid capability would resolve to 'off' with " +
                "no error recorded anywhere.");
        }
    }

    private sealed record FlagRow(string Name, bool Enabled);

    private sealed record EntitlementRow(string Capability, bool Granted);
}
