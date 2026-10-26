using FluentValidation;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Microsoft.EntityFrameworkCore;
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
            .ShouldBe(("NeedsReview", "settlement_untied", false, true));
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

    // #581, ADR-053 addendum: a payout closed with nothing posted will never bring its payments to the bank.
    [Fact]
    public async Task Closing_a_payout_sends_each_payment_still_waiting_for_it_to_a_person_and_out_of_transit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var paid = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(paid, "Succeeded"), ct);
        var debit = await h.AcceptedAsync(50m, ct, tenant: 1);
        var receipted = await h.AcceptedAsync(30m, ct);
        (await h.SettleAsync(h.Payout("po_paid", 30m, Line("1", "Payment", receipted, 30m, 0m, 30m)), ct)).Status.ShouldBe("Posted");
        var elsewhere = await h.AcceptedAsync(20m, ct);
        await h.EmitAsync(h.Observation(elsewhere, "Succeeded"), ct);
        // Out of retries: in review, and its only way out, a retry, would put it back in processing.
        var stalled = await h.AcceptedAsync(40m, ct);
        for (var i = 0; i <= PaymentEngine.RetrySeconds.Length; i++) { await FailOnceAsync(h, stalled, "technical_failure", ct); }
        (await h.ReadAsync(stalled.Id, ct)).Status.ShouldBe("NeedsReview");
        var rejected = await h.AcceptedAsync(60m, ct);
        await FailOnceAsync(h, rejected, "processor_rejected", ct);
        // Waiting for some other payout to receipt it, and only returned in this one.
        var pending = await h.AcceptedAsync(70m, ct);
        var held = await h.SettleAsync(h.Payout("po_1", 119m, Line("1", "Payment", paid, 100m, 0m, 100m),
            Line("2", "Payment", debit, 50m, 0m, 50m), Line("3", "Return", receipted, 30m, 0m, -30m),
            Line("4", "Payment", stalled, 40m, 0m, 40m), Line("5", "Payment", rejected, 60m, 0m, 60m),
            Line("6", "Return", pending, 70m, 0m, -70m)), ct);
        held.Status.ShouldBe("NeedsReview");
        (await h.QueryAsync(new GetFundsInTransit(), ct)).Sum(x => x.Amount).ShouldBe(120m);
        var receipts = h.Ledger.Receipts.Count;

        await h.SendAsync(new CloseSettlementReview(h.Binding, held.Id, h.UserId, "The processor paid it out again."), ct);

        foreach (var op in new[] { paid, debit, stalled })
        {
            var now = await h.ReadAsync(op.Id, ct);
            (now.Status, now.Reason, now.JournalId).ShouldBe(("NeedsReview", "payout_closed", (Guid?)null));
        }
        // A payment the payout only returned, and a payment it never carried, are left as they were.
        (await h.ReadAsync(receipted.Id, ct)).Status.ShouldBe("Settled");
        var untouched = await h.ReadAsync(elsewhere.Id, ct);
        (untouched.Status, untouched.Reason).ShouldBe(("Processing", (string?)null));
        var awaiting = await h.ReadAsync(pending.Id, ct);
        (awaiting.Status, awaiting.Reason).ShouldBe(("Processing", (string?)null));
        // One in review for a reason of its own stays under it.
        var reviewed = await h.ReadAsync(rejected.Id, ct);
        (reviewed.Status, reviewed.Reason).ShouldBe(("NeedsReview", "processor_rejected"));
        (await h.QueryAsync(new GetFundsInTransit(), ct)).ShouldHaveSingleItem().Amount.ShouldBe(20m);
        h.Ledger.Receipts.Count.ShouldBe(receipts);
        var staff = (await h.QueryAsync(new GetPayments(null, paid.Id), ct)).ShouldHaveSingleItem();
        (staff.Status, staff.Reason, staff.CanCloseReview, staff.CanRetry).ShouldBe(("NeedsReview", "payout_closed", true, false));
        var tenant = (await h.QueryAsync(new GetPayments(h.Tenants[1]), ct)).ShouldHaveSingleItem();
        (tenant.Status, tenant.ReceiptRecorded).ShouldBe(("NeedsReview", false));
    }

    [Fact]
    public async Task A_payment_a_closed_payout_sent_to_a_person_stays_there_short_of_a_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var credited = await h.AcceptedAsync(100m, ct);
        var failed = await h.AcceptedAsync(50m, ct);
        var held = await h.SettleAsync(h.Payout("po_1", 149m, Line("1", "Payment", credited, 100m, 0m, 100m),
            Line("2", "Payment", failed, 50m, 0m, 50m)), ct);
        await h.SendAsync(new CloseSettlementReview(h.Binding, held.Id, h.UserId, "Bank short."), ct);

        // A clean bank credit would post a waiting payment's receipt by itself. This one has a person.
        await h.EmitAsync(h.Observation(credited, "BankCredit"), ct);
        await h.EmitAsync(h.Observation(failed, "Failed"), ct);

        var kept = await h.ReadAsync(credited.Id, ct);
        (kept.Status, kept.Reason, kept.JournalId).ShouldBe(("NeedsReview", "payout_closed", (Guid?)null));
        h.Ledger.Receipts.ShouldBeEmpty();
        var ended = await h.ReadAsync(failed.Id, ct);
        (ended.Status, ended.Reason).ShouldBe(("Failed", "collection_failed"));
        // Nor does a later payout carry it to the bank behind the person's back.
        (await h.SettleAsync(h.Payout("po_2", 100m, Line("1", "Payment", credited, 100m, 0m, 100m)), ct)).Reason
            .ShouldBe("conflicting_evidence");
        (await h.SendAsync(new ClosePaymentReview(h.Binding, credited.Id, h.UserId, "Paid out again; receipted by hand."), ct))!
            .Payment.Status.ShouldBe("ReviewClosed");
    }

    [Theory]
    [InlineData("another account")]
    [InlineData("another bank")]
    public async Task Closing_a_payout_moves_no_payment_it_does_not_answer_for(string story)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var binding = story == "another account" ? await h.RebindAsync(ct, account: "acct_two") : await h.RebindAsync(ct, newBank: true);
        var evidence = h.Payout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)) with
        { Account = binding.Account, BankId = binding.BankId };
        var id = (await h.InOrg(() => h.Settlements.ReceiveAsync(binding, evidence, ct), ct))!.Value;
        // Under another account the payment is not known here; one made for another bank contradicts the payout (#630).
        (await h.InOrg(() => h.Settlements.EvaluateAsync(binding, id, null, ct), ct))!.Reason
            .ShouldBe(story == "another account" ? "settlement_incomplete" : "conflicting_evidence");

        (await h.InOrg(() => h.Settlements.CloseAsync(binding, id, h.UserId, "Not ours.", ct), ct))!.Status.ShouldBe("Closed");

        var kept = await h.ReadAsync(op.Id, ct);
        (kept.Status, kept.Reason).ShouldBe(("Processing", (string?)null));
    }

    /// <summary>
    /// A payment sent to a person is taken from the worker, and rests unless it has a fact to judge: a
    /// payment in review is claimed only for a fact it has not judged, so one left due with none would
    /// be found by the worker at every poll and never claimed. An attempt in flight changes nothing.
    /// </summary>
    [Theory]
    [InlineData("a retry falls due with nothing to judge", true)]
    [InlineData("a retry not yet due", true)]
    [InlineData("the worker holds it and completes", true)]
    [InlineData("the worker holds it and fails", true)]
    [InlineData("a fact to judge", false)]
    [InlineData("the worker holds it to judge a fact", false)]
    public async Task A_payment_a_closed_payout_sent_to_a_person_is_taken_from_the_worker_and_rests_unless_it_has_a_fact_to_judge(
        string story, bool rests)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        // What is said of another payment in the account is not this one's to judge.
        await h.EmitAsync(h.Observation(await h.AcceptedAsync(10m, ct, tenant: 1), "Succeeded"), ct);
        var held = await h.SettleAsync(h.Payout("po_1", 99m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);
        // A technical failure on the way: the payment is due again in a second.
        await FailOnceAsync(h, op, "technical_failure", ct);
        if (story != "a retry not yet due") { h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[0])); }
        if (story.Contains("a fact", StringComparison.Ordinal)) { await h.ObserveAsync(h.Observation(op, "Succeeded"), ct); }
        var lease = story.StartsWith("the worker holds it", StringComparison.Ordinal)
            ? (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct))!.LeaseClaimId : null;
        var before = await h.ReadAsync(op.Id, ct);

        await h.SendAsync(new CloseSettlementReview(h.Binding, held.Id, h.UserId, "Bank short."), ct);

        var after = await h.ReadAsync(op.Id, ct);
        (after.Status, after.Reason, after.LeaseClaimId, after.LeaseUntil).ShouldBe(("NeedsReview", "payout_closed", (Guid?)null, (DateTime?)null));
        (after.DueAt, after.Attempts).ShouldBe(rests ? (Resting, 0) : (before.DueAt, before.Attempts));
        // The attempt that was in flight ends, and changes nothing.
        if (story == "the worker holds it and completes")
        {
            await h.InOrg(() => h.Payments.CompleteAsync(h.Binding, op.Id, lease!.Value, new ProcessorResult("Accepted", ProviderOf(op)), ct), ct);
        }
        if (story == "the worker holds it and fails")
        {
            await h.InOrg(() => h.Payments.FailAsync(h.Binding, op.Id, lease!.Value, "technical_failure", ct), ct);
        }
        (await h.ReadAsync(op.Id, ct)).DueAt.ShouldBe(after.DueAt);
        // The worker takes what is left to judge, and the payment stays with the person, at rest.
        (await h.JudgeAsync(op.Id, ct)).ShouldBe(!rests);
        var judged = await h.ReadAsync(op.Id, ct);
        (judged.Status, judged.Reason, judged.JournalId, judged.DueAt).ShouldBe(("NeedsReview", "payout_closed", (Guid?)null, Resting));
    }

    private static readonly DateTime Resting = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

    // One attempt that ends in a failure with this reason, as the worker's would: due now, claimed, failed.
    private static async Task FailOnceAsync(PaymentsHarness h, PaymentOperation op, string reason, CancellationToken ct)
    {
        await h.InOrg(async () =>
        {
            var row = await h.Scope.Db.Set<PaymentOperation>().SingleAsync(x => x.Id == op.Id, ct);
            row.DueAt = h.Clock.GetUtcNow().UtcDateTime;
            await h.Scope.Db.SaveChangesAsync(ct);
        }, ct);
        var claim = await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct);
        await h.InOrg(() => h.Payments.FailAsync(h.Binding, op.Id, claim!.LeaseClaimId!.Value, reason, ct), ct);
    }

    // #630: a payment made for another bank never becomes this payout's, so the payout contradicts it
    // and can only be closed. One nobody here knows yet may still be accepted, and is offered again.
    [Fact]
    public async Task A_payout_naming_a_payment_made_for_another_bank_contradicts_it_and_is_not_offered_to_post()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var binding = await h.RebindAsync(ct, newBank: true);
        var evidence = h.Payout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)) with { BankId = binding.BankId };
        var id = (await h.InOrg(() => h.Settlements.ReceiveAsync(binding, evidence, ct), ct))!.Value;

        (await h.InOrg(() => h.Settlements.EvaluateAsync(binding, id, null, ct), ct))!.Reason.ShouldBe("conflicting_evidence");

        var view = (await h.QueryAsync(new GetSettlements(id), ct)).ShouldHaveSingleItem();
        (view.Status, view.Reason, view.CanPost, view.CanClose).ShouldBe(("NeedsReview", "conflicting_evidence", false, true));
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Processing");
    }
}
