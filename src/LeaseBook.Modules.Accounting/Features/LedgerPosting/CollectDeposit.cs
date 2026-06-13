using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>Collects a security deposit into a deposit-trust bank → <c>DepositCollected</c> (a liability, never income).</summary>
public sealed record CollectDeposit(
    Guid TenantId, decimal Amount, DateOnly Date, Guid DepositBankId, string? Memo, string SourceRef)
    : ICommand<PostResult>;

public sealed class CollectDepositValidator : AbstractValidator<CollectDeposit>
{
    public CollectDepositValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DepositBankId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class CollectDepositHandler(ITenantPostingDimensions dimensions, IAccountingEvents events)
    : ICommandHandler<CollectDeposit, PostResult>
{
    public async Task<PostResult> Handle(CollectDeposit command, CancellationToken ct)
    {
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, ct);
        var id = await events.PostAsync(
            new DepositCollected(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, command.DepositBankId, command.Memo ?? "Security deposit", command.SourceRef),
            ct);
        return new PostResult(id);
    }
}
