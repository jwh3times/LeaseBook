using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>Issues a goodwill credit to a tenant → <c>CreditIssued</c> (reduces what the tenant owes and the owner's accrued income).</summary>
public sealed record IssueCredit(
    Guid TenantId, decimal Amount, DateOnly Date, string Reason, string SourceRef) : ICommand<PostResult>;

public sealed class IssueCreditValidator : AbstractValidator<IssueCredit>
{
    public IssueCreditValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty();
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class IssueCreditHandler(ITenantPostingDimensions dimensions, IAccountingEvents events)
    : ICommandHandler<IssueCredit, PostResult>
{
    public async Task<PostResult> Handle(IssueCredit command, CancellationToken ct)
    {
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, ct);
        var id = await events.PostAsync(
            new CreditIssued(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, command.Reason, command.SourceRef),
            ct);
        return new PostResult(id);
    }
}
