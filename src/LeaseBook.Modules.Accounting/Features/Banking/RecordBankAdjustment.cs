using System.Text.Json.Serialization;
using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.Banking;

/// <summary>
/// Records a bank-only adjustment from the register (P65): a service fee, interest, a transfer between
/// two of the org's bank accounts, a PM contribution into a trust bank (#720), a sweep of the PM's
/// held fees from a trust bank to the PM operating bank (#743), or an owner's contribution into an
/// operating trust bank (#744). The <c>Kind</c> selects the posting template; all six route through the
/// existing engine — no new journal write path. A transfer and a sweep name where the money goes in
/// <c>ToBankAccountId</c>; an owner contribution names the owner and the property in <c>OwnerId</c> and
/// <c>PropertyId</c>, and the property must belong to that owner on the contribution's date.
/// <para>
/// <c>Description</c> is the entry's register/ledger text; <c>InternalNote</c> is staff-only (#468).
/// </para>
/// </summary>
// A stale client still sending the pre-#468 `memo` gets a 400, not a post that silently drops its text.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordBankAdjustment(
    string Kind, decimal Amount, DateOnly Date, Guid BankAccountId, Guid? ToBankAccountId,
    string? Description, string SourceRef, string? InternalNote = null,
    Guid? OwnerId = null, Guid? PropertyId = null) : ICommand<PostResult>;

public sealed class RecordBankAdjustmentValidator : AbstractValidator<RecordBankAdjustment>
{
    // Stryker disable once all : a static initializer runs once per test process (docs/testing/mutation.md, Static initializers)
    public static readonly string[] Kinds = ["fee", "interest", "transfer", "contribution", "sweep", "owner-contribution"];

    public RecordBankAdjustmentValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
        RuleFor(x => x.Kind).Must(k => Kinds.Contains(k, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Kind must be one of: {string.Join(", ", Kinds)}.");
        LedgerPostingMaps.RuleForAmount(this, x => x.Amount);

        // A transfer needs a distinct destination account; the other kinds must not carry one, since
        // their handler would drop it. WithMessage covers only the validator before it, so the
        // missing-destination message is attached to both NotNull and NotEqual (#611).
        When(x => IsKind(x, "transfer"), () =>
        {
            RuleFor(x => x.ToBankAccountId)
                .NotNull().WithMessage("A transfer requires a destination bank account.")
                .NotEqual(Guid.Empty).WithMessage("A transfer requires a destination bank account.");
            RuleFor(x => x.ToBankAccountId).NotEqual(x => x.BankAccountId)
                .WithMessage("A transfer's source and destination must differ.");
        });

        // A sweep goes to the PM operating account; which bank that is the posting decides, so a sweep
        // naming its own source is refused there as not a PM operating account (#743).
        When(x => IsKind(x, "sweep"), () =>
        {
            RuleFor(x => x.ToBankAccountId)
                .NotNull().WithMessage("A sweep requires the PM operating account it goes to.")
                .NotEqual(Guid.Empty).WithMessage("A sweep requires the PM operating account it goes to.");
        });

        When(x => !IsKind(x, "transfer") && !IsKind(x, "sweep"), () =>
        {
            RuleFor(x => x.ToBankAccountId).Null()
                .WithMessage("Only a transfer or a sweep names a destination bank account.");
        });

        // An owner contribution is an owner's money for one of their properties (#744); the other kinds
        // are the PM's own and carry neither, since their handlers would drop them.
        When(x => IsKind(x, OwnerContributionKind), () =>
        {
            RuleFor(x => x.OwnerId)
                .NotNull().WithMessage("An owner contribution names the owner it comes from.")
                .NotEqual(Guid.Empty).WithMessage("An owner contribution names the owner it comes from.");
            RuleFor(x => x.PropertyId)
                .NotNull().WithMessage("An owner contribution names the property it is for.")
                .NotEqual(Guid.Empty).WithMessage("An owner contribution names the property it is for.");
        });

        When(x => !IsKind(x, OwnerContributionKind), () =>
        {
            RuleFor(x => x.OwnerId).Null().WithMessage("Only an owner contribution names an owner or a property.");
            RuleFor(x => x.PropertyId).Null().WithMessage("Only an owner contribution names an owner or a property.");
        });
    }

    internal const string OwnerContributionKind = "owner-contribution";

    internal static bool IsKind(RecordBankAdjustment command, string kind) =>
        string.Equals(command.Kind, kind, StringComparison.OrdinalIgnoreCase);
}

internal sealed class RecordBankAdjustmentHandler(
    IAccountingEvents events,
    PostingCalendar calendar,
    IAccountingPeriods periods,
    IReconciliationLock reconciliationLock,
    IPropertyOwnership ownership)
    : ICommandHandler<RecordBankAdjustment, PostResult>
{
    public async Task<PostResult> Handle(RecordBankAdjustment command, CancellationToken ct)
    {
        // A bank fee, interest, transfer, PM contribution, sweep or owner contribution appears on the bank's
        // statement by the day it is recorded (#642).
        await calendar.EnsureNotAfterTodayAsync(command.Date, ct);

        // A closed period or a reconciled month refuses the posting whatever the PM's held fees are, so
        // say that first: the held-fees floor a fee, a transfer or a sweep reads (#553, #743) would
        // otherwise answer a posting into a locked month with a shortfall nobody can fix there. The
        // posting service checks both again; this only orders the refusals, the same way
        // TransferPropertyDeposits does.
        var period = await periods.GetOpenPeriodAsync(command.Date, ct);
        if (period.Status == PeriodStatus.Closed)
        {
            throw new PeriodClosedException(period.Year, period.Month);
        }

        await reconciliationLock.EnsureOpenAsync(command.BankAccountId, command.Date, ct);
        if (command.ToBankAccountId is { } destination)
        {
            await reconciliationLock.EnsureOpenAsync(destination, command.Date, ct);
        }

        // An owner contribution lands on the statement of the owner the property belonged to on its date,
        // after any ownership transfer; naming anyone else would put it on the wrong statement (#744).
        if (RecordBankAdjustmentValidator.IsKind(command, RecordBankAdjustmentValidator.OwnerContributionKind))
        {
            var owner = await ownership.GetOwnerOnAsync(command.PropertyId!.Value, command.Date, ct);
            if (owner != command.OwnerId)
            {
                throw new PropertyNotOwnedException(command.PropertyId.Value, command.OwnerId!.Value, command.Date);
            }
        }

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
            "contribution" => await events.PostAsync(
                new PMContribution(money, command.Date, command.BankAccountId, command.Description ?? "PM contribution",
                    command.SourceRef, command.InternalNote), ct),
            "sweep" => await events.PostAsync(
                new PMFeesSwept(money, command.Date, command.BankAccountId, command.ToBankAccountId!.Value,
                    command.Description ?? "PM fee sweep", command.SourceRef, command.InternalNote), ct),
            RecordBankAdjustmentValidator.OwnerContributionKind => await events.PostAsync(
                new OwnerContribution(command.OwnerId!.Value, command.PropertyId!.Value, money, command.Date,
                    command.BankAccountId, command.Description ?? "Owner contribution", command.SourceRef,
                    command.InternalNote), ct),
            _ => throw new ValidationException("Unknown bank-adjustment kind."), // unreachable past the validator
        };
        return new PostResult(id);
    }
}
