namespace LeaseBook.Modules.Directory.Contracts;

/// <summary>
/// Consumer-owned read port (ADR-007 / P49) for owner financial figures owned by Accounting. The host
/// adapter delegates to the M1 <c>GetOwnerBalances</c> query via <c>ISender</c>. <b>Batch map only</b>
/// (owner id → figures), never per-id (M2-E12).
/// </summary>
public interface IOwnerFinancials
{
    Task<IReadOnlyDictionary<Guid, OwnerFinancialsRow>> BalancesAsync(CancellationToken ct);
}

/// <summary>An owner's operating (distributable cash equity) and held-deposit totals.</summary>
public sealed record OwnerFinancialsRow(decimal Operating, decimal Deposits)
{
    public decimal Total => Operating + Deposits;
}
