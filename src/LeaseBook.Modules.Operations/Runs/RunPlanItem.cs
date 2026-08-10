using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// One decision a strategy has made about one target, before anything is posted (ADR-019 §4, amended
/// 2026-08-09). A plan is the whole of what <see cref="IRunStrategy.PlanAsync"/> returns; the
/// <see cref="RunEngine"/> drives it, posts what asks to be posted, and records every row.
/// <para>
/// <b>Why a plan rather than a posting loop.</b> Before this existed each strategy ran its own loop,
/// its own catch ladder, its own <c>BulkRunItem</c> construction and its own serialization — 60 lines
/// identical across all three, and a fourth run type had to reproduce them from memory. None of that
/// is domain knowledge. What IS strategy-specific is which targets are eligible, what each posting is
/// worth, and what the run log should say about it, and that is exactly what a plan item carries.
/// </para>
/// <para>
/// <b>Closed hierarchy.</b> The base constructor is <c>private protected</c>, so the only two cases
/// are the two below and the engine's switch over them is exhaustive in practice as well as on paper.
/// A third case is a deliberate edit to this file, not something a caller can introduce.
/// </para>
/// </summary>
public abstract record RunPlanItem
{
    private protected RunPlanItem(RunTargetKind targetKind, Guid targetId)
    {
        TargetKind = targetKind;
        TargetId = targetId;
    }

    /// <summary>What kind of entity this item is about — a lease or an owner.</summary>
    public RunTargetKind TargetKind { get; }

    /// <summary>The lease id or owner id this item is about.</summary>
    public Guid TargetId { get; }
}

/// <summary>
/// "Post this intent." The engine posts it through <see cref="IBatchPosting"/> and records the row
/// the <see cref="PostOutcome"/> implies — including the refusal statuses, which are per-item and
/// never abort the run.
/// </summary>
/// <param name="Intent">What to post. The engine never constructs or inspects one.</param>
/// <param name="Amount">
/// The amount to record on the item when the posting succeeds. Refused items record <c>0</c>, matching
/// the pre-plan behaviour: nothing was posted, so nothing counts toward the run total.
/// </param>
/// <param name="PostedDetail">
/// The strategy's own account of a successful posting, for <c>snapshot_json</c>. The engine adds
/// <c>entryId</c> — and <c>feeEntryId</c> when the outcome carries one — because only it has the
/// outcome; everything else is the strategy's vocabulary and the engine does not read it.
/// </param>
/// <param name="RefusedDetail">
/// The same, for a refusal. The engine adds <c>reason</c>, which is derived from the
/// <see cref="PostStatus"/> and is therefore identical across strategies.
/// </param>
public sealed record PlannedPosting(
    RunTargetKind TargetKind,
    Guid TargetId,
    RunIntent Intent,
    decimal Amount,
    IReadOnlyDictionary<string, object?> PostedDetail,
    IReadOnlyDictionary<string, object?> RefusedDetail)
    : RunPlanItem(TargetKind, TargetId);

/// <summary>
/// "Do not post this target." The strategy decided on data it already had — the target is not in the
/// period's schedule, its rent is zero, the charge already exists — so no posting is attempted and no
/// refusal can arise.
/// </summary>
/// <param name="Status">
/// <see cref="RunItemStatus.Skipped"/> or <see cref="RunItemStatus.Excluded"/>. Both occur:
/// "already charged in this period" is a skip (the work is done), while "no policy resolved" is an
/// exclusion (the work cannot be done). <see cref="RunItemStatus.Posted"/> is not a legal value here
/// and the engine rejects it — an item that posts is a <see cref="PlannedPosting"/>.
/// </param>
/// <param name="Detail">
/// The complete <c>snapshot_json</c> payload. Unlike a posting there is no outcome to wait for, so the
/// engine adds nothing.
/// </param>
public sealed record PlannedExclusion(
    RunTargetKind TargetKind,
    Guid TargetId,
    RunItemStatus Status,
    IReadOnlyDictionary<string, object?> Detail)
    : RunPlanItem(TargetKind, TargetId);
