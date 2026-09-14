using System.Diagnostics.CodeAnalysis;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;

namespace LeaseBook.Modules.Accounting.Features.Posting;

/// <summary>Builds the database-independent posting template for a bulk-run accounting event.</summary>
internal static class RunEventEntryBuilder
{
    public static bool TryBuild(
        AccountingEvent businessEvent,
        [NotNullWhen(true)] out PostEntryRequest? request)
    {
        request = businessEvent switch
        {
            RentCharged e => new PostEntryRequest(e.Date, "RentCharged", null, e.Description, e.SourceRef,
                [
                    new(AccountCodes.TenantReceivable, e.Amount, null, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, UnitId: e.UnitId, OwnerId: e.OwnerId, TenantId: e.TenantId),
                new(AccountCodes.OwnerEquity, null, e.Amount, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId),
            ], DueDate: e.DueDate ?? e.Date),
            FeeCharged e => new PostEntryRequest(e.Date, "FeeCharged", FeeSubtype(e.Kind), e.Description, e.SourceRef,
                [
                    new(AccountCodes.TenantReceivable, e.Amount, null, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, UnitId: e.UnitId, OwnerId: e.OwnerId, TenantId: e.TenantId),
                new(AccountCodes.OwnerEquity, null, e.Amount, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId),
            ], AssessesEntryId: e.AssessesEntryId, DueDate: e.Date),
            ManagementFeeAssessed e => new PostEntryRequest(
                e.Date, "ManagementFeeAssessed", null, e.Description, e.SourceRef,
                [
                    new(AccountCodes.OwnerEquity, e.Amount, null, EntryBasis.Both,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, BankAccountId: e.OperatingBankId),
                // PM income remains structurally isolated: it never carries an owner dimension.
                new(AccountCodes.PmIncome, null, e.Amount, EntryBasis.Both,
                    PropertyId: e.PropertyId, BankAccountId: e.OperatingBankId),
                ]),
            OwnerDisbursed e => new PostEntryRequest(e.Date, "OwnerDisbursed", null, e.Description, e.SourceRef,
                [
                    new(AccountCodes.OwnerEquity, e.Amount, null, EntryBasis.Both,
                    OwnerId: e.OwnerId, BankAccountId: e.BankAccountId),
                new(AccountCodes.TrustBank(e.BankAccountId), null, e.Amount, EntryBasis.Both,
                    BankAccountId: e.BankAccountId),
            ]),
            _ => null,
        };

        return request is not null;
    }

    public static PostEntryRequest Build(AccountingEvent businessEvent)
    {
        if (TryBuild(businessEvent, out var request))
        {
            return request;
        }

        throw new ArgumentException(
            $"{businessEvent.GetType().Name} is not a supported bulk-run accounting event.",
            nameof(businessEvent));
    }

    private static string FeeSubtype(FeeKind kind) => kind switch
    {
        FeeKind.Late => "late",
        FeeKind.MaintenanceRecharge => "maintenance-recharge",
        FeeKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
