namespace LeaseBook.Modules.Directory.Contracts;

/// <summary>Consumer-owned read port (ADR-007) for per-bank uncleared-item counts owned by Accounting.
/// The host adapter delegates to GetBankBalances via ISender. Batch map only (bank id → uncleared count).</summary>
public interface IBankClearanceStatus
{
    Task<IReadOnlyDictionary<Guid, int>> UnclearedCountsAsync(CancellationToken ct);
}
