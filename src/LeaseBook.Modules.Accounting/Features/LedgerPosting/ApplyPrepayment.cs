using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Applies a held prepayment to the tenant's open charges → <c>PrepaymentApplied</c>. The engine guards
/// against the held prepayment and the open receivable (P51); over-application is <c>insufficient_*</c> (409).
/// </summary>
public sealed record ApplyPrepayment(
    Guid TenantId, decimal Amount, DateOnly Date, Guid BankAccountId, string? Memo, string SourceRef)
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
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, ct);
        var id = await events.PostAsync(
            new PrepaymentApplied(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, command.BankAccountId, command.Memo ?? "Prepayment applied", command.SourceRef),
            ct);
        return new PostResult(id);
    }
}
