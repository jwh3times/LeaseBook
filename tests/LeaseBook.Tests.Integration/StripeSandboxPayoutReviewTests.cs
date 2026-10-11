using System.Net;
using System.Text.Json.Nodes;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// What the independent review of step 7 found (#513): a payout Stripe has not finished reconciling
// is not read yet, because its list of lines may not be whole.
public sealed partial class StripeSandboxProcessorTests
{
    private const string PayoutObject = "/v1/payouts/" + RecordedPayout;

    // Stripe documents `completed` as the point at which a payout's balance lines can be listed in
    // full. An event that does not say so is not grounds to list them: Stripe is asked for the payout
    // as it is now, by its id, and the lines are read only when that says they are ready.
    [Theory]
    [InlineData("in_progress", "in_progress", false)]
    [InlineData("in_progress", "not_applicable", false)]
    [InlineData("in_progress", null, false)]
    [InlineData(null, "in_progress", false)]
    [InlineData("in_progress", "completed", true)]
    [InlineData(null, "completed", true)]
    public async Task A_paid_payout_is_read_only_when_stripe_says_its_lines_can_be_listed_in_full(string? onEvent, string? now, bool read)
    {
        var h = new Adapter();
        var answers = RecordedPayoutAnswers();
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? Page(false, Unreconciled(onEvent))
            : sent.Path == PayoutObject ? (200, Reconciled(now).ToJsonString()) : answers(sent);

        if (!read)
        {
            var thrown = await Should.ThrowAsync<Exception>(() => ReadPayout(h, Unreconciled(onEvent)));
            thrown.ShouldNotBeOfType<PaymentConflictException>();
            thrown.ShouldNotBeOfType<PaymentUnavailableException>();
            // Asked what the payout is now, and its lines never.
            h.Stripe.Requests.Select(x => x.Path).ShouldBe([PayoutObject]);
            // In a sweep it is passed over and counted, so that the same window is asked for again.
            var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);
            (swept.Payouts.Count, swept.Listed, swept.Unreadable, swept.Unasked).ShouldBe((0, 1, 0, 1));
            h.Stripe.Requests.ShouldAllBe(x => x.Path != Lines);
        }
        else
        {
            var payout = (await ReadPayout(h, Unreconciled(onEvent))).Value.ShouldNotBeNull();
            // The same payout a completed event reads, whichever way it was learned that the lines were ready.
            var recorded = new Adapter { };
            recorded.Stripe.Answer = RecordedPayoutAnswers();
            var asRecorded = (await ReadPayout(recorded, PayoutEvent("payout.paid"))).Value.ShouldNotBeNull();
            (payout with { Items = [], Generation = default, BankId = default }).ShouldBe(asRecorded with { Items = [], Generation = default, BankId = default });
            payout.Items.ShouldBe(asRecorded.Items);
            h.Stripe.Requests.Select(x => x.Path).ShouldBe([PayoutObject, Lines, "/v1/charges/py_synthetic000002", "/v1/charges/py_synthetic000005"]);
            (await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct)).Payouts.ShouldHaveSingleItem().Items.Count.ShouldBe(15);
        }
        ShouldCarryOnlyPayoutIds(h.Stripe.Requests.Where(x => x.Path != "/v1/events"), h.Binding.Account, RecordedPayout);
        // The recorded event says completed, and asks nothing about the payout itself.
        ((string)PayoutEvent("payout.paid")["data"]!["object"]!["reconciliation_status"]!).ShouldBe("completed");
    }

    // What Stripe answers about the payout is held to what the event is held to. Nothing that is not
    // plainly this payout, in test mode, saying its lines are ready, is grounds to list them.
    [Theory]
    [InlineData("stripe has no such payout")]
    [InlineData("stripe faults")]
    [InlineData("not json")]
    [InlineData("another payout")]
    [InlineData("not a payout")]
    [InlineData("marked live")]
    public async Task A_payout_stripe_cannot_be_asked_about_or_answers_wrongly_for_is_not_read(string difference)
    {
        var h = new Adapter();
        var now = Reconciled("completed");
        (int, string) answer = (200, "");
        switch (difference)
        {
            case "stripe has no such payout": answer = (404, Error("invalid_request_error", "resource_missing")); break;
            case "stripe faults": answer = (500, Error("api_error", null)); break;
            case "not json": answer = (200, "<html>"); break;
            case "another payout": now["id"] = "po_synthetic000002"; answer = (200, now.ToJsonString()); break;
            case "not a payout": now["object"] = "transfer"; answer = (200, now.ToJsonString()); break;
            case "marked live": now["livemode"] = true; answer = (200, now.ToJsonString()); break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }
        h.Stripe.Answer = sent => sent.Path == PayoutObject ? answer : throw new InvalidOperationException("Unexpected request " + sent.Path);

        // A live answer refuses the event, as anything live does. Anything else is thrown, to be asked again.
        if (difference == "marked live") { (await ReadPayout(h, Unreconciled("in_progress"))).ShouldBe(ProcessorRead<ProcessorPayout>.Of(null)); }
        else
        {
            var thrown = await Should.ThrowAsync<Exception>(() => ReadPayout(h, Unreconciled("in_progress")));
            thrown.ShouldNotBeOfType<PaymentConflictException>();
            thrown.ShouldNotBeOfType<PaymentUnavailableException>();
        }

        h.Stripe.Requests.Select(x => x.Path).ShouldBe([PayoutObject]);
    }

    // A failed payout and a manual one are read without lines, so neither waits on a reconciliation.
    [Fact]
    public async Task A_failed_or_manual_payout_does_not_wait_for_reconciliation_and_asks_nothing()
    {
        var h = new Adapter();
        var failed = FailedPayout(Unreconciled("in_progress"));
        var manual = Unreconciled("in_progress");
        manual["data"]!["object"]!["automatic"] = false;

        (await ReadPayout(h, failed)).Value.ShouldNotBeNull().State.ShouldBe("Failed");
        (await ReadPayout(h, manual)).Value.ShouldNotBeNull().PayoutType.ShouldBe("manual");

        h.Stripe.Requests.ShouldBeEmpty();
    }

    /// <summary>The recorded paid event, saying something other than that the payout's reconciliation is complete, or not saying.</summary>
    internal static JsonObject Unreconciled(string? status)
    {
        var paid = PayoutEvent("payout.paid");
        var payout = paid["data"]!["object"]!.AsObject();
        if (status is null) { payout.Remove("reconciliation_status"); } else { payout["reconciliation_status"] = status; }
        return paid;
    }

    /// <summary>The recorded payout as Stripe returns one asked for by its id, with the reconciliation status given.</summary>
    internal static JsonObject Reconciled(string? status)
    {
        var payout = (JsonObject)PayoutEvent("payout.paid")["data"]!["object"]!.DeepClone();
        if (status is null) { payout.Remove("reconciliation_status"); } else { payout["reconciliation_status"] = status; }
        return payout;
    }
}

