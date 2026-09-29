using System.Text.Json.Serialization;
using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Collects a security deposit into a deposit-trust bank → <c>DepositCollected</c> (a liability, never income).
/// <para>
/// <c>Description</c> is <b>owner-facing</b>: it prints on the owner statement. <c>InternalNote</c> is
/// staff-only and never reaches an owner or resident (#468). Both are optional.
/// </para>
/// </summary>
// A stale client still sending the pre-#468 `memo` gets a 400, not a post that silently drops its text.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CollectDeposit(
    Guid TenantId, decimal Amount, DateOnly Date, Guid DepositBankId, string? Description, string SourceRef,
    string? InternalNote = null)
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
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, command.Date, ct);
        var id = await events.PostAsync(
            new DepositCollected(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, command.DepositBankId, command.Description ?? "Security deposit", command.SourceRef,
                command.InternalNote),
            ct);
        return new PostResult(id);
    }
}
