namespace LeaseBook.Modules.Payments.Contracts;

public interface IPaymentLedger
{
    Task<Guid> RecordSettledReceiptAsync(Guid tenantId, Guid bankId, decimal amount, DateOnly date,
        string sourceRef, CancellationToken ct);
}

public sealed record PaymentEligibilityRequest(Guid TenantId, Guid BankId, DateOnly Date);
public interface IPaymentEligibility
{
    Task<IReadOnlyDictionary<PaymentEligibilityRequest, bool>> ReadAsync(
        IReadOnlyList<PaymentEligibilityRequest> requests, CancellationToken ct);
}
