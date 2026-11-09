using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Refunds;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Voids a posted entry → a linked reversal through <see cref="IReversalService"/> (append-only; the
/// original is never mutated, M3-E1). The reversal lands in the open period dated <see cref="AsOfDate"/>
/// (default the organization's today, P57, #642; never later). <see cref="SourceRef"/> is the P54 idempotency key, so a double-submitted void
/// maps to <c>duplicate_source_ref</c> (409) — and a re-void of an already-reversed entry to <c>already_reversed</c>.
/// <para>
/// <see cref="Reason"/> is required and <b>staff-only</b> (#468): it is stored as the reversal's internal
/// note. The owner statement shows the reversal as <c>Void — {original description}</c>, never the reason.
/// </para>
/// <para>
/// A refund check is refused here (<c>refund_check_void_required</c>, #473): it is voided through
/// <see cref="VoidRefundCheck"/>, which refuses a cleared check and clears the void's bank lines.
/// </para>
/// <para>
/// A void whose mirrored lines lower the PM's held fees in a bank is refused (<c>pm_fees_insufficient</c>,
/// #720) when they would go below zero there on its date or any later one, as a bank fee is: past zero
/// the bank would be paying the PM's charges from owners' and tenants' money. Voiding a management fee
/// already swept to the PM operating bank is the usual case. The floor sits here, on the staff void,
/// rather than in <see cref="IReversalService"/>, whose other callers either carry no held-fee lines or
/// repost the corrected position at once (the onboarding supersede).
/// </para>
/// </summary>
public sealed record VoidEntry(Guid EntryId, string Reason, DateOnly? AsOfDate, string SourceRef)
    : ICommand<PostResult>;

public sealed class VoidEntryValidator : AbstractValidator<VoidEntry>
{
    public VoidEntryValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
    }
}

internal sealed class VoidEntryHandler(
    DbContext db, IReversalService reversal, PostingCalendar calendar, IAccountingPeriods periods,
    IReconciliationLock reconciliationLock, IPostingLock postingLock)
    : ICommandHandler<VoidEntry, PostResult>
{
    public async Task<PostResult> Handle(VoidEntry command, CancellationToken ct)
    {
        // The reversal is dated the organization's today unless the void names a day, which cannot be
        // later (#642).
        var asOf = command.AsOfDate ?? await calendar.TodayAsync(ct);
        await calendar.EnsureNotAfterTodayAsync(asOf, ct);

        var entry = await db.Set<JournalEntry>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == command.EntryId, ct);
        if (entry is not null && RefundChecks.IsRefundCheck(entry))
        {
            throw new RefundCheckVoidRequiredException(command.EntryId);
        }

        await GuardHeldFeesAsync(entry, asOf, ct);

        var id = await reversal.ReverseAsync(command.EntryId, command.Reason, asOf, command.SourceRef, ct);
        return new PostResult(id);
    }

    /// <summary>
    /// Refuses the void when, in any bank, it would take more from the held PM fees than the lowest
    /// balance they reach on or after <paramref name="asOf"/>. Under the posting lock, so a charge posted
    /// meanwhile cannot draw on the same fees. An entry the reversal service will refuse anyway (missing,
    /// a reversal itself, already reversed), a void into a closed period, or one into a reconciled month
    /// of a bank it would take from, is left to that refusal: no contribution can be dated there, so a
    /// shortfall is not the answer to give.
    /// </summary>
    private async Task GuardHeldFeesAsync(JournalEntry? entry, DateOnly asOf, CancellationToken ct)
    {
        if (entry is null || entry.ReversesEntryId is not null
            || await db.Set<JournalEntry>().AsNoTracking().AnyAsync(e => e.ReversesEntryId == entry.Id, ct)
            || (await periods.GetOpenPeriodAsync(asOf, ct)).Status == PeriodStatus.Closed)
        {
            return;
        }

        // Money is value-converted, so the lines are summed here rather than in SQL. The held fees a bank
        // gains from the entry are what its void takes back from them.
        var heldFeeLines = await db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.EntryId == entry.Id && l.AccountClass == AccountClass.PmIncome
                && l.Basis != EntryBasis.Accrual && l.BankAccountId != null)
            .Select(l => new { BankAccountId = l.BankAccountId!.Value, l.Debit, l.Credit })
            .ToListAsync(ct);
        var taken = heldFeeLines
            .GroupBy(l => l.BankAccountId)
            .Select(g => (Bank: g.Key, Amount: g.Sum(l => (l.Credit?.Amount ?? 0m) - (l.Debit?.Amount ?? 0m))))
            .Where(b => b.Amount > 0m)
            .ToList();
        if (taken.Count == 0)
        {
            return;
        }

        foreach (var (bank, _) in taken)
        {
            await reconciliationLock.EnsureOpenAsync(bank, asOf, ct);
        }

        await postingLock.AcquireAsync(ct);
        var balances = new BalanceReader(db);
        foreach (var (bank, amount) in taken)
        {
            var held = await balances.HeldFeesFloorAsync(bank, asOf, ct);
            if (amount > held)
            {
                var account = await db.Set<Account>().AsNoTracking()
                    .Where(a => a.BankAccountId == bank
                        && (a.Class == AccountClass.TrustBank || a.Class == AccountClass.PmOperatingBank))
                    .Select(a => new { a.Name, a.Class })
                    .SingleAsync(ct);
                var charge = account.Class == AccountClass.TrustBank
                    ? HeldFeesCharge.Void
                    : HeldFeesCharge.VoidInPmOperatingBank;
                throw new PmFeesInsufficientException(amount, held, bank, charge, account.Name, asOf);
            }
        }
    }
}
