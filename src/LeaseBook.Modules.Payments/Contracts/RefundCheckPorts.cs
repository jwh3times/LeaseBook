namespace LeaseBook.Modules.Payments.Contracts;

/// <summary>A tenant's positive held balance in one bucket (deposit: bank + property + owner; prepayment: bank).</summary>
public sealed record RefundableFunds(
    Guid TenantId, string Source, Guid BankAccountId, Guid? PropertyId, Guid? OwnerId, decimal Held);

/// <summary>Names one held bucket when a tenant's funds sit in more than one.</summary>
public sealed record RefundFundsBucket(Guid BankAccountId, Guid? PropertyId, Guid? OwnerId);

public sealed record RefundPostingRequest(
    Guid TenantId, string Source, decimal Amount, DateOnly Date, Guid CheckId, int CheckNumber,
    RefundFundsBucket? Bucket, string? InternalNote);

public sealed record RefundPosting(Guid EntryId, Guid BankAccountId);

/// <summary><c>outstanding</c>, <c>cleared</c>, <c>reconciled</c> or <c>voided</c>, derived by Accounting.</summary>
public sealed record RefundLedgerStatus(string Status, Guid? VoidEntryId);

/// <summary>
/// Payments' view of the journal for refund checks (#473, ADR-007). Accounting derives the bank from
/// the held liability, owns the posted text, guards the void, and derives clearance status; Payments
/// keeps only the check record. Every call rides the ambient organization transaction.
/// </summary>
public interface IRefundCheckLedger
{
    Task<IReadOnlyList<RefundableFunds>> GetRefundableAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken ct);

    Task<RefundPosting> PostAsync(RefundPostingRequest request, CancellationToken ct);

    /// <summary>Posts the guarded void; returns the reversal entry id.</summary>
    Task<Guid> VoidAsync(Guid entryId, string reason, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, RefundLedgerStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> entryIds, CancellationToken ct);
}
