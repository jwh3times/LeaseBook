using System.Diagnostics;
using FluentValidation;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features.RefundChecks;

/// <summary>
/// Voids a refund check (#473) through Accounting's guarded void: refused once the check has cleared,
/// otherwise a linked reversal that restores the held liability. The check record is not changed — its
/// <c>voided</c> status is read back from the reversal. Null when the check is not visible.
/// </summary>
public sealed record VoidIssuedRefundCheck(Guid Id, string Reason) : ICommand<RefundCheckView?>;

public sealed class VoidIssuedRefundCheckValidator : AbstractValidator<VoidIssuedRefundCheck>
{
    public VoidIssuedRefundCheckValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class VoidIssuedRefundCheckHandler(DbContext db, IOrgContext org, IRefundCheckLedger ledger)
    : ICommandHandler<VoidIssuedRefundCheck, RefundCheckView?>
{
    public async Task<RefundCheckView?> Handle(VoidIssuedRefundCheck c, CancellationToken ct)
    {
        // Serialized with issue and print, so a print can never be recorded against a check being voided.
        await RefundCheckReads.LockAsync(db, org, ct);
        var check = await db.Set<RefundCheck>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (check is null)
        {
            return null;
        }

        var reversalId = await ledger.VoidAsync(check.EntryId, c.Reason.Trim(), ct);
        Activity.Current?.AddEvent(new ActivityEvent("payments.refund_check_voided", tags: new ActivityTagsCollection
        {
            { "check_id", check.Id },
            { "entry_id", reversalId },
        }));
        return (await RefundCheckReads.ViewsAsync(db, ledger, [check], ct))[0];
    }
}

/// <summary>
/// Records one print of a check and returns it for rendering (#473). A voided check cannot be printed
/// (<c>refund_check_voided</c>); reprinting an outstanding check keeps its number. Null when not visible.
/// </summary>
public sealed record RecordRefundCheckPrint(Guid Id) : ICommand<RefundCheckView?>;

internal sealed class RecordRefundCheckPrintHandler(DbContext db, IOrgContext org, IRefundCheckLedger ledger)
    : ICommandHandler<RecordRefundCheckPrint, RefundCheckView?>
{
    public async Task<RefundCheckView?> Handle(RecordRefundCheckPrint c, CancellationToken ct)
    {
        await RefundCheckReads.LockAsync(db, org, ct);
        var check = await db.Set<RefundCheck>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (check is null)
        {
            return null;
        }

        var current = (await RefundCheckReads.ViewsAsync(db, ledger, [check], ct))[0];
        if (current.Status == "voided")
        {
            throw RefundCheckConflictException.Voided();
        }

        if (await db.Set<BankMicrProfile>().AnyAsync(
                p => p.BankAccountId == check.BankAccountId && p.StockKind == CheckStockKinds.Blank, ct))
        {
            throw RefundCheckConflictException.BlankStockUnsupported();
        }

        db.Add(new RefundCheckPrint { Id = LeaseBook.SharedKernel.UuidV7.NewId(), CheckId = check.Id });
        await db.SaveChangesAsync(ct);
        return (await RefundCheckReads.ViewsAsync(db, ledger, [check], ct))[0];
    }
}
