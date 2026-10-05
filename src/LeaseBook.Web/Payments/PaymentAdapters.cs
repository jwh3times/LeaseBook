using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Payments;

internal sealed class PaymentLedgerAdapter(ISender sender) : IPaymentLedger
{
    public async Task<Guid> RecordSettledReceiptAsync(Guid tenantId, Guid bankId, decimal amount,
        DateOnly date, string method, string sourceRef, CancellationToken ct) =>
        (await sender.Send(new RecordPayment(tenantId, amount, date, method, bankId,
            "Simulated tenant payment", sourceRef), ct)).EntryId;

    public async Task<PaymentReturnOutcome> ReturnSettledReceiptAsync(Guid receiptJournalId, DateOnly date,
        string sourceRef, string note, CancellationToken ct)
    {
        try
        {
            return new((await sender.Send(new ReturnTenantPayment(receiptJournalId, date, sourceRef, note), ct)).EntryId, null);
        }
        // Each of these is thrown before the posting service adds anything to the unit of work, so the
        // caller may keep using this transaction to record the refusal. Any other failure propagates
        // and rolls the request back.
        catch (AccountingDomainException e) when (e.Code is "return_prepayment_consumed"
            or "return_owner_funds_disbursed" or "return_precedes_receipt" or "period_closed"
            or "account_period_locked")
        {
            return new(null, e.Code.StartsWith("return_", StringComparison.Ordinal) ? e.Code : "return_period_locked");
        }
    }
}

internal sealed class PaymentFeeRulesAdapter(ISender sender) : IPaymentFeeRules
{
    public async Task<IReadOnlyDictionary<string, ConvenienceFeeRule>> ReadAsync(CancellationToken ct)
    {
        // A tenant may be the caller, and a portal persona holds no write grant on the settings row, so
        // this read never creates it: a missing row resolves to the defaults, which charge nothing.
        var settings = await sender.Query(new GetOrgSettings(CreateIfMissing: false), ct);
        return new Dictionary<string, ConvenienceFeeRule>
        {
            [PaymentMethods.Card] = new(settings.CardFeeRateBps, settings.CardFeeFixed, settings.CardFeeCap),
            [PaymentMethods.Ach] = new(settings.AchFeeRateBps, settings.AchFeeFixed, settings.AchFeeCap),
        };
    }
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