// What the independent review of step 7 found, in a whole sandbox host.
public sealed partial class StripeSandboxHostTests
{
    // A payout in review is a person's. Stripe's paid event arrives again, redelivered or found by
    // the sweep every few minutes, and before the fix each arrival checked the payout again: once the
    // cause had gone it posted, with nobody having asked. Only an administrator's post rechecks it.
    [Theory]
    [InlineData("pm_fees_insufficient", "delivered again")]
    [InlineData("pm_fees_insufficient", "swept")]
    [InlineData("technical_failure", "delivered again")]
    [InlineData("technical_failure", "swept")]
    public async Task A_banked_payout_in_review_is_rechecked_only_by_an_administrator_and_never_by_its_paid_event_arriving_again(string reason, string how)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing", Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var first = await Paid(h, 200m, ct);
        var second = await Paid(h, 300m, ct);
        await Sweep(h, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 496.00m), PaymentLine(h, first, 1.60m), PaymentLine(h, second, 2.40m));
        var paid = PaidPayout(Payout, 496.00m);
        (await h.Deliver(paid, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        // The cause: nothing held to bear the fee shortfall, or the check failing where it locks the first payment.
        var failing = "reference:" + Sandbox.IdFor(first.Id);
        if (reason == "technical_failure") { await HoldFees(h, 10m, ct); h.Commands.FailingLocks[failing] = 0; }

        (await Bank(h, Payout, 496.00m, ct)).ShouldBe(0);

        var held = (await Stored(h, Payout, ct)).Payout;
        (held.Status, held.Reason, held.Banked).ShouldBe(("NeedsReview", reason, true));
        // The cause goes away, and no administrator acts.
        if (reason == "technical_failure") { h.Commands.FailingLocks.TryRemove(failing, out _).ShouldBeTrue(); } else { await HoldFees(h, 10m, ct); }
        var entries = await h.JournalEntries(ct);
        sandbox.Events = _ => StripeSandboxProcessorTests.Page(false, paid);

        for (var arrival = 0; arrival < 2; arrival++)
        {
            if (how == "swept") { h.Clock.Advance(SweepInterval); await Sweep(h, ct); }
            else { (await h.Deliver(paid, ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        }

        if (how == "swept") { sandbox.Sweeps.Count.ShouldBe(3); }
        var after = (await Stored(h, Payout, ct)).Payout;
        (after.Status, after.Reason, after.PostedAt, after.PostedBy).ShouldBe(("NeedsReview", reason, null, null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Effects(h, ct)).ShouldBe(0);
        (await h.Operation(first.Id, ct)).Status.ShouldBe("Processing");
        // The same bank evidence again does not recheck it either.
        (await Bank(h, Payout, 496.00m, ct)).ShouldBe(0);
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // An administrator does, and it posts under their name: the cause really had gone.
        var administrator = UuidV7.NewId();
        var posted = await Post(h, held.Id, ct, administrator);
        (posted.Refusal, posted.Settlement.Status).ShouldBe((null, "Posted"));
        (await Stored(h, Payout, ct)).Payout.PostedBy.ShouldBe(administrator);
        (await h.JournalEntries(ct)).ShouldBe(entries + 4);
    }

    // Zero is an amount a payout can come to, and no evidence is not evidence of zero: a held
    // payout's bank amount reads 0.00 too. It is held like any other and posts only when a bank says 0.00.
    [Fact]
    public async Task A_payout_whose_lines_net_to_zero_is_held_until_bank_evidence_of_zero_and_is_never_taken_as_banked()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 25m, ct);
        var returned = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, returned, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);
        sandbox.HoldsCharge(Charge(h, returned, "052-ach-dispute-charge.json"));
        (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), returned), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        var other = await Paid(h, 215m, ct);
        var entries = await h.JournalEntries(ct);
        // 215.00 in, 200.00 taken back, 15.00 for the dispute: nothing leaves Stripe for the bank.
        const string zero = "po_host000000";
        sandbox.Lines[zero] = LinesOf(PayoutLine(zero, 0m), PaymentLine(h, other, 0m), DisputeLine(returned, "txn_dispute"));

        (await h.Deliver(PaidPayout(zero, 0m, "evt_zero_payout"), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        var (held, items) = await Stored(h, zero, ct);
        (held.Status, held.Reason, held.ProcessorAmount, held.BankAmount, held.Banked).ShouldBe(("AwaitingBank", null, 0m, 0m, false));
        items.Sum(x => x.Net).ShouldBe(0m);
        var view = await View(h, held.Id, ct);
        (view.CanPost, view.CanClose).ShouldBe((false, true));
        // An administrator cannot post it on the strength of a zero nobody gave.
        (await Post(h, held.Id, ct)).Refusal.ShouldBe("bank_evidence_missing");
        (await h.Deliver(PaidPayout(zero, 0m, "evt_zero_payout"), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await Stored(h, zero, ct)).Payout.Status.ShouldBe("AwaitingBank");
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // A bank that says another amount is a mismatch like any other; one that says zero releases it to a person.
        (await Bank(h, zero, 0m, ct)).ShouldBe(0);
        var banked = (await Stored(h, zero, ct)).Payout;
        (banked.Status, banked.Reason, banked.Banked, banked.BankAmount).ShouldBe(("NeedsReview", "settlement_requires_confirmation", true, 0m));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Post(h, held.Id, ct)).Settlement.Status.ShouldBe("Posted");
        // The receipt, the reversal and the dispute fee.
        (await h.JournalEntries(ct)).ShouldBe(entries + 3);
    }

    // The bank's evidence is stored in one transaction and the payout checked in the next. Stripe's
    // word that the payout failed can land between the two, and then the check must post nothing.
    [Fact]
    public async Task A_payout_that_fails_between_its_bank_evidence_and_its_check_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        var paid = PaidPayout(Payout, 198.40m);
        (await h.Deliver(paid, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        var entries = await h.JournalEntries(ct);
        var id = (await Stored(h, Payout, ct)).Payout.Id;
        // The first half of what the bank command does: the evidence, kept, and the payout ready to be checked.
        await h.InOrg(async sp => (await sp.GetRequiredService<SettlementEngine>().ReceiveBankEvidenceAsync(h.Binding,
            new BankEvidence(Payout, Account, PaymentModes.StripeSandbox, h.Binding.Generation, 198.40m, Today(h), "bank-between"), ct))
            .ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Stored, id, true)), ct);
        (await Stored(h, Payout, ct)).Payout.Status.ShouldBe("Received");

        (await h.Deliver(StripeSandboxProcessorTests.FailedPayout(paid), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        // The second half, as the runner makes it: no administrator, and nothing to post.
        await Should.ThrowAsync<PaymentConflictException>(() => h.InOrg(sp => sp.GetRequiredService<SettlementEngine>().EvaluateAsync(h.Binding, id, null, ct), ct));
        (await h.Deliver(paid, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await Should.ThrowAsync<PaymentConflictException>(() => Post(h, id, ct));
        var after = (await Stored(h, Payout, ct)).Payout;
        (after.Status, after.Reason, after.Banked, after.PostedAt).ShouldBe(("NeedsReview", "payout_failed", true, null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing");
    }

    // A bank cannot have received tomorrow what it has not received yet, and a receipt dated ahead
    // would sit in a period nobody has reached. Refused by the command, and by the engine whoever calls it.
    [Fact]
    public async Task Bank_evidence_dated_after_today_is_refused_by_the_command_and_by_the_engine()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        var entries = await h.JournalEntries(ct);

        (await Bank(h, Payout, 198.40m, ct, Today(h).AddDays(1))).ShouldBe(1);
        (await Bank(h, Payout, 198.40m, ct, Today(h).AddYears(1))).ShouldBe(1);
        await h.InOrg(async sp => (await sp.GetRequiredService<SettlementEngine>().ReceiveBankEvidenceAsync(h.Binding,
            new BankEvidence(Payout, Account, PaymentModes.StripeSandbox, h.Binding.Generation, 198.40m, Today(h).AddDays(1), "bank-tomorrow"), ct))
            .ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Malformed)), ct);

        var payout = (await Stored(h, Payout, ct)).Payout;
        (payout.Status, payout.Banked, payout.BankDate).ShouldBe(("AwaitingBank", false, default));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        // Today, and a day already past, are taken: evidence was not used up by the refusals.
        (await Bank(h, Payout, 198.40m, ct, Today(h).AddDays(-1))).ShouldBe(0);
        (await Stored(h, Payout, ct)).Payout.Status.ShouldBe("Posted");
    }

    // Not only the engine's rule: the table refuses a payout marked posted that holds no bank evidence.
    [Fact]
    public async Task The_database_refuses_a_posted_payout_that_holds_no_bank_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(new Sandbox().Replay, ct);
        PaymentSettlement Forged(string payoutId, string status, string evidence) => new()
        {
            Id = UuidV7.NewId(),
            Generation = h.Binding.Generation,
            Account = Account,
            Mode = PaymentModes.StripeSandbox,
            PayoutId = payoutId,
            BankId = h.Binding.BankId,
            ProcessorAmount = 10m,
            Fingerprint = new string('0', 64),
            Status = status,
            EvidenceId = evidence,
        };

        var refused = await Should.ThrowAsync<DbUpdateException>(() => h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(Forged("po_forged", SettlementStatuses.Posted, ""));
            await db.SaveChangesAsync(ct);
        }, ct));
        refused.InnerException.ShouldBeOfType<Npgsql.PostgresException>().ConstraintName.ShouldBe("ck_payment_settlement_posted_banked");

        // Held without evidence, and posted with it, are both rows the table takes.
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.AddRange(Forged("po_held", SettlementStatuses.AwaitingBank, ""), Forged("po_posted", SettlementStatuses.Posted, "bank-x"));
            await db.SaveChangesAsync(ct);
        }, ct);
        (await Payouts(h, ct)).Select(x => x.PayoutId).ShouldBe(["po_held", "po_posted"], ignoreOrder: true);
    }

    // The paid event came before Stripe had finished reconciling the payout. It is not acknowledged
    // and nothing is kept; the sweep asks again, and keeps the payout once Stripe says its lines are whole.
    [Fact]
    public async Task A_paid_payout_stripe_has_not_finished_reconciling_is_not_stored_until_it_has()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing", Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        await Sweep(h, ct);
        var entries = await h.JournalEntries(ct);
        // Listed now, the lines are short of the payout: one payment is not yet among them.
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m));
        var paid = PaidPayout(Payout, 198.40m);
        var payout = paid["data"]!["object"]!.AsObject();
        payout["reconciliation_status"] = "in_progress";
        sandbox.Payouts[Payout] = (200, payout.ToJsonString());

        var delivered = await h.Deliver(paid, ct);

        delivered.Status.ShouldBe(HttpStatusCode.ServiceUnavailable, delivered.Body);
        ((string?)JsonNode.Parse(delivered.Body)!["code"]).ShouldBe("payment_callback_deferred");
        delivered.Commands.ShouldBe(0);
        sandbox.LineRequests.ShouldBeEmpty();
        (await Payouts(h, ct)).ShouldBeEmpty();
        sandbox.Events = _ => StripeSandboxProcessorTests.Page(false, paid);
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        (await Payouts(h, ct)).ShouldBeEmpty();
        sandbox.LineRequests.ShouldBeEmpty();
        var from = sandbox.Sweeps[^1].Query["created[gte]"];

        // Reconciled: the event still says what it said, and Stripe, asked, says the lines are ready.
        var completed = (JsonObject)payout.DeepClone();
        completed["reconciliation_status"] = "completed";
        sandbox.Payouts[Payout] = (200, completed.ToJsonString());
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);

        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(from);
        var stored = (await Payouts(h, ct)).ShouldHaveSingleItem();
        (stored.Status, stored.Reason, stored.ProcessorAmount).ShouldBe(("AwaitingBank", null, 198.40m));
        (await Stored(h, Payout, ct)).Items.ShouldHaveSingleItem().Kind.ShouldBe("Payment");
        (await h.JournalEntries(ct)).ShouldBe(entries);
        StripeSandboxProcessorTests.ShouldCarryOnlyPayoutIds(sandbox.Replay.Requests.Where(x => x.Path.StartsWith("/v1/payouts/", StringComparison.Ordinal) || x.Path == "/v1/balance_transactions"), Account, Payout);
        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);
        (await Stored(h, Payout, ct)).Payout.Status.ShouldBe("Posted");
    }
}

