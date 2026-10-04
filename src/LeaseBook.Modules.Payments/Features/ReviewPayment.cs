using FluentValidation;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Payments.Features;

/// <summary>The outcome of a staff review action. <see cref="Refusal"/> is a stable <c>return_*</c> code.</summary>
public sealed record PaymentReviewResult(PaymentView Payment, string? Refusal = null)
{
    /// <summary>What to tell the administrator about a refused return. Staff-facing; never shown to a tenant.</summary>
    public static string Describe(string refusal) => refusal switch
    {
        "return_prepayment_consumed" =>
            "Part of this payment became prepaid credit that has since been used, so the return cannot be posted. Correct the ledger, then close the review.",
        "return_owner_funds_disbursed" =>
            "Funds from this payment have since left the owner's balance, so the return cannot be posted. Correct the ledger, then close the review.",
        "return_precedes_receipt" =>
            "The return is dated before the payment it returns, so it cannot be posted. Review the evidence, then close the review.",
        "return_period_locked" =>
            "The return is dated in a locked period, and the date cannot be changed. Correct the ledger, then close the review.",
        "return_partial_unsupported" =>
            "Only a full return can be posted. Correct the ledger, then close the review.",
        "return_kind_unsupported" =>
            "A refund or dispute cannot be posted as a return. Correct the ledger, then close the review.",
        "return_conflicting_evidence" =>
            "The return evidence conflicts, so nothing was posted. Review the evidence, then close the review.",
        _ => "Accounting refused the return, so nothing was posted. Review the ledger, then close the review.",
    };
}

/// <summary>Posts a returned payment as the receipt's linked reversal (#490). <c>null</c> when the payment is unknown.</summary>
public sealed record PostPaymentReturn(FixtureBinding Binding, Guid Id) : ICommand<PaymentReviewResult?>;

public sealed class PostPaymentReturnValidator : AbstractValidator<PostPaymentReturn>
{
    public PostPaymentReturnValidator() => RuleFor(x => x.Id).NotEmpty();
}

internal sealed class PostPaymentReturnHandler(PaymentEngine engine, ISender sender)
    : ICommandHandler<PostPaymentReturn, PaymentReviewResult?>
{
    public async Task<PaymentReviewResult?> Handle(PostPaymentReturn c, CancellationToken ct)
    {
        var decision = await engine.PostReturnAsync(c.Binding, c.Id, ct);
        if (decision is null) { return null; }
        return new((await sender.Query(new GetPayments(null, c.Id), ct)).Single(), decision.Refusal);
    }
}

/// <summary>Closes a payment review with a note and no posting (#490). <c>null</c> when the payment is unknown.</summary>
public sealed record ClosePaymentReview(FixtureBinding Binding, Guid Id, Guid UserId, string Note)
    : ICommand<PaymentReviewResult?>;

public sealed class ClosePaymentReviewValidator : AbstractValidator<ClosePaymentReview>
{
    public ClosePaymentReviewValidator()
    {
        RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Note).Must(x => !string.IsNullOrWhiteSpace(x))
            .WithMessage("Enter a note explaining how this was resolved.").MaximumLength(500);
    }
}

internal sealed class ClosePaymentReviewHandler(PaymentEngine engine, ISender sender)
    : ICommandHandler<ClosePaymentReview, PaymentReviewResult?>
{
    public async Task<PaymentReviewResult?> Handle(ClosePaymentReview c, CancellationToken ct)
    {
        if (await engine.CloseReviewAsync(c.Binding, c.Id, c.UserId, c.Note.Trim(), ct) is null) { return null; }
        return new((await sender.Query(new GetPayments(null, c.Id), ct)).Single());
    }
}
