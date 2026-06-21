namespace LeaseBook.Modules.Banking.Contracts;

/// <summary>
/// Consumer-owned write port (ADR-007 / P49 / P68): apply clearances to a set of register lines. The host
/// adapter delegates to Accounting's <c>ApplyClearances</c> command via <c>ISender</c> (upserts
/// <c>bank_line_status</c> → <c>cleared</c>); idempotent. Banking never writes <c>bank_line_status</c> directly.
/// </summary>
public interface IBankClearing
{
    Task ApplyClearancesAsync(IReadOnlyCollection<Guid> journalLineIds, CancellationToken ct);
}
