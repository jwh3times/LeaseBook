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
    /// </summary>
    Task<IReadOnlyList<BulkRunItem>> ConfirmAsync(
        BulkRun run,
        IReadOnlyList<Guid> selectedTargetIds,
        IBatchPosting posting,
        CancellationToken ct);
}
