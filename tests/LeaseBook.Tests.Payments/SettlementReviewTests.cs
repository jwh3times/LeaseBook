using FluentValidation;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;
using static LeaseBook.Tests.Payments.Support.PaymentsHarness;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// An administrator's two answers to a payout that waits (ADR-053), dispatched as the staff endpoints
/// dispatch them: post it, or close its review with a note. Each answers with the payout as staff now
/// see it, and a post that did not post says why in the stable reason the screen explains.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SettlementReviewTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Posting_a_payout_whose_cause_has_gone_posts_it_and_answers_with_it_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        h.Ledger.SettlementRefusal = ("pm_fees_insufficient", "1");
        var waiting = await h.SettleAsync(h.Payout("po_1", 99m, Line("1", "Payment", op, 100m, 1m, 99m)), ct);
        (waiting.Status, waiting.Reason, waiting.ReasonItem).ShouldBe(("NeedsReview", "pm_fees_insufficient", "1"));
        // The held fees were funded since.
        h.Ledger.SettlementRefusal = null;
        var admin = UuidV7.NewId();

        var result = await h.SendAsync(new PostSettlement(h.Binding, waiting.Id, admin), ct);

        result.ShouldNotBeNull();
        result.Refusal.ShouldBeNull();
        (result.Settlement.Id, result.Settlement.Status, result.Settlement.Reason, result.Settlement.CanPost, result.Settlement.CanClose)
            .ShouldBe((waiting.Id, "Posted", (string?)null, false, false));
        result.Settlement.PostedAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime);
        var line = result.Settlement.Items.ShouldHaveSingleItem();
        (line.PaymentId, line.EntryId, line.FeeEntryId).ShouldBe(((Guid?)op.Id, (await h.ReadAsync(op.Id, ct)).JournalId, (await h.ReadItemsAsync(waiting.Id, ct)).Single().FeeEntryId));
        line.FeeEntryId.ShouldNotBeNull();
        (await h.ReadSettlementAsync(waiting.Id, ct)).PostedBy.ShouldBe(admin);

        // Posting it again finds it posted, posts nothing more, and still answers that nothing stopped it.
        (await h.SendAsync(new PostSettlement(h.Binding, waiting.Id, admin), ct))!.Refusal.ShouldBeNull();
        h.Ledger.Settlements.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_payout_that_still_cannot_post_answers_with_the_reason_it_waits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var untied = await h.SettleAsync(h.Payout("po_untied", 49m, Line("1", "Payment", await h.AcceptedAsync(50m, ct), 50m, 0m, 50m)), ct);
        var refused = await h.AcceptedAsync(30m, ct);
        // The bank date's period is locked, and stays locked.
        h.Ledger.SettlementRefusal = ("settlement_period_locked", null);
        var period = await h.SettleAsync(h.Payout("po_period", 30m, Line("1", "Payment", refused, 30m, 0m, 30m)), ct);

        var stillUntied = await h.SendAsync(new PostSettlement(h.Binding, untied.Id, h.UserId), ct);
        var stillLocked = await h.SendAsync(new PostSettlement(h.Binding, period.Id, h.UserId), ct);

        stillUntied!.Refusal.ShouldBe("settlement_untied");
        (stillUntied.Settlement.Status, stillUntied.Settlement.Reason, stillUntied.Settlement.CanPost, stillUntied.Settlement.CanClose)
            .ShouldBe(("NeedsReview", "settlement_untied", true, true));
        stillLocked!.Refusal.ShouldBe("settlement_period_locked");
        stillLocked.Settlement.PostedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_payout_no_bank_has_spoken_for_is_not_posted_and_answers_that_the_bank_has_not_confirmed_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = (await h.InOrg(() => h.Settlements.ReceivePayoutAsync(h.Binding,
            h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct), ct))!.Value;

        var result = await h.SendAsync(new PostSettlement(h.Binding, id, h.UserId), ct);

        result!.Refusal.ShouldBe(SettlementReasons.BankEvidenceMissing);
        (result.Settlement.Status, result.Settlement.Reason, result.Settlement.BankAmount, result.Settlement.ProcessorAmount)
            .ShouldBe(("AwaitingBank", (string?)null, (decimal?)null, (decimal?)100m));
        h.Ledger.Settlements.ShouldBeEmpty();
    }

    [Fact]
    public async Task Closing_a_payouts_review_keeps_the_note_as_written_and_offers_nothing_more()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var untied = await h.SettleAsync(h.Payout("po_untied", 49m, Line("1", "Payment", await h.AcceptedAsync(50m, ct), 50m, 0m, 50m)), ct);
        var admin = UuidV7.NewId();

        var result = await h.SendAsync(new CloseSettlementReview(h.Binding, untied.Id, admin, "  The processor refunded the dollar.  "), ct);

        result!.Refusal.ShouldBeNull();
        (result.Settlement.Status, result.Settlement.ReviewNote, result.Settlement.ReviewClosedAt, result.Settlement.CanPost, result.Settlement.CanClose)
            .ShouldBe(("Closed", "The processor refunded the dollar.", (DateTime?)h.Clock.GetUtcNow().UtcDateTime, false, false));
        (await h.ReadSettlementAsync(untied.Id, ct)).ReviewClosedBy.ShouldBe(admin);
        h.Ledger.Settlements.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_posted_payout_cannot_be_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var posted = await h.SettleAsync(h.Payout("po_1", 20m, Line("1", "Payment", await h.AcceptedAsync(20m, ct), 20m, 0m, 20m)), ct);

        await Should.ThrowAsync<PaymentConflictException>(() =>
            h.SendAsync(new CloseSettlementReview(h.Binding, posted.Id, h.UserId, "Too late."), ct));
        (await h.ReadSettlementAsync(posted.Id, ct)).Status.ShouldBe("Posted");
    }

    [Fact]
    public async Task A_payout_unknown_here_is_neither_posted_nor_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);

        (await h.SendAsync(new PostSettlement(h.Binding, UuidV7.NewId(), h.UserId), ct)).ShouldBeNull();
        (await h.SendAsync(new CloseSettlementReview(h.Binding, UuidV7.NewId(), h.UserId, "Nothing to see."), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_payouts_review_closed_with_no_note_is_refused_before_anything_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var untied = await h.SettleAsync(h.Payout("po_untied", 49m, Line("1", "Payment", await h.AcceptedAsync(50m, ct), 50m, 0m, 50m)), ct);

        var refused = await Should.ThrowAsync<ValidationException>(() =>
            h.SendAsync(new CloseSettlementReview(h.Binding, untied.Id, h.UserId, ""), ct));

        refused.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe("Enter a note explaining how this was resolved.");
        (await h.ReadSettlementAsync(untied.Id, ct)).Status.ShouldBe("NeedsReview");
    }
}
