using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Records a payment a processor has settled, on the date its bank evidence gives → <c>PaymentReceived</c>,
/// exactly as <see cref="RecordPayment"/> posts it (ADR-046, #642).
/// <para>
/// It exists so <see cref="RecordPayment"/> can refuse a date after the organization's today without
/// refusing the processor. The bank's date is evidence, bounded by the processor's own rules, and those
/// compare it with the UTC date; in a US evening that is the organization's tomorrow. No route maps
/// this command: only the payment engine's ledger adapter sends it.
/// </para>
/// </summary>
public sealed record RecordSettledReceipt(
    Guid TenantId, decimal Amount, DateOnly Date, string Method, Guid BankAccountId,
    string? Description, string SourceRef) : ICommand<PostResult>;

public sealed class RecordSettledReceiptValidator : AbstractValidator<RecordSettledReceipt>
{
    public RecordSettledReceiptValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        RuleFor(x => x.Method).Must(LedgerPostingMaps.Methods.ContainsKey)
            .WithMessage($"Method must be one of: {string.Join(", ", LedgerPostingMaps.Methods.Keys)}.");
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);
    }
}

internal sealed class RecordSettledReceiptHandler(ITenantPostingDimensions dimensions, IAccountingEvents events)
    : ICommandHandler<RecordSettledReceipt, PostResult>
{
    public Task<PostResult> Handle(RecordSettledReceipt command, CancellationToken ct) =>
        RecordPaymentHandler.PostAsync(dimensions, events, new RecordPayment(
            command.TenantId, command.Amount, command.Date, command.Method, command.BankAccountId,
            command.Description, command.SourceRef), ct);
}
