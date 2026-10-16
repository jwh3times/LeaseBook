using FluentValidation;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// What the payment and payout requests refuse before a handler runs (ADR-046, ADR-053, #490). Each case
/// changes one field of a request that passes and names the field the 400 points at, so the form can put
/// the message beside it; each boundary is tried from both sides.
/// </summary>
public sealed class PaymentValidatorTests
{
    private static readonly FixtureBinding Binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "acct_validator");

    private static readonly SubmitPayment Submit = new(Binding, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, "USD");

    private static readonly GetPaymentQuote Quote = new(Binding, 100m, PaymentMethods.Card);

    private static string[] Refused<T>(AbstractValidator<T> validator, T request) =>
        [.. validator.Validate(request).Errors.Select(e => e.PropertyName).Distinct()];

    private static string MessageFor<T>(AbstractValidator<T> validator, T request) =>
        validator.Validate(request).Errors.ShouldHaveSingleItem().ErrorMessage;

    public static TheoryData<string, SubmitPayment> AcceptedSubmissions => new()
    {
        { "as it stands", Submit },
        { "by card", Submit with { Method = PaymentMethods.Card } },
        { "one cent", Submit with { Amount = 0.01m } },
        { "the ceiling", Submit with { Amount = 10_000m } },
        { "cents", Submit with { Amount = 1234.56m } },
        { "a quoted fee", Submit with { QuotedFee = 2.50m } },
        { "a fee of one cent", Submit with { QuotedFee = 0.01m } },
    };

    [Theory]
    [MemberData(nameof(AcceptedSubmissions))]
    public void A_submission_within_every_limit_passes(string _, SubmitPayment request) =>
        Refused(new SubmitPaymentValidator(), request).ShouldBeEmpty();

    public static TheoryData<string, SubmitPayment, string> RefusedSubmissions => new()
    {
        { "no key", Submit with { Key = Guid.Empty }, "Key" },
        { "no tenant", Submit with { TenantId = Guid.Empty }, "TenantId" },
        { "no user", Submit with { UserId = Guid.Empty }, "UserId" },
        { "another currency", Submit with { Currency = "EUR" }, "Currency" },
        { "no currency", Submit with { Currency = "" }, "Currency" },
        { "nothing", Submit with { Amount = 0m }, "Amount" },
        { "a negative amount", Submit with { Amount = -5m }, "Amount" },
        { "a cent over the ceiling", Submit with { Amount = 10_000.01m }, "Amount" },
        { "a part of a cent", Submit with { Amount = 1.005m }, "Amount" },
        { "a method nobody offers", Submit with { Method = "paypal" }, "Method" },
        { "an upper-case method", Submit with { Method = "CARD" }, "Method" },
        { "a negative fee", Submit with { QuotedFee = -0.01m }, "QuotedFee" },
        { "a fee in part of a cent", Submit with { QuotedFee = 0.005m }, "QuotedFee" },
    };

    [Theory]
    [MemberData(nameof(RefusedSubmissions))]
    public void A_submission_outside_a_limit_is_refused_on_that_field(string _, SubmitPayment request, string field) =>
        Refused(new SubmitPaymentValidator(), request).ShouldBe([field]);

    public static TheoryData<string, SubmitPayment, string> SubmissionMessages => new()
    {
        { "nothing", Submit with { Amount = 0m }, "Enter an amount from $0.01 to $10,000.00." },
        { "a cent over the ceiling", Submit with { Amount = 10_000.01m }, "Enter an amount from $0.01 to $10,000.00." },
        { "a part of a cent", Submit with { Amount = 1.005m }, "Enter an amount with at most two decimal places." },
        { "a method nobody offers", Submit with { Method = "paypal" }, "Choose card or bank debit." },
        { "a fee in part of a cent", Submit with { QuotedFee = 0.005m }, "The fee must be an amount in whole cents." },
    };

    [Theory]
    [MemberData(nameof(SubmissionMessages))]
    public void A_refused_submission_tells_the_tenant_what_to_change(string _, SubmitPayment request, string message) =>
        MessageFor(new SubmitPaymentValidator(), request).ShouldBe(message);

    public static TheoryData<string, GetPaymentQuote, string?> Quotes => new()
    {
        { "as it stands", Quote, null },
        { "by bank debit", Quote with { Method = PaymentMethods.Ach }, null },
        { "one cent", Quote with { Amount = 0.01m }, null },
        { "the ceiling", Quote with { Amount = 10_000m }, null },
        { "nothing", Quote with { Amount = 0m }, "Enter an amount from $0.01 to $10,000.00." },
        { "a cent over the ceiling", Quote with { Amount = 10_000.01m }, "Enter an amount from $0.01 to $10,000.00." },
        { "a part of a cent", Quote with { Amount = 99.999m }, "Enter an amount with at most two decimal places." },
        { "a method nobody offers", Quote with { Method = "wire" }, "Choose card or bank debit." },
    };

    [Theory]
    [MemberData(nameof(Quotes))]
    public void A_quote_is_asked_for_only_an_amount_and_a_method_that_could_be_paid(string _, GetPaymentQuote request, string? message)
    {
        var errors = new GetPaymentQuoteValidator().Validate(request).Errors;
        if (message is null) { errors.ShouldBeEmpty(); }
        else { errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe(message); }
    }

    public static TheoryData<string, string?> Notes => new()
    {
        { "no note", null },
        { "an empty note", "" },
        { "spaces", "   " },
        { "a line break", "\n\t" },
        { "a note too long", new string('a', 501) },
    };

    [Theory]
    [MemberData(nameof(Notes))]
    public void A_review_is_closed_only_with_a_note_that_says_something_and_fits(string _, string? note)
    {
        Refused(new ClosePaymentReviewValidator(), new ClosePaymentReview(Binding, Guid.NewGuid(), Guid.NewGuid(), note!)).ShouldBe(["Note"]);
        Refused(new CloseSettlementReviewValidator(), new CloseSettlementReview(Binding, Guid.NewGuid(), Guid.NewGuid(), note!)).ShouldBe(["Note"]);
    }

    [Theory]
    [InlineData("a word", "Fixed")]
    [InlineData("padded", "  Owner repaid the tenant.  ")]
    [InlineData("the longest note", null)]
    public void A_note_that_says_something_and_fits_closes_a_review(string _, string? note)
    {
        note ??= new string('a', 500);
        Refused(new ClosePaymentReviewValidator(), new ClosePaymentReview(Binding, Guid.NewGuid(), Guid.NewGuid(), note)).ShouldBeEmpty();
        Refused(new CloseSettlementReviewValidator(), new CloseSettlementReview(Binding, Guid.NewGuid(), Guid.NewGuid(), note)).ShouldBeEmpty();
    }

    [Fact]
    public void A_blank_note_is_refused_with_the_message_the_form_shows()
    {
        MessageFor(new ClosePaymentReviewValidator(), new ClosePaymentReview(Binding, Guid.NewGuid(), Guid.NewGuid(), " "))
            .ShouldBe("Enter a note explaining how this was resolved.");
        MessageFor(new CloseSettlementReviewValidator(), new CloseSettlementReview(Binding, Guid.NewGuid(), Guid.NewGuid(), " "))
            .ShouldBe("Enter a note explaining how this was resolved.");
    }

    [Fact]
    public void A_review_action_names_the_payment_or_payout_and_the_person_taking_it()
    {
        Refused(new ClosePaymentReviewValidator(), new ClosePaymentReview(Binding, Guid.Empty, Guid.Empty, "Fixed")).ShouldBe(["Id", "UserId"]);
        Refused(new CloseSettlementReviewValidator(), new CloseSettlementReview(Binding, Guid.Empty, Guid.Empty, "Fixed")).ShouldBe(["Id", "UserId"]);
        Refused(new PostSettlementValidator(), new PostSettlement(Binding, Guid.Empty, Guid.Empty)).ShouldBe(["Id", "UserId"]);
        Refused(new PostSettlementValidator(), new PostSettlement(Binding, Guid.NewGuid(), Guid.Empty)).ShouldBe(["UserId"]);
        Refused(new PostSettlementValidator(), new PostSettlement(Binding, Guid.NewGuid(), Guid.NewGuid())).ShouldBeEmpty();
        Refused(new PostPaymentReturnValidator(), new PostPaymentReturn(Binding, Guid.Empty)).ShouldBe(["Id"]);
        Refused(new PostPaymentReturnValidator(), new PostPaymentReturn(Binding, Guid.NewGuid())).ShouldBeEmpty();
    }
}
