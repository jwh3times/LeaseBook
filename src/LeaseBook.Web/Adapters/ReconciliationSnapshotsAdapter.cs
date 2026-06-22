using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Reporting.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007/016) for Reporting's <see cref="IReconciliationSnapshots"/> port. Delegates
/// to Accounting's <see cref="GetReconciliationHistory"/> query via <see cref="ISender"/>, filters to
/// finalized rows, and returns a batch map (bank-account id → latest finalized snapshot) so the
/// statement assembler can surface the "reconciles to" figure on the fiduciary panel without touching
/// Accounting tables directly.
/// </summary>
internal sealed class ReconciliationSnapshotsAdapter(ISender sender) : IReconciliationSnapshots
{
    public async Task<IReadOnlyDictionary<Guid, ReconciliationSnapshotRow>> GetLatestFinalizedAsync(
        CancellationToken ct)
    {
        var history = await sender.Query(new GetReconciliationHistory(), ct);

        // Keep only finalized rows; pick the latest per bank account (history is already newest-first).
        var latestByBank = history.Rows
            .Where(r => r.Status == "finalized" && r.FinalizedAt.HasValue)
            .GroupBy(r => r.BankAccountId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var row = g.First(); // already newest-first from the query
                    return new ReconciliationSnapshotRow(
                        row.BankAccountId, row.Year, row.Month,
                        row.StatementEndingBalance, row.FinalizedAt!.Value);
                });

        return latestByBank;
    }
}
