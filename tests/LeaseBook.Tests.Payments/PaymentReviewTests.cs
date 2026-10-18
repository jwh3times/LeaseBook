using FluentValidation;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// A person's two answers to a payment in review (#490), dispatched as the staff endpoints dispatch
/// them: post the bank's return, or close the review with a note. Each answers with the payment as
/// staff now see it, and with the stable reason when a return did not post.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentReviewTests(PostgresFixture fixture)
{
    // Settled by a bank credit, then a bank's return of it, in review.
    private static async Task<PaymentOperation> ReturnInReviewAsync(PaymentsHarness h, CancellationToken ct, decimal returned = 100m)
    {
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);
        await h.EmitAsync(h.Observation(op, "Return") with { Gross = returned, Net = returned, BankDate = h.Today.AddDays(3) }, ct);
        return await h.ReadAsync(op.Id, ct);
    }

    [Fact]
    public async Task Posting_a_full_return_answers_with_the_payment_returned_and_the_reversal_it_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await ReturnInReviewAsync(h, ct);

        var result = await h.SendAsync(new PostPaymentReturn(h.Binding, op.Id), ct);

        result.ShouldNotBeNull();
        result.Refusal.ShouldBeNull();
        var reversal = (await h.ReadEffectsAsync(ct)).Single(x => x.Kind == "Return").JournalId;
        (result.Payment.Id, result.Payment.Status, result.Payment.Reason, result.Payment.ReceiptEntryId, result.Payment.ReturnEntryId)
            .ShouldBe((op.Id, "Returned", (string?)null, op.JournalId, (Guid?)reversal));
        (result.Payment.CanPostReturn, result.Payment.CanCloseReview).ShouldBe((false, false));
        h.Ledger.Returns.ShouldHaveSingleItem().ReceiptJournalId.ShouldBe(op.JournalId!.Value);
    }

    [Fact]
    public async Task A_return_the_ledger_refuses_answers_with_its_reason_and_is_still_offered_since_the_ledger_can_be_mended()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await ReturnInReviewAsync(h, ct);
        h.Ledger.ReturnRefusal = "return_prepayment_consumed";

        var result = await h.SendAsync(new PostPaymentReturn(h.Binding, op.Id), ct);

        result!.Refusal.ShouldBe("return_prepayment_consumed");
        (result.Payment.Status, result.Payment.Reason, result.Payment.ReturnEntryId).ShouldBe(("NeedsReview", "return_prepayment_consumed", (Guid?)null));
        (result.Payment.CanPostReturn, result.Payment.CanCloseReview).ShouldBe((true, true));
        (await h.ReadEffectsAsync(ct)).ShouldNotContain(x => x.Kind == "Return");
    }

    [Fact]
    public async Task A_return_of_part_of_the_payment_answers_that_only_a_full_return_posts_and_is_no_longer_offered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await ReturnInReviewAsync(h, ct, returned: 60m);

        var result = await h.SendAsync(new PostPaymentReturn(h.Binding, op.Id), ct);

        result!.Refusal.ShouldBe("return_partial_unsupported");
        (result.Payment.Reason, result.Payment.CanPostReturn, result.Payment.CanCloseReview).ShouldBe(("return_partial_unsupported", false, true));
        h.Ledger.Returns.ShouldBeEmpty();
    }

    [Fact]
    public async Task Closing_a_review_keeps_the_note_as_written_and_answers_with_the_payment_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await ReturnInReviewAsync(h, ct);
        var staff = UuidV7.NewId();

        var result = await h.SendAsync(new ClosePaymentReview(h.Binding, op.Id, staff, "  Owner repaid the tenant directly.  "), ct);

        result!.Refusal.ShouldBeNull();
        (result.Payment.Status, result.Payment.ReviewNote, result.Payment.ReviewClosedAt)
            .ShouldBe(("ReviewClosed", "Owner repaid the tenant directly.", (DateTime?)h.Clock.GetUtcNow().UtcDateTime));
        (result.Payment.CanPostReturn, result.Payment.CanCloseReview).ShouldBe((false, false));
        (await h.ReadAsync(op.Id, ct)).ReviewClosedBy.ShouldBe(staff);
        h.Ledger.Returns.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_payment_unknown_here_is_neither_returned_nor_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);

        (await h.SendAsync(new PostPaymentReturn(h.Binding, UuidV7.NewId()), ct)).ShouldBeNull();
        (await h.SendAsync(new ClosePaymentReview(h.Binding, UuidV7.NewId(), h.UserId, "Nothing to see."), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_review_closed_with_no_note_is_refused_before_anything_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await ReturnInReviewAsync(h, ct);

        var refused = await Should.ThrowAsync<ValidationException>(() => h.SendAsync(new ClosePaymentReview(h.Binding, op.Id, h.UserId, "   "), ct));

        refused.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe("Enter a note explaining how this was resolved.");
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("NeedsReview");
    }

    [Fact]
    public async Task A_technical_failure_is_tried_again_and_never_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, UuidV7.NewId(), 30m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);
        for (var attempt = 0; attempt <= PaymentEngine.RetrySeconds.Length; attempt++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(11));
            var lease = (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct))!.LeaseClaimId!.Value;
            await h.InOrg(() => h.Payments.FailAsync(h.Binding, op.Id, lease, "technical_failure", ct), ct);
        }

        await Should.ThrowAsync<PaymentConflictException>(() => h.SendAsync(new ClosePaymentReview(h.Binding, op.Id, h.UserId, "Gave up."), ct));

        var view = (await h.QueryAsync(new GetPayments(null, op.Id), ct)).ShouldHaveSingleItem();
        (view.Status, view.Reason, view.CanRetry, view.CanCloseReview).ShouldBe(("NeedsReview", "technical_failure", true, false));
    }
}
