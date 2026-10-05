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
/// prepayment over-application checks, the PM-fee over-sweep check, the disbursement reserve floor,
/// and the processor fee shortfall check.
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
            DepositResponsibilityTransferred e => PostDepositResponsibilityTransferredAsync(e, ct),
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
            ProcessorFeeDifference e => PostProcessorFeeDifferenceAsync(e, ct),
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

    public async Task<Guid> PostOpeningPositionAsync(OpeningPositionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // WP-7 §3.1: a pm_income opening must be shaped so the trust equation's held_pm_fees term
        // reads it — wrong shapes post cleanly, balance (I1-invisible), and corrupt I2 later.
        if (req.AccountCode == AccountCodes.PmIncome)
        {
            if (req.Basis != EntryBasis.Both)
                throw new InvalidOpeningPositionException(
                    InvalidOpeningPositionReason.HeldFeesBasisMustBeBoth);
            if (req.BankAccountId is null)
                throw new InvalidOpeningPositionException(
                    InvalidOpeningPositionReason.HeldFeesBankRequired);
            if (req.OwnerId is not null)
                throw new InvalidOpeningPositionException(
                    InvalidOpeningPositionReason.PmIncomeOwnerDimension);
            var isTrustClass = await db.Set<Account>().AsNoTracking()
                .AnyAsync(a => a.Class == AccountClass.TrustBank && a.BankAccountId == req.BankAccountId, ct);
            if (!isTrustClass)
                throw new InvalidOpeningPositionException(
                    InvalidOpeningPositionReason.HeldFeesBankNotTrust);
        }

        // The clearing contra mirrors the real leg's amount on the opposite side, same basis + dims-light
        // (clearing carries the bank dim only, for per-account residual reads). Both legs tagged req.Basis.
        var realLeg = new PostLineRequest(
            req.AccountCode, req.Debit, req.Credit, req.Basis,
            req.PropertyId, req.UnitId, req.OwnerId, req.TenantId, req.BankAccountId, req.Memo);
        var clearingLeg = new PostLineRequest(
            AccountCodes.MigrationClearing, req.Credit, req.Debit, req.Basis,
            BankAccountId: req.BankAccountId, Memo: req.Memo);

        return await posting.PostAsync(new PostEntryRequest(
            req.Cutover, "OpeningBalance", null, req.Memo ?? "Opening balance", req.SourceRef,
            [realLeg, clearingLeg]), ct);
    }

    // ----- Accrual-only charges -------------------------------------------------------------------

    private Task<Guid> PostRentChargedAsync(RentCharged e, CancellationToken ct) =>
        posting.PostAsync(RunEventEntryBuilder.Build(e), ct);

    private Task<Guid> PostFeeChargedAsync(FeeCharged e, CancellationToken ct) =>
        posting.PostAsync(RunEventEntryBuilder.Build(e), ct);

    private Task<Guid> PostCreditIssuedAsync(CreditIssued e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "CreditIssued", null, e.Reason, e.SourceRef,
            [
                new(AccountCodes.OwnerEquity, e.Amount, null, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId),
                new(AccountCodes.TenantReceivable, null, e.Amount, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, TenantId: e.TenantId),
            ], InternalNote: e.InternalNote), ct);

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
            e.Date, "PaymentReceived", MethodSubtype(e.Method), e.Description, e.SourceRef, lines,
            InternalNote: e.InternalNote), ct);
    }

    private Task<Guid> PostDepositCollectedAsync(DepositCollected e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "DepositCollected", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.TrustBank(e.DepositBankId), e.Amount, null, EntryBasis.Both,
                    BankAccountId: e.DepositBankId),
                new(AccountCodes.SecurityDepositsHeld, null, e.Amount, EntryBasis.Both,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, TenantId: e.TenantId, BankAccountId: e.DepositBankId),
            ], InternalNote: e.InternalNote), ct);

    private Task<Guid> PostDepositResponsibilityTransferredAsync(
        DepositResponsibilityTransferred e,
        CancellationToken ct)
    {
        var lines = e.Positions.SelectMany(position => new[]
        {
            new PostLineRequest(
                AccountCodes.SecurityDepositsHeld, position.Amount, null, EntryBasis.Both,
                PropertyId: e.PropertyId, OwnerId: e.FromOwnerId, TenantId: position.TenantId,
                BankAccountId: position.BankAccountId),
            new PostLineRequest(
                AccountCodes.SecurityDepositsHeld, null, position.Amount, EntryBasis.Both,
                PropertyId: e.PropertyId, OwnerId: e.ToOwnerId, TenantId: position.TenantId,
                BankAccountId: position.BankAccountId),
        }).ToList();

        return posting.PostAsync(new PostEntryRequest(
            e.Date, "DepositResponsibilityTransferred", null, e.Description, e.SourceRef, lines), ct);
    }

    private Task<Guid> PostPrepaymentReceivedAsync(PrepaymentReceived e, CancellationToken ct) =>
        posting.PostAsync(new PostEntryRequest(e.Date, "PrepaymentReceived", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.TrustBank(e.BankAccountId), e.Amount, null, EntryBasis.Both,
                    BankAccountId: e.BankAccountId),
                new(AccountCodes.TenantPrepayments, null, e.Amount, EntryBasis.Both,
                    TenantId: e.TenantId, BankAccountId: e.BankAccountId),
            ], InternalNote: e.InternalNote), ct);

    // ----- Liability applications -----------------------------------------------------------------

    private async Task<Guid> PostDepositAppliedAsync(DepositApplied e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);
        var held = await _balances.DepositsHeldAsync(
            e.TenantId, e.PropertyId, e.OwnerId, e.DepositBankId, ct);
        if (e.Amount.Amount > held)
        {
            throw new InsufficientLiabilityException(LiabilityKind.Deposit, e.Amount.Amount, held, e.TenantId);
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
                    ReceivableSource.Deposit, e.Amount.Amount, owed, e.TenantId);
            }
        }

        // Liability ↓ and deposit-bank ↓; operating-bank ↑. Income is recognized on application,
        // identically in both bases (the four/five lines net the physical dep→oper transfer).
        var lines = new List<PostLineRequest>
        {
            // Owner dim mirrors the collecting credit (DepositCollected / the opening import): without it
            // the owner-attributed deposit column never comes back down on a disposition (I7).
            new(AccountCodes.SecurityDepositsHeld, e.Amount, null, EntryBasis.Both,
                PropertyId: e.PropertyId, OwnerId: e.OwnerId, TenantId: e.TenantId, BankAccountId: e.DepositBankId),
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
            e.Date, "DepositApplied", null, e.Description, e.SourceRef, lines,
            InternalNote: e.InternalNote), ct);
    }

    private async Task<Guid> PostPrepaymentAppliedAsync(PrepaymentApplied e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);
        // Bounded by the bank it draws on AND the tenant's total (#475): an application against a bank that
        // never held the prepayment would leave that bank's bucket negative and the collecting bank's
        // positive, with the owner's equity on a bank the cash never reached (I10). The total still bounds
        // it because journals from before this guard can hold a positive bucket beside a negative one.
        var held = Math.Min(
            await _balances.PrepaymentsHeldAsync(e.TenantId, e.BankAccountId, ct),
            await _balances.PrepaymentsHeldAsync(e.TenantId, ct));
        if (e.Amount.Amount > held)
        {
            throw new InsufficientLiabilityException(LiabilityKind.Prepayment, e.Amount.Amount, held, e.TenantId);
        }

        // A prepayment clears charges and likewise has no excess path — it may not exceed the open
        // receivable (ADR-011 / P51). Read under the held lock.
        var owed = Math.Max(await _balances.TenantReceivableAsync(e.TenantId, ct), 0m);
        if (e.Amount.Amount > owed)
        {
            throw new InsufficientReceivableException(
                ReceivableSource.Prepayment, e.Amount.Amount, owed, e.TenantId);
        }

        // No bank movement — the liability and the owner's income both sit in the bank that holds the cash.
        return await posting.PostAsync(new PostEntryRequest(e.Date, "PrepaymentApplied", null, e.Description, e.SourceRef,
            [
                new(AccountCodes.TenantPrepayments, e.Amount, null, EntryBasis.Both,
                    TenantId: e.TenantId, BankAccountId: e.BankAccountId),
                new(AccountCodes.TenantReceivable, null, e.Amount, EntryBasis.Accrual,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, TenantId: e.TenantId),
                new(AccountCodes.OwnerEquity, null, e.Amount, EntryBasis.Cash,
                    PropertyId: e.PropertyId, OwnerId: e.OwnerId, BankAccountId: e.BankAccountId),
            ], InternalNote: e.InternalNote), ct);
    }

    // ----- PM income ------------------------------------------------------------------------------

    private Task<Guid> PostManagementFeeAssessedAsync(ManagementFeeAssessed e, CancellationToken ct) =>
        posting.PostAsync(RunEventEntryBuilder.Build(e), ct);

    private async Task<Guid> PostPmFeesSweptAsync(PMFeesSwept e, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);
        var held = await _balances.HeldFeesAsync(e.OperatingBankId, ct);
        if (e.Amount.Amount > held)
        {
            throw new InsufficientLiabilityException(LiabilityKind.FeeSweep, e.Amount.Amount, held);
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

        return await posting.PostAsync(RunEventEntryBuilder.Build(e), ct);
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

        var liabilityCode = e.Source == RefundSource.Prepayments
            ? AccountCodes.TenantPrepayments
            : AccountCodes.SecurityDepositsHeld;
        var subtype = e.Source == RefundSource.Prepayments ? "prepayments" : "deposits";
        // A prepayment refund is bounded by the bank it draws on AND the tenant's total: before #475 an
        // application debited whichever bank its caller named, so a journal from then can show one bank's
        // bucket positive while another's is negative and the tenant holds less than that bucket shows (#473).
        var held = e.Source == RefundSource.Prepayments
            ? Math.Min(
                await _balances.PrepaymentsHeldAsync(e.TenantId, e.BankAccountId, ct),
                await _balances.PrepaymentsHeldAsync(e.TenantId, ct))
            : await _balances.DepositsHeldAsync(
                e.TenantId, e.PropertyId, e.OwnerId, e.BankAccountId, ct);

        if (e.Amount.Amount > held)
        {
            throw new InsufficientLiabilityException(LiabilityKind.Refund, e.Amount.Amount, held, e.TenantId);
        }

        // Deposits carry an owner dim from collection and must be released against the same one (I7);
        // prepayments are collected owner-null, so the refund stays owner-null rather than inventing one.
        var (refundProperty, refundOwner) = e.Source == RefundSource.Deposits
            ? (e.PropertyId, e.OwnerId)
            : (null, (Guid?)null);

        return await posting.PostAsync(new PostEntryRequest(e.Date, "RefundIssued", subtype, e.Description, e.SourceRef,
            [
                new(liabilityCode, e.Amount, null, EntryBasis.Both,
                    PropertyId: refundProperty, OwnerId: refundOwner,
                    TenantId: e.TenantId, BankAccountId: e.BankAccountId),
                new(AccountCodes.TrustBank(e.BankAccountId), null, e.Amount, EntryBasis.Both,
                    BankAccountId: e.BankAccountId),
            ], InternalNote: e.InternalNote), ct);
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
            ], InternalNote: e.InternalNote), ct);
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
            ], InternalNote: e.InternalNote), ct);
    }

    // ----- Processor fee differences (ADR-053) ---------------------------------------------------

    private async Task<Guid> PostProcessorFeeDifferenceAsync(ProcessorFeeDifference e, CancellationToken ct)
    {
        var shortfall = e.Direction == FeeDifferenceDirection.Shortfall;
        if (shortfall)
        {
            // The PM covers the shortfall from its own held funds in that bank, and only from them. Past
            // zero the entry would still balance and the trust equation would still hold, but the bank
            // would hold less than owners and tenants are owed. The bank-fee template leaves that rule
            // to procedure (ADR-014); this one enforces it.
            //
            // The entry is dated on the bank date, which may be in the past, so the read is the lowest
            // balance on or after that date: fees earned since must not cover a day they were not there.
            await postingLock.AcquireAsync(ct);
            var held = await _balances.HeldFeesFloorAsync(e.BankAccountId, e.Date, ct);
            if (e.Amount.Amount > held)
            {
                throw new PmFeesInsufficientException(e.Amount.Amount, held, e.BankAccountId);
            }
        }

        // A payout lands in a trust bank. Naming the trust account outright, rather than resolving
        // whichever bank account carries this id, makes a PM operating bank an unknown account here.
        var trustBank = AccountCodes.TrustBank(e.BankAccountId);
        Money? debitIfShortfall = shortfall ? e.Amount : null;
        Money? debitIfSurplus = shortfall ? null : e.Amount;
        return await posting.PostAsync(new PostEntryRequest(
            e.Date, "ProcessorFeeDifference", FeeDifferenceSubtype(e.Direction), e.Description, e.SourceRef,
            [
                new(AccountCodes.PmIncome, debitIfShortfall, debitIfSurplus, EntryBasis.Both, BankAccountId: e.BankAccountId),
                new(trustBank, debitIfSurplus, debitIfShortfall, EntryBasis.Both, BankAccountId: e.BankAccountId),
            ], InternalNote: e.InternalNote), ct);
    }

    // Stored in the append-only journal, so spelled out here rather than taken from the enum's names.
    private static string FeeDifferenceSubtype(FeeDifferenceDirection direction) => direction switch
    {
        FeeDifferenceDirection.Shortfall => "Shortfall",
        FeeDifferenceDirection.Surplus => "Surplus",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };

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
            ], InternalNote: e.InternalNote), ct);
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
            throw new ReserveFloorException(amount.Amount, equity, reserve.Amount, ownerId);
        }
    }

    private static string MethodSubtype(PaymentMethod method) => method switch
    {
        PaymentMethod.Ach => "ACH",
        PaymentMethod.Card => "Card",
        PaymentMethod.Check => "Check",
        PaymentMethod.Cash => "Cash",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };
}
