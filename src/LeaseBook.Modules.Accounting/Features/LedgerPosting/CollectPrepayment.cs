using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Collects a prepayment into a trust bank → <c>PrepaymentReceived</c> (a liability until applied).
/// <para>
/// <c>Description</c> is <b>owner-facing</b>: it prints on the owner statement. <c>InternalNote</c> is
/// staff-only and never reaches an owner or resident (#468). Both are optional.
/// </para>
/// </summary>
public sealed record CollectPrepayment(
    Guid TenantId, decimal Amount, DateOnly Date, Guid BankAccountId, string? Description, string SourceRef,
    string? InternalNote = null)
    : ICommand<PostResult>;

public sealed class CollectPrepaymentValidator : AbstractValidator<CollectPrepayment>
{
    public CollectPrepaymentValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class CollectPrepaymentHandler(ITenantPostingDimensions dimensions, IAccountingEvents events)
    : ICommandHandler<CollectPrepayment, PostResult>
{
    public async Task<PostResult> Handle(CollectPrepayment command, CancellationToken ct)
    {
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, command.Date, ct);
        var id = await events.PostAsync(
            new PrepaymentReceived(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, command.BankAccountId, command.Description ?? "Prepayment", command.SourceRef,
                command.InternalNote),
            ct);
        return new PostResult(id);
    }
}
