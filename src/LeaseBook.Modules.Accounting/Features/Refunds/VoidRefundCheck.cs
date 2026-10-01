using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Refunds;

/// <summary>
/// Voids a refund check (#473): a linked reversal that restores the held liability, refused once the
/// check's bank line is cleared or reconciled (<c>refund_check_cleared</c>) — a check the bank has paid
/// cannot be un-written. The original withdrawal and its reversal are then cleared together: they net to
/// zero and never reach the bank, so a voided check does not linger as an outstanding item.
/// <para>
/// The reason is staff-only, stored as the reversal's internal note like any void (#468). The reversal
/// lands in the open period dated <see cref="AsOfDate"/> (default today).
/// </para>
/// </summary>
public sealed record VoidRefundCheck(Guid EntryId, string Reason, DateOnly? AsOfDate) : ICommand<PostResult>;

public sealed class VoidRefundCheckValidator : AbstractValidator<VoidRefundCheck>
{
    public VoidRefundCheckValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class VoidRefundCheckHandler(
    DbContext db, IOrgContext tenant, IPostingLock postingLock, IReversalService reversal, TimeProvider clock)
    : ICommandHandler<VoidRefundCheck, PostResult>
{
    public async Task<PostResult> Handle(VoidRefundCheck command, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);

        var entry = await db.Set<JournalEntry>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == command.EntryId, ct);
        if (entry is null || !RefundChecks.IsRefundCheck(entry))
        {
            throw new EntryNotFoundException(command.EntryId);
        }

        var bankLineId = await BankLineIdAsync(command.EntryId, ct);
        var cleared = await db.Set<BankLineState>().AsNoTracking()
            .AnyAsync(s => s.JournalLineId == bankLineId && s.Status != BankLineStatus.Uncleared, ct);
        if (cleared)
        {
            throw new RefundCheckClearedException(command.EntryId);
        }

        var asOf = command.AsOfDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var reversalId = await reversal.ReverseAsync(
            command.EntryId, command.Reason, asOf, RefundChecks.VoidSourceRef(command.EntryId), ct);

        await new ApplyClearancesHandler(db, tenant).Handle(
            new ApplyClearances([bankLineId, await BankLineIdAsync(reversalId, ct)]), ct);
        return new PostResult(reversalId);
    }

    private Task<Guid> BankLineIdAsync(Guid entryId, CancellationToken ct) =>
        db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.EntryId == entryId && l.AccountClass == AccountClass.TrustBank)
            .Select(l => l.Id)
            .SingleAsync(ct);
}
