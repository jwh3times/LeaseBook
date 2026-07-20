using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Applies a held deposit → <c>DepositApplied</c>: as owner income (damages) or against the tenant's
/// open charges. The engine guards against-charges against the open receivable (P51) and both targets
/// against the held deposit; over-application surfaces as <c>insufficient_receivable</c>/<c>insufficient_liability</c> (409).
/// </summary>
public sealed record ApplyDeposit(
    Guid TenantId, decimal Amount, DateOnly Date, Guid DepositBankId, Guid OperatingBankId,
    string Target, string Reason, string SourceRef) : ICommand<PostResult>;

public sealed class ApplyDepositValidator : AbstractValidator<ApplyDeposit>
{
    public ApplyDepositValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DepositBankId).NotEmpty();
        RuleFor(x => x.OperatingBankId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty();
        RuleFor(x => x.Target).Must(LedgerPostingMaps.DepositTargets.ContainsKey)
            .WithMessage($"Target must be one of: {string.Join(", ", LedgerPostingMaps.DepositTargets.Keys)}.");
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class ApplyDepositHandler(ITenantPostingDimensions dimensions, IAccountingEvents events)
    : ICommandHandler<ApplyDeposit, PostResult>
{
    public async Task<PostResult> Handle(ApplyDeposit command, CancellationToken ct)
    {
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, ct);
        var id = await events.PostAsync(
            new DepositApplied(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, command.DepositBankId, command.OperatingBankId,
                LedgerPostingMaps.DepositTargets[command.Target], command.Reason, command.SourceRef),
            ct);
        return new PostResult(id);
    }
}
