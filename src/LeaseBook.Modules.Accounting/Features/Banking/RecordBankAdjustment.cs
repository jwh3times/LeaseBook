using System.Text.Json.Serialization;
using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.Banking;

/// <summary>
/// Records a bank-only adjustment from the register (P65): a service fee, interest, or a transfer between
/// two of the org's bank accounts. The <c>Kind</c> selects the posting template; all three route through
/// the existing engine — no new journal write path. Owner-/vendor-/fee-sweep runs are M6, not here.
/// <para>
/// <c>Description</c> is the entry's register/ledger text; <c>InternalNote</c> is staff-only (#468).
/// </para>
/// </summary>
// A stale client still sending the pre-#468 `memo` gets a 400, not a post that silently drops its text.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordBankAdjustment(
    string Kind, decimal Amount, DateOnly Date, Guid BankAccountId, Guid? ToBankAccountId,
    string? Description, string SourceRef, string? InternalNote = null) : ICommand<PostResult>;

public sealed class RecordBankAdjustmentValidator : AbstractValidator<RecordBankAdjustment>
{
    public static readonly string[] Kinds = ["fee", "interest", "transfer"];

    public RecordBankAdjustmentValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        RuleFor(x => x.Kind).Must(k => Kinds.Contains(k, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Kind must be one of: {string.Join(", ", Kinds)}.");
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);

        // A transfer needs a distinct destination account; the other kinds must not carry one.
        When(x => string.Equals(x.Kind, "transfer", StringComparison.OrdinalIgnoreCase), () =>
        {
            RuleFor(x => x.ToBankAccountId).NotNull().NotEqual(Guid.Empty)
                .WithMessage("A transfer requires a destination bank account.");
            RuleFor(x => x.ToBankAccountId).NotEqual(x => x.BankAccountId)
                .WithMessage("A transfer's source and destination must differ.");
        });
    }
}

internal sealed class RecordBankAdjustmentHandler(IAccountingEvents events)
    : ICommandHandler<RecordBankAdjustment, PostResult>
{
    public async Task<PostResult> Handle(RecordBankAdjustment command, CancellationToken ct)
    {
        var money = new Money(command.Amount);
        var id = command.Kind.ToLowerInvariant() switch
        {
            "fee" => await events.PostAsync(
                new BankFeeCharged(money, command.Date, command.BankAccountId, command.Description ?? "Bank fee",
                    command.SourceRef, command.InternalNote), ct),
            "interest" => await events.PostAsync(
                new InterestEarned(money, command.Date, command.BankAccountId, command.Description ?? "Interest",
                    command.SourceRef, command.InternalNote), ct),
            "transfer" => await events.PostAsync(
                new TrustTransfer(money, command.Date, command.BankAccountId, command.ToBankAccountId!.Value,
                    command.Description ?? "Transfer", command.SourceRef, command.InternalNote), ct),
            _ => throw new ValidationException("Unknown bank-adjustment kind."), // unreachable past the validator
        };
        return new PostResult(id);
    }
}