// The same two rules in the engine every processor shares, shown in a simulation host.
public sealed partial class SimulatedPaymentTests
{
    // A redelivery of a payout in review is the same evidence, and changes nothing. Before the fix it
    // checked the payout again, and one whose cause had gone posted with nobody having asked.
    [Fact]
    public async Task A_redelivered_payout_in_review_is_not_checked_again_until_an_administrator_posts_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        var evidence = h.Payout("po_1", 592.50m, h.Line("1", "Payment", op, 600m, 7.50m, 592.50m));
        await h.Settle(evidence, ct);
        var held = (await List(admin, ct)).Single();
        (held.Status, held.Reason).ShouldBe(("NeedsReview", "pm_fees_insufficient"));
        await HoldFees(h, 10m, ct);
        var before = (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct));

        await h.Settle(evidence, ct);
        await h.Settle(evidence with { ObservedAt = evidence.ObservedAt.AddMinutes(5) }, ct);

        var after = (await List(admin, ct)).Single();
        (after.Status, after.Reason, after.PostedAt, after.CanPost).ShouldBe(("NeedsReview", "pm_fees_insufficient", null, true));
        (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct)).ShouldBe(before);
        (await Staff(admin, op.Id, ct)).Status.ShouldBe("Processing");

        (await admin.PostAsync($"{Settlements}/{held.Id}/post", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await List(admin, ct)).Single().Status.ShouldBe("Posted");
        (await h.Balance(1, ct)).ShouldBe(400m);
    }

    // A payout's return line is refused while a failure is stored for the payment and not yet judged,
    // as its payment line is: the payment's status still says settled, and something disagrees.
    [Fact]
    public async Task A_payouts_return_line_is_refused_while_a_failure_is_stored_for_the_payment_and_not_yet_judged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        await h.Emit(op, "BankCredit", ct);
        await h.Deliver(h.Observation(op, "Failed"), ct);
        (await Staff(admin, op.Id, ct)).Status.ShouldBe("Settled");
        var before = (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct));

        await h.Settle(h.Payout("po_return", -600m, h.Line("1", "Return", op, 600m, 0m, -600m)), ct);

        var payout = (await List(admin, ct)).Single();
        (payout.Status, payout.Reason, payout.CanPost).ShouldBe(("NeedsReview", "conflicting_evidence", false));
        (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct)).ShouldBe(before);
    }
}
