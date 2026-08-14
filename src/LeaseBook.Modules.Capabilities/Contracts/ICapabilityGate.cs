namespace LeaseBook.Modules.Capabilities.Contracts;

/// <summary>
/// The one question every caller asks (ADR-028). Two sources sit behind it — feature flags (ops
/// toggles, temporary, end in deletion) and entitlements (paid grants, durable) — with entitlement
/// gating first so a rollout can never hand out a paid feature.
/// <para>
/// <b>Pick the member by what a wrong answer costs.</b> A stale "on" that renders a menu item is a
/// cosmetic bug; a stale "on" that posts a journal entry is a fiduciary one. So the cheap member is
/// cache-served and the money-path member is not, and the difference is a deliberate cost:
/// <see cref="ResolveDurableAsync"/> pays one indexed read per run, which is negligible against the
/// per-item posting loop it guards and unaffordable on every UI render.
/// </para>
/// <para>
/// <b>Organization context is required by both.</b> With none they fault rather than answering "off": without
/// <c>app.org_id</c> RLS filters entitlements to zero rows, every paid capability reads as
/// unavailable, and the result is indistinguishable from a deliberate revoke with nothing logged
/// anywhere. That is the same fail-loud rule background jobs follow. The failure arrives on the
/// returned task, not before it, so composing these with <c>Task.WhenAll</c> behaves.
/// </para>
/// </summary>
public interface ICapabilityGate
{
    /// <summary>
    /// The current set, cache-served (30s TTL), for UI and non-money paths. Multi-step work resolves
    /// this <i>once</i> and passes the result explicitly down the call chain — see
    /// <see cref="CapabilitySet"/> — so that a flag flipped mid-run cannot make step 7 of a bulk run
    /// disagree with step 1. Do not re-ask mid-run.
    /// <para>
    /// <b>Asynchronous even though a cache hit needs no I/O</b>, because a miss needs a great deal of
    /// it. <c>Invalidate()</c> bumps one replica-wide generation, so a single <c>NOTIFY</c> makes
    /// every cached key stale at once and every caller misses simultaneously. A request-path refresh
    /// reads on its caller's ambient transaction, so it does not need a second pooled connection while
    /// the first is held. The member remains asynchronous because the miss still performs database I/O;
    /// the token is honoured throughout, so a disconnect stops paying for that refresh.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no ambient organization context.</exception>
    Task<CapabilitySet> GetCachedAsync(CancellationToken ct);

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
    /// There is no ambient organization context, or the ambient <c>app.org_id</c> is not the org being
    /// resolved.
    /// </exception>
    Task<CapabilitySet> ResolveDurableAsync(CancellationToken ct);
}
