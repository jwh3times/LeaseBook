using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Posts the bank's return of a tenant payment as a linked reversal of its receipt (#490, ADR-052): a
/// mirror of every receipt line, in the same basis and dimensions, dated <see cref="ReturnDate"/> — the
/// date the bank evidenced the debit, never the receipt date or today.
/// <para>
/// Unlike <see cref="VoidEntry"/> this is guarded. A receipt's credit may since have been spent, and a
/// raw reversal would then drive a balance below zero while every entry still balanced. Under the
/// posting lock it refuses (<c>return_prepayment_consumed</c>, <c>return_owner_funds_disbursed</c>) when
/// the reversal would leave the tenant's prepaid credit in that bank, or the owner's cash equity — in
/// that bank or in total — negative. The reversal is backdated, so each guard reads the lowest balance
/// on or after <see cref="ReturnDate"/> rather than today's: money that has arrived since must not hide a
/// balance the reversal would have driven negative in between. A return dated before its receipt is
/// refused (<c>return_precedes_receipt</c>), and one dated in a locked period is refused by the posting
/// service like any other entry; the date is never moved.
/// </para>
/// <para>
/// <see cref="InternalNote"/> is staff-only (#468). The reversal's owner-facing description is
/// <c>Void — {receipt description}</c>, as for every reversal.
/// </para>
/// </summary>
public sealed record ReturnTenantPayment(Guid ReceiptEntryId, DateOnly ReturnDate, string SourceRef, string InternalNote)
    : ICommand<PostResult>;

public sealed class ReturnTenantPaymentValidator : AbstractValidator<ReturnTenantPayment>
{
    public ReturnTenantPaymentValidator()
    {
        RuleFor(x => x.ReceiptEntryId).NotEmpty();
        RuleFor(x => x.ReturnDate).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        RuleFor(x => x.InternalNote).NotEmpty().MaximumLength(500);
    }
}

internal sealed class ReturnTenantPaymentHandler(DbContext db, IPostingLock postingLock, IReversalService reversal)
    : ICommandHandler<ReturnTenantPayment, PostResult>
{
    public async Task<PostResult> Handle(ReturnTenantPayment command, CancellationToken ct)
    {
        // The balances below are only meaningful if nothing can post between reading them and the reversal.
        await postingLock.AcquireAsync(ct);

        var receipt = await db.Set<JournalEntry>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == command.ReceiptEntryId, ct);
        if (receipt is null || receipt.EventType != "PaymentReceived")
        {
            throw new EntryNotFoundException(command.ReceiptEntryId);
        }

        // Answer a repeat as what it is. Left to the guards, a receipt already reversed would be reported
        // as spent, since its own reversal emptied the balances they read.
        if (await db.Set<JournalEntry>().AsNoTracking().AnyAsync(e => e.ReversesEntryId == command.ReceiptEntryId, ct))
        {
            throw new AlreadyReversedException(command.ReceiptEntryId, AlreadyReversedReason.AlreadyReversed);
        }

        if (command.ReturnDate < receipt.EntryDate)
        {
            throw new PaymentReturnBlockedException(command.ReceiptEntryId, PaymentReturnBlock.ReturnPrecedesReceipt);
        }

        var lines = await (
            from line in db.Set<JournalLine>().AsNoTracking()
            join account in db.Set<Account>().AsNoTracking() on line.AccountId equals account.Id
            where line.EntryId == command.ReceiptEntryId
            select new { account.Code, line.AccountClass, line.Credit, line.TenantId, line.OwnerId, line.BankAccountId })
            .ToListAsync(ct);

        var balances = new BalanceReader(db);

        foreach (var prepaid in lines
            .Where(l => l.Code == AccountCodes.TenantPrepayments && l.Credit is not null)
            .GroupBy(l => (TenantId: l.TenantId!.Value, BankAccountId: l.BankAccountId!.Value)))
        {
            var floor = await balances.PrepaymentsHeldFloorAsync(
                prepaid.Key.TenantId, prepaid.Key.BankAccountId, command.ReturnDate, ct);
            if (floor < prepaid.Sum(l => l.Credit!.Value.Amount))
            {
                throw new PaymentReturnBlockedException(command.ReceiptEntryId, PaymentReturnBlock.PrepaymentConsumed);
            }
        }

        foreach (var equity in lines
            .Where(l => l.AccountClass == AccountClass.OwnerEquity && l.Credit is not null)
            .GroupBy(l => (OwnerId: l.OwnerId!.Value, BankAccountId: l.BankAccountId!.Value)))
        {
            var returned = equity.Sum(l => l.Credit!.Value.Amount);
            // Both reads: the per-bank one stops another bank's equity covering this reversal, and the
            // total stops a payout recorded without a bank dimension from being missed.
            if (await balances.OwnerEquityCashFloorAsync(
                    equity.Key.OwnerId, equity.Key.BankAccountId, command.ReturnDate, ct) < returned
                || await balances.OwnerEquityCashFloorAsync(equity.Key.OwnerId, command.ReturnDate, ct) < returned)
            {
                throw new PaymentReturnBlockedException(command.ReceiptEntryId, PaymentReturnBlock.OwnerFundsDisbursed);
            }
        }

        var id = await reversal.ReverseAsync(
            command.ReceiptEntryId, command.InternalNote, command.ReturnDate, command.SourceRef, ct);
        return new PostResult(id);
    }
}
