using System.Text.Json.Serialization;
using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Records a tenant payment into a trust bank → <c>PaymentReceived</c> (auto-splits receivable vs prepayment).
/// <para>
/// <c>Description</c> is <b>owner-facing</b>: it prints on the owner statement. <c>InternalNote</c> is
/// staff-only and never reaches an owner or resident (#468). Both are optional.
/// </para>
/// </summary>
// A stale client still sending the pre-#468 `memo` gets a 400, not a post that silently drops its text.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordPayment(
    Guid TenantId, decimal Amount, DateOnly Date, string Method, Guid BankAccountId,
    string? Description, string SourceRef, string? InternalNote = null) : ICommand<PostResult>;

public sealed class RecordPaymentValidator : AbstractValidator<RecordPayment>
{
    public RecordPaymentValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        RuleFor(x => x.Method).Must(LedgerPostingMaps.Methods.ContainsKey)
            .WithMessage($"Method must be one of: {string.Join(", ", LedgerPostingMaps.Methods.Keys)}.");
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class RecordPaymentHandler(ITenantPostingDimensions dimensions, IAccountingEvents events)
    : ICommandHandler<RecordPayment, PostResult>
{
    public async Task<PostResult> Handle(RecordPayment command, CancellationToken ct)
    {
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, command.Date, ct);
        var id = await events.PostAsync(
            new PaymentReceived(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, LedgerPostingMaps.Methods[command.Method], command.BankAccountId,
                command.Description ?? "Payment", command.SourceRef, command.InternalNote),
            ct);
        return new PostResult(id);
    }
}
