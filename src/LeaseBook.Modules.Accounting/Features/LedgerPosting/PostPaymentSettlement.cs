using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// One item of a processor payout (ADR-053). <see cref="Item"/> identifies it within the payout and
/// becomes part of each entry's source reference. <see cref="FeeDifference"/> is what the bank received
/// short of the item's ledger amount: positive is a shortfall the PM bears, negative a surplus the PM
/// keeps, zero a clean item.
/// </summary>
public abstract record SettlementLine(string Item, decimal FeeDifference);

/// <summary>A tenant payment: the receipt posts for <see cref="Amount"/>, the amount going to the tenant's ledger.</summary>
public sealed record SettlementReceipt(
    string Item, Guid TenantId, decimal Amount, string Method, string Description, decimal FeeDifference = 0m)
    : SettlementLine(Item, FeeDifference);

/// <summary>The bank's return of a payment already receipted: the guarded linked reversal of that receipt (ADR-052).</summary>
public sealed record SettlementReturn(string Item, Guid ReceiptEntryId, decimal FeeDifference = 0m)
    : SettlementLine(Item, FeeDifference);

/// <summary>A processor fee with no payment beside it, such as a return fee: a shortfall of <see cref="Amount"/>.</summary>
public sealed record SettlementFee(string Item, decimal Amount) : SettlementLine(Item, Amount);

/// <summary>An entry a settlement posted. <see cref="Kind"/> is <c>Receipt</c>, <c>Return</c> or <c>FeeDifference</c>.</summary>
public sealed record SettlementPosting(string Item, string Kind, Guid EntryId);

/// <summary>
/// Either every posting of the payout, or the reason nothing was posted: the stable code of the
/// accounting rule that refused (<c>pm_fees_insufficient</c>, <c>return_owner_funds_disbursed</c>,
/// <c>period_closed</c>, …) and the item it refused on.
/// </summary>
public sealed record PaymentSettlementResult(
    IReadOnlyList<SettlementPosting> Postings, string? Refusal = null, string? RefusedItem = null)
{
    public bool Posted => Refusal is null;
}

/// <summary>
/// Posts one processor payout as a batch, completely or not at all (ADR-053). Every entry is dated
/// <see cref="BankDate"/> and lands in the trust bank that received the payout, with a source reference
/// under <c>payout:{PayoutReference}</c> so that the payout reconciles as one statement line.
/// <para>
/// The items post in an order that lets the batch pay for itself: receipts, then fee surpluses, then
/// returns, then fee shortfalls. A shortfall may be covered by a surplus in the same payout, and a
/// return by another tenant's receipt for the same owner. Each entry goes through its own guarded
/// template or command, so the guards see the balances the earlier entries left.
/// </para>
/// <para>
/// A refusal by any accounting rule is a <b>result</b>, not an exception: the entries already posted
/// are undone to a savepoint and the caller's transaction stays usable, so the caller can record why
/// the payout did not post. Anything else propagates and rolls the caller back.
/// </para>
/// </summary>
public sealed record PostPaymentSettlement(
    Guid BankAccountId, DateOnly BankDate, string PayoutReference, IReadOnlyList<SettlementLine> Lines)
    : ICommand<PaymentSettlementResult>;

public sealed class PostPaymentSettlementValidator : AbstractValidator<PostPaymentSettlement>
{
    public PostPaymentSettlementValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.BankDate).NotEmpty();
        RuleFor(x => x.PayoutReference).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Lines).NotEmpty()
            .Must(lines => lines.Select(l => l.Item).Distinct(StringComparer.Ordinal).Count() == lines.Count)
            .WithMessage("Each item of a payout needs its own reference.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Item).NotEmpty().MaximumLength(60);
            line.RuleFor(l => l.FeeDifference).Must(WholeCents)
                .WithMessage("A fee difference must be an amount in whole cents.");
        });
        RuleForEach(x => x.Lines).Must(line => line switch
        {
            SettlementReceipt r => r.TenantId != Guid.Empty && r.Amount > 0m && WholeCents(r.Amount)
                && LedgerPostingMaps.Methods.ContainsKey(r.Method),
            SettlementReturn r => r.ReceiptEntryId != Guid.Empty,
            SettlementFee f => f.Amount > 0m,
            _ => false,
        }).WithMessage("A payout item is incomplete or of an unknown kind.");
    }

    private static bool WholeCents(decimal value) => decimal.Round(value, 2) == value;
}

