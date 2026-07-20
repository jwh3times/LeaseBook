using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>Records a tenant payment into a trust bank → <c>PaymentReceived</c> (auto-splits receivable vs prepayment).</summary>
public sealed record RecordPayment(
    Guid TenantId, decimal Amount, DateOnly Date, string Method, Guid BankAccountId,
    string? Memo, string SourceRef) : ICommand<PostResult>;

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
        var dims = await LedgerPostingMaps.ResolveAsync(dimensions, command.TenantId, ct);
        var id = await events.PostAsync(
            new PaymentReceived(
                command.TenantId, dims.PropertyId, dims.OwnerId, LedgerPostingMaps.Money(command.Amount),
                command.Date, LedgerPostingMaps.Methods[command.Method], command.BankAccountId,
                command.Memo ?? "Payment", command.SourceRef),
            ct);
        return new PostResult(id);
    }
}
