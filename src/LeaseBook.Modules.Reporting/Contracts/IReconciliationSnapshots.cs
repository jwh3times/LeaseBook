namespace LeaseBook.Modules.Reporting.Contracts;

/// <summary>
/// Consumer-owned read port (ADR-007/016): the latest <em>finalized</em> reconciliation snapshot per
/// bank account. The host adapter delegates to Accounting's <c>GetReconciliationHistory</c> query via
/// <c>ISender</c> and filters to finalized rows. Used by <c>StatementAssembler</c> to surface the
/// "reconciles to" bank figure on the fiduciary panel without the Reporting module touching Accounting
/// tables directly. Batch map: bank-account id → snapshot.
/// </summary>
public interface IReconciliationSnapshots
{
    /// <summary>
    /// Returns the latest finalized reconciliation per bank account (bank-account id → snapshot).
    /// Accounts with no finalized reconciliation are absent from the map.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ReconciliationSnapshotRow>> GetLatestFinalizedAsync(CancellationToken ct);
}

/// <summary>A single finalized reconciliation snapshot for one bank account.</summary>
/// <param name="BankAccountId">The reconciled bank account.</param>
/// <param name="Year">Statement period year.</param>
/// <param name="Month">Statement period month (1–12).</param>
/// <param name="StatementEndingBalance">The bank-statement ending balance as reconciled.</param>
/// <param name="FinalizedAt">UTC timestamp when the reconciliation was finalized.</param>
public sealed record ReconciliationSnapshotRow(
    Guid BankAccountId,
    int Year,
    int Month,
    decimal StatementEndingBalance,
    DateTime FinalizedAt);