internal sealed class PostPaymentSettlementHandler(
    DbContext db, IPostingLock postingLock, ITenantPostingDimensions dimensions, IAccountingEvents events,
    IReversalService reversal)
    : ICommandHandler<PostPaymentSettlement, PaymentSettlementResult>
{
    private const string Savepoint = "payment_settlement";

    public async Task<PaymentSettlementResult> Handle(PostPaymentSettlement command, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A payment settlement posts inside the caller's organization transaction.");
        await postingLock.AcquireAsync(ct);

        // Everything tracked before this point is the caller's and survives a refusal; everything
        // tracked after it describes rows the savepoint rollback removes.
        var tracked = db.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        await transaction.CreateSavepointAsync(Savepoint, ct);

        var postings = new List<SettlementPosting>();
        string? current = null;
        try
        {
            foreach (var receipt in command.Lines.OfType<SettlementReceipt>())
            {
                current = receipt.Item;
                var entry = await new RecordPaymentHandler(dimensions, events).Handle(new RecordPayment(
                    receipt.TenantId, receipt.Amount, command.BankDate, receipt.Method, command.BankAccountId,
                    receipt.Description, SourceRef(command, receipt.Item)), ct);
                postings.Add(new(receipt.Item, "Receipt", entry.EntryId));
            }

            foreach (var line in command.Lines.Where(l => l.FeeDifference < 0m))
            {
                current = line.Item;
                postings.Add(await PostFeeDifferenceAsync(command, line, ct));
            }

            foreach (var returned in command.Lines.OfType<SettlementReturn>())
            {
                current = returned.Item;
                var entry = await new ReturnTenantPaymentHandler(db, postingLock, reversal).Handle(new ReturnTenantPayment(
                    returned.ReceiptEntryId, command.BankDate, SourceRef(command, returned.Item),
                    $"Returned in payout {command.PayoutReference}."), ct);
                postings.Add(new(returned.Item, "Return", entry.EntryId));
            }

            foreach (var line in command.Lines.Where(l => l.FeeDifference > 0m))
            {
                current = line.Item;
                postings.Add(await PostFeeDifferenceAsync(command, line, ct));
            }
        }
        catch (Exception refusal) when (refusal is AccountingDomainException or ValidationException)
        {
            await transaction.RollbackToSavepointAsync(Savepoint, ct);
            foreach (var entry in db.ChangeTracker.Entries().Where(e => !tracked.Contains(e.Entity)).ToList())
            {
                entry.State = EntityState.Detached;
            }
            // A tenant with no lease on the bank date fails validation, not an accounting rule.
            var code = refusal is AccountingDomainException domain ? domain.Code : "attribution_unavailable";
            return new PaymentSettlementResult([], code, current);
        }

        await transaction.ReleaseSavepointAsync(Savepoint, ct);
        return new PaymentSettlementResult(postings);
    }

    private async Task<SettlementPosting> PostFeeDifferenceAsync(
        PostPaymentSettlement command, SettlementLine line, CancellationToken ct)
    {
        var shortfall = line.FeeDifference > 0m;
        var entryId = await events.PostAsync(new ProcessorFeeDifference(
            new Money(Math.Abs(line.FeeDifference)),
            shortfall ? FeeDifferenceDirection.Shortfall : FeeDifferenceDirection.Surplus,
            command.BankDate, command.BankAccountId,
            shortfall ? "Processor fee shortfall" : "Processor fee surplus",
            SourceRef(command, line.Item) + ":fee",
            $"Payout {command.PayoutReference}, item {line.Item}."), ct);
        return new(line.Item, "FeeDifference", entryId);
    }

    private static string SourceRef(PostPaymentSettlement command, string item) =>
        $"payout:{command.PayoutReference}:{item}";
}
