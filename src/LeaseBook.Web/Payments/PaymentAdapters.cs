using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Payments;

internal sealed class PaymentLedgerAdapter(ISender sender) : IPaymentLedger
{
    public async Task<Guid> RecordSettledReceiptAsync(Guid tenantId, Guid bankId, decimal amount,
        DateOnly date, string sourceRef, CancellationToken ct) =>
        (await sender.Send(new RecordPayment(tenantId, amount, date, "ach", bankId,
            "Simulated tenant payment", sourceRef), ct)).EntryId;
}

internal sealed class PaymentEligibilityAdapter(ISender sender) : IPaymentEligibility
{
    public async Task<IReadOnlyDictionary<PaymentEligibilityRequest, bool>> ReadAsync(
        IReadOnlyList<PaymentEligibilityRequest> requests, CancellationToken ct)
    {
        var result = await sender.Query(new GetPaymentEligibility(requests
            .Select(x => new PaymentEligibilityKey(x.TenantId, x.BankId, x.Date)).ToArray()), ct);
        return result.ToDictionary(x => new PaymentEligibilityRequest(x.Key.TenantId, x.Key.BankId, x.Key.Date), x => x.Value);
    }
}
