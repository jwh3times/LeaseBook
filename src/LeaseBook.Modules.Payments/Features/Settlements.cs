using FluentValidation;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features;

/// <summary>One line of a payout as staff see it. <see cref="PaymentId"/> is the payment it is about, when it names one LeaseBook knows.</summary>
public sealed record SettlementItemView(string Item, string Kind, Guid? PaymentId, decimal Gross, decimal Fee,
    decimal Net, Guid? EntryId, Guid? FeeEntryId);

/// <summary>
/// A payout batch as staff see it (ADR-053): the bank amount and date, why it is waiting if it is, and
/// its lines. Staff-only; no tenant surface reads a payout.
/// </summary>
public sealed record SettlementView(Guid Id, string PayoutReference, DateOnly BankDate, decimal BankAmount,
    string Status, string? Reason, string? ReasonItem, DateTime CreatedAt, DateTime? PostedAt, string? ReviewNote,
    DateTime? ReviewClosedAt, bool CanPost, bool CanClose, IReadOnlyList<SettlementItemView> Items);

/// <summary>The newest payouts, at most 100, or the one named.</summary>
public sealed record GetSettlements(Guid? Id = null) : IQuery<IReadOnlyList<SettlementView>>;

internal sealed class GetSettlementsHandler(DbContext db) : IQueryHandler<GetSettlements, IReadOnlyList<SettlementView>>
{
    public async Task<IReadOnlyList<SettlementView>> Handle(GetSettlements q, CancellationToken ct)
    {
        var query = db.Set<PaymentSettlement>().AsNoTracking();
        if (q.Id is { } id) { query = query.Where(x => x.Id == id); }
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(100).ToListAsync(ct);
        var ids = rows.Select(x => x.Id).ToArray();
        var items = await db.Set<PaymentSettlementItem>().AsNoTracking().Where(x => ids.Contains(x.SettlementId)).ToListAsync(ct);
        var providerIds = items.Select(x => x.ProviderId).Where(x => x != "").Distinct().ToArray();
        var payments = await db.Set<PaymentOperation>().AsNoTracking()
            .Where(x => x.ProviderId != null && providerIds.Contains(x.ProviderId))
            .ToDictionaryAsync(x => x.ProviderId!, x => x.Id, ct);
        return rows.Select(x => new SettlementView(x.Id, x.PayoutId, x.BankDate, x.BankAmount, x.Status, x.Reason,
            x.ReasonItem, x.CreatedAt, x.PostedAt, x.ReviewNote, x.ReviewClosedAt,
            // Conflicting evidence cannot be posted whatever a person decides; it can only be closed.
            // A payout still marked received is one whose check never finished: it has both ways out.
            CanPost: x.Status is SettlementStatuses.NeedsReview or SettlementStatuses.Received
                && x.Reason != "conflicting_evidence",
            CanClose: x.Status is SettlementStatuses.NeedsReview or SettlementStatuses.Received,
            items.Where(i => i.SettlementId == x.Id).OrderBy(i => i.Item, StringComparer.Ordinal)
                .Select(i => new SettlementItemView(i.Item, i.Kind,
                    payments.TryGetValue(i.ProviderId, out var paymentId) ? paymentId : null,
                    i.Gross, i.Fee, i.Net, i.EntryId, i.FeeEntryId)).ToArray())).ToArray();
    }
}

/// <summary>The outcome of an administrator's action on a payout. <see cref="Refusal"/> is the stable reason it still waits.</summary>
public sealed record SettlementReviewResult(SettlementView Settlement, string? Refusal = null)
{
    /// <summary>What to tell the administrator about a payout that did not post. Staff-facing.</summary>
    public static string Describe(string refusal) => refusal switch
    {
        "settlement_incomplete" =>
            "A line of this payout names a payment LeaseBook cannot identify, so nothing was posted.",
        "settlement_untied" =>
            "The lines of this payout do not add up to the bank amount, or a charge differs from the payment, so nothing was posted.",
        "unsupported_settlement" =>
            "This payout contains something LeaseBook does not post, such as a refund, a dispute, a partial return or a reserve. Correct the ledger, then close the review.",
        "pm_fees_insufficient" =>
            "The management fees held in the account do not cover a processor fee shortfall in this payout, so nothing was posted. Fund the held fees, then post again.",
        "settlement_period_locked" =>
            "The payout's bank date is in a locked period, and the date cannot be changed. Correct the ledger, then close the review.",
        "attribution_unavailable" =>
            "A tenant in this payout has no lease on the bank date, so nothing was posted. Review the lease, then post again.",
        "technical_failure" =>
            "Checking this payout could not finish. Post it to try again, or close the review.",
        "conflicting_evidence" =>
            "The evidence for this payout conflicts with what is already recorded, so nothing was posted. Review it, then close the review.",
        var code when code.StartsWith("return_", StringComparison.Ordinal) =>
            "A return in this payout cannot be posted: " + PaymentReviewResult.Describe(code),
        _ => "Accounting refused this payout, so nothing was posted. Review the ledger, then close the review.",
    };
}

/// <summary>Checks and posts a payout that is waiting, including one that contains a return (ADR-053). Null when unknown.</summary>
public sealed record PostSettlement(FixtureBinding Binding, Guid Id, Guid UserId) : ICommand<SettlementReviewResult?>;

public sealed class PostSettlementValidator : AbstractValidator<PostSettlement>
{
    public PostSettlementValidator()
    {
        RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.UserId).NotEmpty();
    }
}

internal sealed class PostSettlementHandler(SettlementEngine engine, ISender sender)
    : ICommandHandler<PostSettlement, SettlementReviewResult?>
{
    public async Task<SettlementReviewResult?> Handle(PostSettlement c, CancellationToken ct)
    {
        var settlement = await engine.EvaluateAsync(c.Binding, c.Id, c.UserId, ct);
        if (settlement is null) { return null; }
        return new((await sender.Query(new GetSettlements(c.Id), ct)).Single(),
            settlement.Status == SettlementStatuses.Posted ? null : settlement.Reason);
    }
}

/// <summary>Closes a payout's review with a note and no posting (ADR-053). Null when unknown.</summary>
public sealed record CloseSettlementReview(FixtureBinding Binding, Guid Id, Guid UserId, string Note)
    : ICommand<SettlementReviewResult?>;

public sealed class CloseSettlementReviewValidator : AbstractValidator<CloseSettlementReview>
{
    public CloseSettlementReviewValidator()
    {
        RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Note).Must(x => !string.IsNullOrWhiteSpace(x))
            .WithMessage("Enter a note explaining how this was resolved.").MaximumLength(500);
    }
}

internal sealed class CloseSettlementReviewHandler(SettlementEngine engine, ISender sender)
    : ICommandHandler<CloseSettlementReview, SettlementReviewResult?>
{
    public async Task<SettlementReviewResult?> Handle(CloseSettlementReview c, CancellationToken ct)
    {
        if (await engine.CloseAsync(c.Binding, c.Id, c.UserId, c.Note.Trim(), ct) is null) { return null; }
        return new((await sender.Query(new GetSettlements(c.Id), ct)).Single());
    }
}
