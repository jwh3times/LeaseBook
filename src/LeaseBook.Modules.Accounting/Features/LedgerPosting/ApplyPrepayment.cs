using System.Text.Json.Serialization;
using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Refunds;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Applies a held prepayment to the tenant's open charges → <c>PrepaymentApplied</c>. The engine guards
/// against the held prepayment and the open receivable (P51); over-application is <c>insufficient_*</c> (409).
/// <para>
/// The bank is the one that holds the prepayment (#475), as it is for a refund (#308): left out, it is
/// derived from the held prepayment — with several holding banks the caller must name one
/// (<c>prepayment_bank_ambiguous</c>). A named bank must itself hold the amount, or the template refuses it
/// as <c>insufficient_liability</c>.
/// </para>
/// <para>
/// <c>Description</c> is <b>owner-facing</b>: it prints on the owner statement. <c>InternalNote</c> is
/// staff-only and never reaches an owner or resident (#468). Both are optional.
/// </para>
/// </summary>
// A stale client still sending the pre-#468 `memo` gets a 400, not a post that silently drops its text.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplyPrepayment(
    Guid TenantId, decimal Amount, DateOnly Date, Guid? BankAccountId, string? Description, string SourceRef,
    string? InternalNote = null)
    : ICommand<PostResult>;

public sealed class ApplyPrepaymentValidator : AbstractValidator<ApplyPrepayment>
{
    public ApplyPrepaymentValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        // NotEmpty on a Guid? rejects only null, so an explicit Guid.Empty needs its own rule.
        RuleFor(x => x.BankAccountId).Must(id => id != Guid.Empty)
            .WithMessage("'Bank Account Id' must not be empty.");
        RuleFor(x => x.SourceRef).NotEmpty();
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class ApplyPrepaymentHandler(
    ITenantPostingDimensions dimensions, DbContext db, IPostingLock postingLock, IAccountingEvents events)
    : ICommandHandler<ApplyPrepayment, PostResult>
{
    public async Task<PostResult> Handle(ApplyPrepayment command, CancellationToken ct)
    {
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, command.Date, ct);
        var bankAccountId = command.BankAccountId ?? await HoldingBankAsync(command, ct);
        var id = await events.PostAsync(
            new PrepaymentApplied(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, bankAccountId, command.Description ?? "Prepayment applied", command.SourceRef,
                command.InternalNote),
            ct);
        return new PostResult(id);
    }

    /// <summary>
    /// The single bank holding the tenant's prepayment. Read under the per-org lock the template takes, so
    /// the bank chosen here is the one its per-bank guard re-reads (the lock is re-entrant).
    /// </summary>
    private async Task<Guid> HoldingBankAsync(ApplyPrepayment command, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);
        var banks = (await RefundChecks.HeldBucketsAsync(db, command.TenantId, RefundSource.Prepayments, ct))
            .Select(b => b.BankAccountId)
            .ToArray();

        return banks.Length switch
        {
            0 => throw new InsufficientLiabilityException(LiabilityKind.Prepayment, command.Amount, 0m, command.TenantId),
            1 => banks[0],
            _ => throw new PrepaymentBankAmbiguousException(command.TenantId),
        };
    }
}
