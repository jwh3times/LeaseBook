using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Posting;

/// <summary>
/// The posting-template catalog (§C.3): translates each business event into a balanced, per-basis
/// entry and posts it through the single write path. Guarded events (P31) take the per-org advisory
/// lock and read a balance before posting — the <c>PaymentReceived</c> auto-split, the deposit/
/// prepayment over-application checks, the PM-fee over-sweep check, and the disbursement reserve floor.
/// Every entry balances per basis <i>by construction</i> (proven by the catalog property test).
/// </summary>
internal sealed class AccountingEventService(DbContext db, IPostingService posting, IPostingLock postingLock)
    : IAccountingEvents, IBalanceForward
{
    private readonly BalanceReader _balances = new(db);

    public Task<Guid> PostAsync(AccountingEvent businessEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(businessEvent);
        return businessEvent switch
        {
            RentCharged e => PostRentChargedAsync(e, ct),
            FeeCharged e => PostFeeChargedAsync(e, ct),
            CreditIssued e => PostCreditIssuedAsync(e, ct),
            PaymentReceived e => PostPaymentReceivedAsync(e, ct),
            DepositCollected e => PostDepositCollectedAsync(e, ct),
            PrepaymentReceived e => PostPrepaymentReceivedAsync(e, ct),
            DepositApplied e => PostDepositAppliedAsync(e, ct),
            PrepaymentApplied e => PostPrepaymentAppliedAsync(e, ct),
            ManagementFeeAssessed e => PostManagementFeeAssessedAsync(e, ct),
            PMFeesSwept e => PostPmFeesSweptAsync(e, ct),
            OwnerContribution e => PostOwnerContributionAsync(e, ct),
            OwnerDisbursed e => PostOwnerDisbursedAsync(e, ct),
            VendorPaid e => PostVendorPaidAsync(e, ct),
            RefundIssued e => PostRefundIssuedAsync(e, ct),
            BankFeeCharged e => PostBankFeeChargedAsync(e, ct),
            InterestEarned e => PostInterestEarnedAsync(e, ct),
            TrustTransfer e => PostTrustTransferAsync(e, ct),
            _ => throw new ArgumentOutOfRangeException(
                nameof(businessEvent), businessEvent.GetType().Name, "No posting template for this event."),
        };
    }

    public Task<Guid> PostAsync(BalanceForwardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Opening positions are an arbitrary balanced line set, all basis `both` (P27).
        var lines = request.Lines
            .Select(l => new PostLineRequest(
                l.AccountCode, l.Debit, l.Credit, EntryBasis.Both,
                l.PropertyId, l.UnitId, l.OwnerId, l.TenantId, l.BankAccountId, l.Memo))
            .ToList();

        return posting.PostAsync(new PostEntryRequest(
            request.Date, "BalanceForward", null, request.Description, request.SourceRef, lines), ct);
    }

    public Task<Guid> PostOpeningPositionAsync(OpeningPositionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The clearing contra mirrors the real leg's amount on the opposite side, same basis + dims-light
        // (clearing carries the bank dim only, for per-account residual reads). Both legs tagged req.Basis.
        var realLeg = new PostLineRequest(
            req.AccountCode, req.Debit, req.Credit, req.Basis,
            req.PropertyId, req.UnitId, req.OwnerId, req.TenantId, req.BankAccountId, req.Memo);
        var clearingLeg = new PostLineRequest(
            AccountCodes.MigrationClearing, req.Credit, req.Debit, req.Basis,
            BankAccountId: req.BankAccountId, Memo: req.Memo);

        return posting.PostAsync(new PostEntryRequest(
            req.Cutover, "OpeningBalance", null, req.Memo ?? "Opening balance", req.SourceRef,
            [realLeg, clearingLeg]), ct);
    }

    // ----- Accrual-only charges -------------------------------------------------------------------

    private Task<Guid> PostRentChargedAsync(RentCharged e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "RentCharged", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.TenantReceivable, e.Amount, null, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, UnitId: e.UnitId, OwnerId: e.OwnerId, TenantId: e.TenantId),
                new(AccountCodes.OwnerEquity, null, e.Amount, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId),
            ]), ct);

    private Task<Guid> PostFeeChargedAsync(FeeCharged e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "FeeCharged", FeeSubtype(e.Kind), e.Description, e.SourceRef,
            [
                new(AccountCodes.TenantReceivable, e.Amount, null, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, UnitId: e.UnitId, OwnerId: e.OwnerId, TenantId: e.TenantId),
                new(AccountCodes.OwnerEquity, null, e.Amount, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId),
            ]), ct);

    private Task<Guid> PostCreditIssuedAsync(CreditIssued e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "CreditIssued", null, e.Reason, e.SourceRef,
            [
                new(AccountCodes.OwnerEquity, e.Amount, null, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId),
                new(AccountCodes.TenantReceivable, null, e.Amount, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, TenantId: e.TenantId),
            ]), ct);

    // ----- Cash receipts --------------------------------------------------------------------------

    private async Task<Guid> PostPaymentReceivedAsync(PaymentReceived e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);

        // Auto-split (P31): up to the open receivable clears it; any excess is a prepayment liability
        // (never a negative receivable).
        var openReceivable = await _balances.TenantReceivableAsync(e.TenantId, ct);
        var receivablePortion = Math.Min(e.Amount.Amount, Math.Max(openReceivable, 0m));
        var excess = e.Amount.Amount - receivablePortion;

        var trustBank = AccountCodes.TrustBank(e.BankAccountId);
        var lines = new List<PostLineRequest>
        {
            new(trustBank, e.Amount, null, EntryBasis.Both, BankAccountId: e.BankAccountId),
        };

        if (receivablePortion > 0m)
        {
            lines.Add(new(AccountCodes.TenantReceivable, null, new Money(receivablePortion), EntryBasis.Accrual,
                PropertyId: e.PropertyId, OwnerId: e.OwnerId, TenantId: e.TenantId));
            lines.Add(new(AccountCodes.OwnerEquity, null, new Money(receivablePortion), EntryBasis.Cash,
                PropertyId: e.PropertyId, OwnerId: e.OwnerId, BankAccountId: e.BankAccountId));
        }

        if (excess > 0m)
        {
            lines.Add(new(AccountCodes.TenantPrepayments, null, new Money(excess), EntryBasis.Both,
                TenantId: e.TenantId, BankAccountId: e.BankAccountId));
        }

        return await posting.PostAsync(new PostEntryRequest(
            e.Date, "PaymentReceived", MethodSubtype(e.Method), e.Description, e.SourceRef, lines), ct);
    }

    private Task<Guid> PostDepositCollectedAsync(DepositCollected e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "DepositCollected", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.TrustBank(e.DepositBankId), e.Amount, null, EntryBasis.Both,
                    BankAccountId: e.DepositBankId),
                new(AccountCodes.SecurityDepositsHeld, null, e.Amount, EntryBasis.Both,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, TenantId: e.TenantId, BankAccountId: e.DepositBankId),
            ]), ct);

    private Task<Guid> PostPrepaymentReceivedAsync(PrepaymentReceived e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "PrepaymentReceived", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.TrustBank(e.BankAccountId), e.Amount, null, EntryBasis.Both,
                    BankAccountId: e.BankAccountId),
                new(AccountCodes.TenantPrepayments, null, e.Amount, EntryBasis.Both,
                    TenantId: e.TenantId, BankAccountId: e.BankAccountId),
            ]), ct);

    // ----- Liability applications -----------------------------------------------------------------

    private async Task<Guid> PostDepositAppliedAsync(DepositApplied e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);
        var held = await _balances.DepositsHeldAsync(e.TenantId, ct);
        if (e.Amount.Amount > held)
        {
            throw new InsufficientLiabilityException(
                $"Deposit application {e.Amount} exceeds the {held:0.00} held for tenant {e.TenantId}.");
        }

        // Applied against charges has no excess path (unlike PaymentReceived's auto-split), so it may
        // not exceed the open receivable or it would silently drive it negative (ADR-011 / P51). Damages
        // (ToOwnerIncome) legitimately exceed any receivable and stay unguarded. Read under the held lock.
        if (e.Target == DepositApplication.AgainstCharges)
        {
            var owed = Math.Max(await _balances.TenantReceivableAsync(e.TenantId, ct), 0m);
            if (e.Amount.Amount > owed)
            {
                throw new InsufficientReceivableException(
                    $"Deposit application {e.Amount} exceeds the {owed:0.00} owed by tenant {e.TenantId}.");
            }
        }

        // Liability ↓ and deposit-bank ↓; operating-bank ↑. Income is recognized on application,
        // identically in both bases (the four/five lines net the physical dep→oper transfer).
        var lines = new List<PostLineRequest>
        {
            new(AccountCodes.SecurityDepositsHeld, e.Amount, null, EntryBasis.Both,
                TenantId: e.TenantId, BankAccountId: e.DepositBankId),
            new(AccountCodes.TrustBank(e.DepositBankId), null, e.Amount, EntryBasis.Both,
                BankAccountId: e.DepositBankId),
            new(AccountCodes.TrustBank(e.OperatingBankId), e.Amount, null, EntryBasis.Both,
                BankAccountId: e.OperatingBankId),
        };

        if (e.Target == DepositApplication.ToOwnerIncome)
        {
            lines.Add(new(AccountCodes.OwnerEquity, null, e.Amount, EntryBasis.Both,
                PropertyId: e.PropertyId, OwnerId: e.OwnerId, BankAccountId: e.OperatingBankId));
        }
        else
        {
            // Applied against charges: the equity credit splits into a receivable clear (accrual) and
            // the owner's cash income (cash).
            lines.Add(new(AccountCodes.TenantReceivable, null, e.Amount, EntryBasis.Accrual,
                PropertyId: e.PropertyId, OwnerId: e.OwnerId, TenantId: e.TenantId));
            lines.Add(new(AccountCodes.OwnerEquity, null, e.Amount, EntryBasis.Cash,
                PropertyId: e.PropertyId, OwnerId: e.OwnerId, BankAccountId: e.OperatingBankId));
        }

        return await posting.PostAsync(new PostEntryRequest(
            e.Date, "DepositApplied", null, e.Description, e.SourceRef, lines), ct);
    }

    private async Task<Guid> PostPrepaymentAppliedAsync(PrepaymentApplied e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);
        var held = await _balances.PrepaymentsHeldAsync(e.TenantId, ct);
        if (e.Amount.Amount > held)
        {
            throw new InsufficientLiabilityException(
                $"Prepayment application {e.Amount} exceeds the {held:0.00} held for tenant {e.TenantId}.");
        }

        // A prepayment clears charges and likewise has no excess path — it may not exceed the open
        // receivable (ADR-011 / P51). Read under the held lock.
        var owed = Math.Max(await _balances.TenantReceivableAsync(e.TenantId, ct), 0m);
        if (e.Amount.Amount > owed)
        {
            throw new InsufficientReceivableException(
                $"Prepayment application {e.Amount} exceeds the {owed:0.00} owed by tenant {e.TenantId}.");
        }

        // No bank movement — both positions sit in the same operating trust.
        return await posting.PostAsync(new PostEntryRequest(e.Date, "PrepaymentApplied", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.TenantPrepayments, e.Amount, null, EntryBasis.Both,
                    TenantId: e.TenantId, BankAccountId: e.BankAccountId),
                new(AccountCodes.TenantReceivable, null, e.Amount, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, TenantId: e.TenantId),
                new(AccountCodes.OwnerEquity, null, e.Amount, EntryBasis.Cash,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, BankAccountId: e.BankAccountId),
            ]), ct);
    }

    // ----- PM income ------------------------------------------------------------------------------

    private Task<Guid> PostManagementFeeAssessedAsync(ManagementFeeAssessed e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "ManagementFeeAssessed", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.OwnerEquity, e.Amount, null, EntryBasis.Both,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, BankAccountId: e.OperatingBankId),
                // pm_income carries NO owner dimension (P25) — the structural isolation.
                new(AccountCodes.PmIncome, null, e.Amount, EntryBasis.Both,
                    PropertyId: e.PropertyId, BankAccountId: e.OperatingBankId),
            ]), ct);

    private async Task<Guid> PostPmFeesSweptAsync(PMFeesSwept e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);
        var held = await _balances.HeldFeesAsync(e.OperatingBankId, ct);
        if (e.Amount.Amount > held)
        {
            throw new InsufficientLiabilityException(
                $"Fee sweep {e.Amount} exceeds the {held:0.00} held in the operating trust bank.");
        }

        // Cash moves trust → PM operating; the income attribution moves with it; net income unchanged.
        return await posting.PostAsync(new PostEntryRequest(e.Date, "PMFeesSwept", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.TrustBank(e.OperatingBankId), null, e.Amount, EntryBasis.Both,
                    BankAccountId: e.OperatingBankId),
                new(AccountCodes.PmOperatingBank(e.PmBankId), e.Amount, null, EntryBasis.Both,
                    BankAccountId: e.PmBankId),
                new(AccountCodes.PmIncome, e.Amount, null, EntryBasis.Both, BankAccountId: e.OperatingBankId),
                new(AccountCodes.PmIncome, null, e.Amount, EntryBasis.Both, BankAccountId: e.PmBankId),
            ]), ct);
    }

    // ----- Owner cash movements -------------------------------------------------------------------

    private Task<Guid> PostOwnerContributionAsync(OwnerContribution e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "OwnerContribution", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.TrustBank(e.BankAccountId), e.Amount, null, EntryBasis.Both,
                    BankAccountId: e.BankAccountId),
                new(AccountCodes.OwnerEquity, null, e.Amount, EntryBasis.Both,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, BankAccountId: e.BankAccountId),
            ]), ct);

    private async Task<Guid> PostOwnerDisbursedAsync(OwnerDisbursed e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);
        await GuardReserveFloorAsync(e.OwnerId, e.Amount, e.Reserve, ct);

        return await posting.PostAsync(new PostEntryRequest(e.Date, "OwnerDisbursed", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.OwnerEquity, e.Amount, null, EntryBasis.Both,
                    OwnerId: e.OwnerId, BankAccountId: e.BankAccountId),
                new(AccountCodes.TrustBank(e.BankAccountId), null, e.Amount, EntryBasis.Both,
                    BankAccountId: e.BankAccountId),
            ]), ct);
    }

    private async Task<Guid> PostVendorPaidAsync(VendorPaid e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);
        await GuardReserveFloorAsync(e.OwnerId, e.Amount, e.Reserve, ct);

        return await posting.PostAsync(new PostEntryRequest(
            e.Date, "VendorPaid", null, $"Vendor payment to {e.Payee} — {e.Description}", e.SourceRef,
            [
                new(AccountCodes.OwnerEquity, e.Amount, null, EntryBasis.Both,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, BankAccountId: e.BankAccountId),
                new(AccountCodes.TrustBank(e.BankAccountId), null, e.Amount, EntryBasis.Both,
                    BankAccountId: e.BankAccountId),
            ]), ct);
    }

    private async Task<Guid> PostRefundIssuedAsync(RefundIssued e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);

        var (liabilityCode, held, subtype) = e.Source == RefundSource.Prepayments
            ? (AccountCodes.TenantPrepayments, await _balances.PrepaymentsHeldAsync(e.TenantId, ct), "prepayments")
            : (AccountCodes.SecurityDepositsHeld, await _balances.DepositsHeldAsync(e.TenantId, ct), "deposits");

        if (e.Amount.Amount > held)
        {
            throw new InsufficientLiabilityException(
                $"Refund {e.Amount} exceeds the {held:0.00} held ({subtype}) for tenant {e.TenantId}.");
        }

        return await posting.PostAsync(new PostEntryRequest(e.Date, "RefundIssued", subtype, e.Description, e.SourceRef,
            [
                new(liabilityCode, e.Amount, null, EntryBasis.Both,
                    TenantId: e.TenantId, BankAccountId: e.BankAccountId),
                new(AccountCodes.TrustBank(e.BankAccountId), null, e.Amount, EntryBasis.Both,
                    BankAccountId: e.BankAccountId),
            ]), ct);
    }

    // ----- Bank adjustments (M4 / ADR-014) --------------------------------------------------------

    private async Task<Guid> PostBankFeeChargedAsync(BankFeeCharged e, CancellationToken ct)
    {
        // The PM covers the fee from its own held funds in that bank: held PM fees ↓ and the bank ↓, so
        // owners/tenants are untouched and the trust equation stays balanced.
        var bank = await BankCodeAsync(e.BankAccountId, ct);
        return await posting.PostAsync(new PostEntryRequest(e.Date, "BankFeeCharged", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.PmIncome, e.Amount, null, EntryBasis.Both, BankAccountId: e.BankAccountId),
                new(bank, null, e.Amount, EntryBasis.Both, BankAccountId: e.BankAccountId),
            ]), ct);
    }

    private async Task<Guid> PostInterestEarnedAsync(InterestEarned e, CancellationToken ct)
    {
        // Interest accrues to the PM's held position in the account (the bank ↑, held PM fees ↑); the
        // entitlement policy (PM vs owner vs housing fund) is deferred (ADR-014).
        var bank = await BankCodeAsync(e.BankAccountId, ct);
        return await posting.PostAsync(new PostEntryRequest(e.Date, "InterestEarned", null, e.Description, e.SourceRef,
            [
                new(bank, e.Amount, null, EntryBasis.Both, BankAccountId: e.BankAccountId),
                new(AccountCodes.PmIncome, null, e.Amount, EntryBasis.Both, BankAccountId: e.BankAccountId),
            ]), ct);
    }

    private async Task<Guid> PostTrustTransferAsync(TrustTransfer e, CancellationToken ct)
    {
        // Moves the PM's own held funds between accounts: cash from→to, and the held-PM-fee attribution
        // moves with it, so each account's trust equation stays balanced. Owner/deposit funds are never
        // moved by this template (ADR-014).
        var fromBank = await BankCodeAsync(e.FromBankId, ct);
        var toBank = await BankCodeAsync(e.ToBankId, ct);
        return await posting.PostAsync(new PostEntryRequest(e.Date, "TrustTransfer", null, e.Description, e.SourceRef,
            [
                new(toBank, e.Amount, null, EntryBasis.Both, BankAccountId: e.ToBankId),
                new(fromBank, null, e.Amount, EntryBasis.Both, BankAccountId: e.FromBankId),
                new(AccountCodes.PmIncome, e.Amount, null, EntryBasis.Both, BankAccountId: e.FromBankId),
                new(AccountCodes.PmIncome, null, e.Amount, EntryBasis.Both, BankAccountId: e.ToBankId),
            ]), ct);
    }

    /// <summary>The chart code of the bank account representing <paramref name="bankId"/> (trust or PM operating).</summary>
    private async Task<string> BankCodeAsync(Guid bankId, CancellationToken ct)
    {
        var code = await db.Set<Account>()
            .Where(a => a.BankAccountId == bankId
                && (a.Class == AccountClass.TrustBank || a.Class == AccountClass.PmOperatingBank))
            .Select(a => a.Code)
            .SingleOrDefaultAsync(ct);
        return code ?? throw new UnknownAccountException(AccountCodes.TrustBank(bankId));
    }

    private async Task GuardReserveFloorAsync(Guid ownerId, Money amount, Money reserve, CancellationToken ct)
    {
        var equity = await _balances.OwnerEquityCashAsync(ownerId, ct);
        if (equity - amount.Amount < reserve.Amount)
        {
            throw new ReserveFloorException(
                $"Disbursement {amount} would take owner {ownerId} equity from {equity:0.00} below the " +
                $"reserve floor {reserve}.");
        }
    }

    private static string FeeSubtype(FeeKind kind) => kind switch
    {
        FeeKind.Late => "late",
        FeeKind.MaintenanceRecharge => "maintenance-recharge",
        FeeKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string MethodSubtype(PaymentMethod method) => method switch
    {
        PaymentMethod.Ach => "ACH",
        PaymentMethod.Card => "Card",
        PaymentMethod.Check => "Check",
        PaymentMethod.Cash => "Cash",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };
}
