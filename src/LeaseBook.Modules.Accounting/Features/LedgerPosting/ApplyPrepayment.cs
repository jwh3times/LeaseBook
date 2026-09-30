using System.Text.Json.Serialization;
using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Applies a held prepayment to the tenant's open charges → <c>PrepaymentApplied</c>. The engine guards
/// against the held prepayment and the open receivable (P51); over-application is <c>insufficient_*</c> (409).
/// <para>
/// <c>Description</c> is <b>owner-facing</b>: it prints on the owner statement. <c>InternalNote</c> is
/// staff-only and never reaches an owner or resident (#468). Both are optional.
/// </para>
/// </summary>
// A stale client still sending the pre-#468 `memo` gets a 400, not a post that silently drops its text.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplyPrepayment(
    Guid TenantId, decimal Amount, DateOnly Date, Guid BankAccountId, string? Description, string SourceRef,
    string? InternalNote = null)
    : ICommand<PostResult>;

public sealed class ApplyPrepaymentValidator : AbstractValidator<ApplyPrepayment>
{
    public ApplyPrepaymentValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class ApplyPrepaymentHandler(ITenantPostingDimensions dimensions, IAccountingEvents events)
    : ICommandHandler<ApplyPrepayment, PostResult>
{
    public async Task<PostResult> Handle(ApplyPrepayment command, CancellationToken ct)
    {
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, command.Date, ct);
        var id = await events.PostAsync(
            new PrepaymentApplied(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, command.BankAccountId, command.Description ?? "Prepayment applied", command.SourceRef,
                command.InternalNote),
            ct);
        return new PostResult(id);
    }
}
