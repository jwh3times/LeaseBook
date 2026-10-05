namespace LeaseBook.Modules.Payments.Contracts;

public interface IPaymentLedger
{
    Task<Guid> RecordSettledReceiptAsync(Guid tenantId, Guid bankId, decimal amount, DateOnly date,
        string method, string sourceRef, CancellationToken ct);

    /// <summary>
    /// Posts the bank's return of a settled receipt as its linked reversal, dated <paramref name="date"/>.
    /// Accounting owns the guards (#490). A refusal is a result, not an exception: nothing was written,
    /// so the caller can record why on the same transaction.
    /// </summary>
    Task<PaymentReturnOutcome> ReturnSettledReceiptAsync(Guid receiptJournalId, DateOnly date,
        string sourceRef, string note, CancellationToken ct);

    /// <summary>
    /// Posts one payout as a batch on the bank date, completely or not at all. A refusal by an
    /// accounting rule is a result and leaves this transaction usable, so the caller can record it.
    /// </summary>
    Task<SettlementOutcome> PostSettlementAsync(Guid bankId, DateOnly bankDate, string payoutReference,
        IReadOnlyList<SettlementLineRequest> lines, CancellationToken ct);
}

/// <summary>
/// One line of a payout to post (ADR-053). <see cref="Kind"/> is a <see cref="Domain.SettlementItemKinds"/>
/// value. A payment carries the tenant, the ledger amount and the method; a return carries the receipt
/// it reverses. <see cref="FeeDifference"/> is what the bank received short of the ledger amount:
/// positive a shortfall, negative a surplus. A standalone fee is all fee difference.
/// </summary>
public sealed record SettlementLineRequest(string Item, string Kind, Guid? TenantId, decimal Amount,
    string Method, Guid? ReceiptJournalId, decimal FeeDifference);

/// <summary>An entry a payout posted. <see cref="Kind"/> is <c>Receipt</c>, <c>Return</c> or <c>FeeDifference</c>.</summary>
public sealed record SettlementPostingResult(string Item, string Kind, Guid EntryId);

/// <summary>Every posting of the payout, or the stable reason nothing was posted and the item it stopped on.</summary>
public sealed record SettlementOutcome(IReadOnlyList<SettlementPostingResult> Postings, string? Refusal, string? RefusedItem);

/// <summary>Exactly one is set: the reversal's journal id, or a stable <c>return_*</c> refusal code.</summary>
public sealed record PaymentReturnOutcome(Guid? JournalId, string? Refusal);

/// <summary>
/// The organization's convenience-fee rule for each online payment method (ADR-053), keyed by the
/// <see cref="Domain.PaymentMethods"/> value. Read on the ambient organization transaction.
/// </summary>
public interface IPaymentFeeRules
{
    Task<IReadOnlyDictionary<string, Domain.ConvenienceFeeRule>> ReadAsync(CancellationToken ct);
}

public sealed record PaymentEligibilityRequest(Guid TenantId, Guid BankId, DateOnly Date);
public interface IPaymentEligibility
{
    Task<IReadOnlyDictionary<PaymentEligibilityRequest, bool>> ReadAsync(
        IReadOnlyList<PaymentEligibilityRequest> requests, CancellationToken ct);
}
