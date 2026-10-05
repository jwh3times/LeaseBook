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
}

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
