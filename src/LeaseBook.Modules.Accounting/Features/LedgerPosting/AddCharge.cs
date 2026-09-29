using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Adds a charge to a tenant → <c>RentCharged</c> (kind <c>rent</c>) or <c>FeeCharged</c> (late/maintenance-recharge/other).
/// <para>
/// <c>Description</c> is <b>owner-facing</b>: it prints on the owner statement. <c>InternalNote</c> is
/// staff-only and never reaches an owner or resident (#468). Both are optional.
/// </para>
/// </summary>
public sealed record AddCharge(
    Guid TenantId, decimal Amount, DateOnly Date, string Kind, string? Description, string SourceRef,
    string? InternalNote = null)
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
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, command.Date, ct);
        var amount = LedgerPostingMaps.Money(command.Amount);
        var feeKind = LedgerPostingMaps.ChargeKinds[command.Kind];

        var id = feeKind is null
            ? await events.PostAsync(
                new RentCharged(
                    command.TenantId, dims.PropertyId, dims.OwnerId, dims.UnitId, amount, command.Date,
                    command.Description ?? "Rent", command.SourceRef, InternalNote: command.InternalNote),
                ct)
            : await events.PostAsync(
                new FeeCharged(
                    command.TenantId, dims.PropertyId, dims.OwnerId, dims.UnitId, amount, command.Date,
                    feeKind.Value, command.Description ?? DefaultLabel(feeKind.Value), command.SourceRef,
                    InternalNote: command.InternalNote),
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
