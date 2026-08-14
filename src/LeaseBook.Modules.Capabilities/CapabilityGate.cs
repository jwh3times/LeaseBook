using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Capabilities.Resolution;
using LeaseBook.SharedKernel.Tenancy;

namespace LeaseBook.Modules.Capabilities;

/// <summary>
/// The seam's only implementation (ADR-028). Scoped: it binds the singleton cache and the scoped
/// state reader to whatever <c>(org, user)</c> the ambient unit of work is running as.
/// <para>
/// <b>It deliberately does not take <see cref="IPlatformScope"/>.</b> That port opens a transaction,
/// and <see cref="ResolveDurableAsync"/> is called from inside one — <c>OrgContextMiddleware</c>
/// opens a transaction for every authenticated request — so reaching for it would throw
/// "transaction already started" on the money path. It is not needed either: <c>feature_flags</c> is
/// organization-readable, and an org's own <c>entitlements</c> and <c>capability_cohorts</c> rows are
/// readable under the ambient <c>app.org_id</c> through each table's <c>_org_read</c> policy. A
/// organization-plane read of an organization's own capability state is legitimate and needs no escape.
/// </para>
/// <para>
/// It also does not reimplement resolution. Order lives in <see cref="CapabilityResolver"/> and is
/// reached through <see cref="CapabilityStateReader"/> on both paths, so the cheap answer and the
/// durable answer can differ only in <i>when</i> the state was read, never in how it was interpreted.
/// </para>
/// </summary>
internal sealed class CapabilityGate(
    CapabilityCache cache,
    CapabilityStateReader reader,
    ITenantContext tenant,
    IActorContext actor) : ICapabilityGate
{
    /// <inheritdoc />
    /// <remarks>
    /// Not memoized per scope. The contract is "the current cached set", so a long-lived scope (a
    /// job, a bulk run) still observes the 30-second TTL rather than pinning one answer for its whole
    /// life. Callers that need a frozen set freeze it by holding the reference — that is what makes
    /// <see cref="CapabilitySet"/> a value handed down the call chain instead of an ambient service
    /// re-asked at each step.
    /// </remarks>
    public async Task<CapabilitySet> GetCachedAsync(CancellationToken ct)
    {
        var orgId = RequireOrg();

        return await cache.GetAsync(
            orgId,
            actor.UserId,
            token => reader.ReadAsync(orgId, actor.UserId, token),
            ct);
    }

    /// <inheritdoc />
    public async Task<CapabilitySet> ResolveDurableAsync(CancellationToken ct) =>
        // The three-argument overload: read on the AMBIENT transaction, on the organization plane. It
        // asserts that app.org_id matches the org being resolved and throws when it does not, which
        // is the guard that keeps a mis-scoped call from returning a plausible "not entitled".
        await reader.ReadAsync(RequireOrg(), actor.UserId, ct);

    // Both members are `async` rather than expression-bodied pass-throughs purely so that a missing
    // organization context FAULTS the returned task instead of throwing before the task is handed back. The
    // difference is invisible under a bare await and load-bearing under Task.WhenAll — which is how
    // a run engine composing several of these would call them.

    private Guid RequireOrg() =>
        tenant.OrgId is { } orgId && orgId != Guid.Empty
            ? orgId
            : throw new InvalidOperationException(
                "Capability resolution requires organization context. Resolving without it would read zero " +
                "entitlement rows and answer 'off' for every paid capability — indistinguishable " +
                "from a deliberate revoke, and recorded nowhere. Establish context first: HTTP " +
                "requests get it from OrgContextMiddleware, jobs and the CLI from OrgScopedExecutor.");
}
