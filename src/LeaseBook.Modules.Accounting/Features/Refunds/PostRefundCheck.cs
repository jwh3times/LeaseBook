using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Refunds;

/// <summary>
/// Posts a refund check → <c>RefundIssued</c> (#473). The bank is derived from the bucket that holds the
/// liability, never supplied (#308): with one held bucket for the source it is used; with several the
/// caller must name one (<c>refund_bucket_ambiguous</c>); a named bucket that holds nothing, or less
/// than the amount, is <c>insufficient_liability</c>. A deposit refund keeps its collection's
/// property/owner (ADR-026). The posting happens at issue: the check is an outstanding withdrawal until
/// its bank line clears.
/// <para>
/// <see cref="CheckId"/> is the Payments check record's id; it becomes the entry's <c>source_ref</c>,
/// which makes a retried issue a <c>duplicate_source_ref</c> and marks the entry as a check for the
/// generic void's refusal.
/// </para>
/// </summary>
public sealed record PostRefundCheck(
    Guid TenantId, string Source, decimal Amount, DateOnly Date, Guid CheckId, int CheckNumber,
    RefundBucket? Bucket = null, string? InternalNote = null) : ICommand<RefundCheckPosted>;

public sealed record RefundCheckPosted(Guid EntryId, Guid BankAccountId, Guid? PropertyId, Guid? OwnerId);

public sealed class PostRefundCheckValidator : AbstractValidator<PostRefundCheck>
{
    public PostRefundCheckValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CheckId).NotEmpty();
        RuleFor(x => x.CheckNumber).InclusiveBetween(1, 99_999_999);
        RuleFor(x => x.Source).Must(RefundChecks.Sources.ContainsKey)
            .WithMessage($"Source must be one of: {string.Join(", ", RefundChecks.Sources.Keys)}.");
        RuleFor(x => x.Bucket!.BankAccountId).NotEmpty().When(x => x.Bucket is not null);
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class PostRefundCheckHandler(DbContext db, IPostingLock postingLock, IAccountingEvents events)
    : ICommandHandler<PostRefundCheck, RefundCheckPosted>
{
    public async Task<RefundCheckPosted> Handle(PostRefundCheck command, CancellationToken ct)
    {
        var source = RefundChecks.Sources[command.Source];
        var sourceName = source == RefundSource.Deposits ? "deposit" : "prepayment";

        // Read the buckets under the same per-org lock the template takes, so the bucket chosen here is
        // the one the template's guard then re-reads (the lock is re-entrant within the transaction).
        await postingLock.AcquireAsync(ct);
        var buckets = (await RefundChecks.ReadAsync(db, [command.TenantId], ct))
            .Where(b => b.Source == sourceName)
            .ToArray();

        RefundableBalance? chosen;
        if (command.Bucket is { } requested)
        {
            chosen = buckets.FirstOrDefault(b => b.BankAccountId == requested.BankAccountId
                && b.PropertyId == requested.PropertyId && b.OwnerId == requested.OwnerId);
        }
        else if (buckets.Length > 1)
        {
            throw new RefundBucketAmbiguousException(command.TenantId);
        }
        else
        {
            chosen = buckets.SingleOrDefault();
        }

        if (chosen is null)
        {
            throw new InsufficientLiabilityException(LiabilityKind.Refund, command.Amount, 0m, command.TenantId);
        }

        var entryId = await events.PostAsync(
            new RefundIssued(
                command.TenantId, LedgerPostingMaps.Money(command.Amount), command.Date, chosen.BankAccountId, source,
                RefundChecks.Description(command.CheckNumber, source), RefundChecks.SourceRef(command.CheckId),
                chosen.PropertyId, chosen.OwnerId, command.InternalNote),
            ct);
        return new RefundCheckPosted(entryId, chosen.BankAccountId, chosen.PropertyId, chosen.OwnerId);
    }
}
