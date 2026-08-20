using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// One strategy per <see cref="Domain.RunType"/> (WP-2 = Rent, WP-3 = LateFee, WP-4 = Disbursement).
/// The <see cref="RunEngine"/> resolves the correct strategy by <see cref="Domain.RunType"/> and calls
/// <see cref="PreviewAsync"/> / <see cref="PlanAsync"/> without knowing the concrete type.
/// <para>
/// A strategy answers two questions and no others: <b>what would this run do</b>, and <b>what should
/// it do for these targets</b>. It posts nothing, records nothing, and catches nothing — the engine
/// owns the posting loop, the outcome-to-status mapping, item construction and persistence
/// (ADR-019 §4, amended 2026-08-09).
/// </para>
/// <para>
/// <b>State each rule once (ADR-019 §4c).</b> The two methods are two projections of one set of
/// rules, not two implementations of them. Give the strategy a private, pure <c>Decide(...)</c> over
/// already-fetched data and let both methods project its result — including the exclusion vocabulary,
/// where the machine code and the operator-facing sentence belong to the same construction.
/// <see cref="PlanAsync"/> must still read its own fresh data and hand it in; sharing the rules must
/// never become sharing the data. Written twice instead, the copies drift: the NC §42-46 clamp was
/// added to both paths by hand and only one of them was under test, so deleting the other left the
/// suite green while the preview offered a lease that was not yet chargeable.
/// </para>
/// </summary>
public interface IRunStrategy
{
    RunType RunType { get; }

    /// <summary>
    /// Returns what would be posted for <paramref name="period"/> — all eligible targets with amounts,
    /// already-done flags, and exclusion reasons. No mutations occur.
    /// <para>
    /// Returns a <see cref="StrategyPreview"/> rather than a <see cref="RunPreview"/>, and the
    /// difference is load-bearing: a preview carries the capability-version token the operator echoes
    /// back on confirm, which only <see cref="RunEngine.PreviewAsync"/> can resolve. A strategy that
    /// could return a <c>RunPreview</c> could return an unstamped one.
    /// </para>
    /// </summary>
    Task<StrategyPreview> PreviewAsync(RunPeriod period, CancellationToken ct);

    /// <summary>
    /// Decides what the run should do for each of <paramref name="selectedTargetIds"/>, as one
    /// <see cref="RunPlanItem"/> per target: post this intent, or record this target as skipped or
    /// excluded for this reason. Nothing is posted and nothing is persisted here.
    /// <para>
    /// <b>Capabilities are deliberately absent from this signature.</b> The freeze is an engine
    /// property — <see cref="RunEngine.ConfirmAsync"/> resolves the set exactly once, at its own
    /// entry, and both capability guards reject above this call — so a capability can decide whether a
    /// run happens and never what it produces. Passing the set down here would widen the surface that
    /// could re-resolve it without making any guarantee stronger; under the chunked run confirmation ADR-019's
    /// revisit trigger contemplates, the engine is what resumes and therefore what must carry the
    /// snapshot across a chunk boundary.
    /// </para>
    /// <para>
    /// It follows that a strategy must not reach a capability by another route either — no injected
    /// <c>ICapabilitySnapshot</c>, no collaborator that resolves one. Whoever first needs a capability
    /// inside a strategy has to state which of the two they are doing: gating whether a target is
    /// posted at all (allowed, and it belongs above this call), or deriving a posted value (forbidden
    /// by ADR-028 §4 and the money rule beneath it — money-affecting parameters live in
    /// <c>OrgSettings</c>, which is org-scoped, RLS'd, audited, seeded and golden-pinned).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<RunPlanItem>> PlanAsync(
        RunPeriod period,
        IReadOnlyList<Guid> selectedTargetIds,
        CancellationToken ct);
}
