using System.Diagnostics;
using FluentValidation;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features.RefundChecks;

/// <summary>
/// Issues a refund check (#473): posts <c>RefundIssued</c> through Accounting — which derives the bank
/// from the held liability and guards the amount — and records the check in the same transaction. The
/// check number is the one on the stock in the printer; it must be unused on the derived bank
/// (<c>check_number_taken</c>). A retried <see cref="Key"/> returns the original check without posting;
/// the same key with different data is <c>refund_check_conflict</c>.
/// </summary>
public sealed record IssueRefundCheck(
    Guid Key, Guid TenantId, string Source, decimal Amount, DateOnly Date, int CheckNumber,
    string PayeeName, string AddressLine1, string? AddressLine2, string City, string State, string PostalCode,
    string? Memo, string? InternalNote, RefundFundsBucket? Bucket = null) : ICommand<RefundCheckView>;

public sealed class IssueRefundCheckValidator : AbstractValidator<IssueRefundCheck>
{
    public IssueRefundCheckValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Source).Must(s => s is "deposit" or "prepayment").WithMessage("Source must be deposit or prepayment.");
        RuleFor(x => x.Amount).Must(a => a > 0m && a <= 1_000_000m && decimal.Round(a, 2) == a)
            .WithMessage("Amount must be a positive value up to 1,000,000.00 with at most 2 decimal places.");
        RuleFor(x => x.CheckNumber).InclusiveBetween(1, 99_999_999);
        RuleFor(x => x.PayeeName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.AddressLine1).NotEmpty().MaximumLength(120);
        RuleFor(x => x.AddressLine2).MaximumLength(120);
        RuleFor(x => x.City).NotEmpty().MaximumLength(60);
        RuleFor(x => x.State).Matches("^[A-Z]{2}$").WithMessage("Use the two-letter state code.");
        RuleFor(x => x.PostalCode).Matches(@"^\d{5}(-\d{4})?$").WithMessage("Use a 5-digit or ZIP+4 postal code.");
        RuleFor(x => x.Memo).MaximumLength(60);
        RuleFor(x => x.InternalNote).MaximumLength(500);
        RuleFor(x => x.Bucket!.BankAccountId).NotEmpty().When(x => x.Bucket is not null);
    }
}

internal sealed class IssueRefundCheckHandler(DbContext db, IOrgContext org, IRefundCheckLedger ledger)
    : ICommandHandler<IssueRefundCheck, RefundCheckView>
{
    public async Task<RefundCheckView> Handle(IssueRefundCheck c, CancellationToken ct)
    {
        await RefundCheckReads.LockAsync(db, org, ct);

        var existing = await db.Set<RefundCheck>().AsNoTracking().FirstOrDefaultAsync(x => x.Key == c.Key, ct);
        if (existing is not null)
        {
            if (existing.TenantId != c.TenantId || existing.Source != c.Source || existing.Amount != c.Amount
                || existing.CheckNumber != c.CheckNumber)
            {
                throw RefundCheckConflictException.KeyReused();
            }

            return (await RefundCheckReads.ViewsAsync(db, ledger, [existing], ct))[0];
        }

        var id = UuidV7.NewId();
        var posting = await ledger.PostAsync(
            new RefundPostingRequest(c.TenantId, c.Source, c.Amount, c.Date, id, c.CheckNumber, c.Bucket,
                string.IsNullOrWhiteSpace(c.InternalNote) ? null : c.InternalNote.Trim()),
            ct);

        if (await db.Set<RefundCheck>().AnyAsync(
                x => x.BankAccountId == posting.BankAccountId && x.CheckNumber == c.CheckNumber, ct))
        {
            throw RefundCheckConflictException.NumberTaken();
        }

        var check = new RefundCheck
        {
            Id = id,
            Key = c.Key,
            TenantId = c.TenantId,
            BankAccountId = posting.BankAccountId,
            CheckNumber = c.CheckNumber,
            Source = c.Source,
            Amount = c.Amount,
            IssueDate = c.Date,
            PayeeName = c.PayeeName.Trim(),
            AddressLine1 = c.AddressLine1.Trim(),
            AddressLine2 = string.IsNullOrWhiteSpace(c.AddressLine2) ? null : c.AddressLine2.Trim(),
            City = c.City.Trim(),
            State = c.State,
            PostalCode = c.PostalCode,
            Memo = string.IsNullOrWhiteSpace(c.Memo) ? null : c.Memo.Trim(),
            EntryId = posting.EntryId,
        };
        db.Add(check);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Backstop for the unique (bank, number) index; the advisory lock makes this unreachable in
            // practice. Throwing rolls the whole unit back, including the posted refund.
            throw RefundCheckConflictException.NumberTaken();
        }

        // No payee data in telemetry: the check and entry ids are enough to find it.
        Activity.Current?.AddEvent(new ActivityEvent("payments.refund_check_issued", tags: new ActivityTagsCollection
        {
            { "check_id", check.Id },
            { "entry_id", check.EntryId },
        }));
        return (await RefundCheckReads.ViewsAsync(db, ledger, [check], ct))[0];
    }
}
