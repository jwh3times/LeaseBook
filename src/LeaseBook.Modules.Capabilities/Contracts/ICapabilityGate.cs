using LeaseBook.Modules.Capabilities.Registry;

namespace LeaseBook.Modules.Capabilities.Contracts;

/// <summary>
/// The one question every caller asks (ADR-028). Two sources sit behind it — feature flags (ops
/// toggles, temporary, end in deletion) and entitlements (paid grants, durable) — with entitlement
/// gating first so a rollout can never hand out a paid feature.
/// <para>
/// <b>Pick the member by what a wrong answer costs.</b> A stale "on" that renders a menu item is a
/// cosmetic bug; a stale "on" that posts a journal entry is a fiduciary one. So the two cheap members
/// are cache-served and the money-path member is not, and the difference is a deliberate cost:
/// <see cref="ResolveDurableAsync"/> pays one indexed read per run, which is negligible against the
/// per-item posting loop it guards and unaffordable on every UI render.
/// </para>
/// <para>
/// <b>Org context is required by all three.</b> With none they throw rather than answering "off":
/// without <c>app.org_id</c> RLS filters entitlements to zero rows, every paid capability reads as
/// unavailable, and the result is indistinguishable from a deliberate revoke with nothing logged
/// anywhere. That is the same fail-loud rule background jobs follow.
/// </para>
/// </summary>
public interface ICapabilityGate
{
    /// <summary>
    /// Cache-served (30s TTL). For UI and non-money paths.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no ambient org context.</exception>
    bool IsEnabled(Capability capability);

    /// <summary>
    /// The current set, cache-served, to be frozen by the caller. Multi-step work resolves this
    /// <i>once</i> and passes the result explicitly down the call chain — see
    /// <see cref="CapabilitySet"/> — so that a flag flipped mid-run cannot make step 7 of a bulk run
    /// disagree with step 1. Do not re-ask mid-run.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no ambient org context.</exception>
    CapabilitySet Snapshot();

    /// <summary>
    /// Transaction-consistent read for MONEY paths. Must be called inside the ambient transaction —
    /// every authenticated request already is one, and jobs get one from
    /// <c>OrgScopedExecutor</c>.
    /// <para>
    /// Not cache-served, for two reasons. A kill switch that only takes effect after a 30-second TTL
    /// does not work during the incident you flipped it for; and a cached read is not consistent with
    /// the rows being written in the same transaction, so a run could post under capabilities that no
    /// longer hold.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// There is no ambient org context, or the ambient <c>app.org_id</c> is not the org being
    /// resolved.
    /// </exception>
    Task<CapabilitySet> ResolveDurableAsync(CancellationToken ct);
}
