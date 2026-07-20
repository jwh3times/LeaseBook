using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>Adds a charge to a tenant → <c>RentCharged</c> (kind <c>rent</c>) or <c>FeeCharged</c> (late/maintenance-recharge/other).</summary>
public sealed record AddCharge(
    Guid TenantId, decimal Amount, DateOnly Date, string Kind, string? Memo, string SourceRef)
    : ICommand<PostResult>;

public sealed class AddChargeValidator : AbstractValidator<AddCharge>
{
    public AddChargeValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        RuleFor(x => x.Kind).Must(LedgerPostingMaps.ChargeKinds.ContainsKey)
            .WithMessage($"Kind must be one of: {string.Join(", ", LedgerPostingMaps.ChargeKinds.Keys)}.");
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class AddChargeHandler(ITenantPostingDimensions dimensions, IAccountingEvents events)
    : ICommandHandler<AddCharge, PostResult>
{
    public async Task<PostResult> Handle(AddCharge command, CancellationToken ct)
    {
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, ct);
        var amount = LedgerPostingMaps.Money(command.Amount);
        var feeKind = LedgerPostingMaps.ChargeKinds[command.Kind];

        var id = feeKind is null
            ? await events.PostAsync(
                new RentCharged(
                    command.TenantId, dims.PropertyId, dims.OwnerId, dims.UnitId, amount, command.Date,
                    command.Memo ?? "Rent", command.SourceRef),
                ct)
            : await events.PostAsync(
                new FeeCharged(
                    command.TenantId, dims.PropertyId, dims.OwnerId, dims.UnitId, amount, command.Date,
                    feeKind.Value, command.Memo ?? DefaultLabel(feeKind.Value), command.SourceRef),
                ct);
        return new PostResult(id);
    }

    private static string DefaultLabel(FeeKind kind) => kind switch
    {
        FeeKind.Late => "Late fee",
        FeeKind.MaintenanceRecharge => "Maintenance recharge",
        _ => "Charge",
    };
}
