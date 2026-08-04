using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// One strategy per <see cref="Domain.RunType"/> (WP-2 = Rent, WP-3 = LateFee, WP-4 = Disbursement).
/// The <see cref="RunEngine"/> resolves the correct strategy by <see cref="Domain.RunType"/> and calls
/// <see cref="PreviewAsync"/> / <see cref="ConfirmAsync"/> without knowing the concrete type.
/// </summary>
public interface IRunStrategy
{
    RunType RunType { get; }

    /// <summary>
    /// Returns a preview of what would be posted for <paramref name="period"/> — all eligible targets
    /// with amounts, already-done flags, and exclusion reasons. No mutations occur.
    /// </summary>
    Task<RunPreview> PreviewAsync(RunPeriod period, CancellationToken ct);

    /// <summary>
    /// Executes the run for the selected targets, posting via <paramref name="posting"/>, and returns
    /// the per-item outcomes as <see cref="BulkRunItem"/>s (not yet persisted — the engine does that).
    /// <para>
    /// Implementations must catch <c>DuplicateSourceRefException</c> per-item (→ Skipped) and the
    /// period-locked exception per-item (→ Excluded); no unhandled posting exception should escape.
    /// </para>
    /// <para>
    /// <paramref name="capabilities"/> is resolved ONCE at <see cref="RunEngine.ConfirmAsync"/> entry
    /// and frozen for the whole run. It is a parameter rather than an ambient service on purpose:
    /// ADR-019 contemplates chunked confirms, and under chunking a chunk boundary is a new
    /// transaction. An ambient lookup would silently lose the freeze there; a parameter cannot be lost
    /// without a signature change. Do not re-resolve inside an implementation, and do not inject the
    /// snapshot port into one.
    /// </para>
    /// <para>
    /// <b>What it may decide.</b> Reachability only (ADR-028): whether a posting path runs at all.
    /// It may never change the lines or amounts an existing business event produces, so no value read
    /// off it may become an argument to an Accounting command, business event, or posting-template
    /// input. Money-affecting parameters live in <c>OrgSettings</c>, which is org-scoped, RLS'd,
    /// audited, seeded and golden-pinned; capabilities are none of those things.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<BulkRunItem>> ConfirmAsync(
        BulkRun run,
        IReadOnlyList<Guid> selectedTargetIds,
        IBatchPosting posting,
        RunCapabilities capabilities,
        CancellationToken ct);
}
