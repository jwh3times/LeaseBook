using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Payments.Support.PaymentsHarness;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Payout batches with their bank evidence (ADR-053): stored once, checked line by line, and posted
/// completely or not at all. The figures of the first test are the worked batch in
/// <c>docs/payments/fee-and-settlement-spec.md</c>.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SettlementPostingTests(PostgresFixture fixture)
{
    public static TheoryData<string, string> Memos() => new()
    {
        { PaymentModes.Simulation, "Simulated tenant payment" },
        { PaymentModes.StripeSandbox, "Stripe sandbox tenant payment" },
    };

    [Theory]
    [MemberData(nameof(Memos))]
    public async Task The_worked_payout_posts_by_itself_with_each_fee_difference(string mode, string memo)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct, mode: mode);
        var clean = await h.AcceptedAsync(1000m, ct, tenant: 1, method: PaymentMethods.Card, fee: 30.17m);
        var shortfall = await h.AcceptedAsync(500m, ct, method: PaymentMethods.Card, fee: 15.24m);
        var surplus = await h.AcceptedAsync(250m, ct, method: PaymentMethods.Ach, fee: 7.78m);

        var payout = await h.SettleAsync(h.Payout("po_1", 1742.90m,
            Line("1", "Payment", clean, 1030.17m, 30.17m, 1000m),
            Line("2", "Payment", shortfall, 515.24m, 22.74m, 492.50m),
            Line("3", "Payment", surplus, 257.78m, 7.38m, 250.40m)), ct);

        (payout.Status, payout.Reason, payout.ReasonItem, payout.PostedBy).ShouldBe(("Posted", (string?)null, (string?)null, (Guid?)null));
        payout.PostedAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime);
        var request = h.Ledger.Settlements.ShouldHaveSingleItem();
        (request.BankId, request.BankDate, request.PayoutReference, request.ReceiptMemo).ShouldBe((h.Binding.BankId, h.Today, "po_1", memo));
        // Each tenant is credited the ledger amount; what the processor kept beyond the quote is the
        // shortfall, and what it kept short of it the surplus.
        request.Lines.ShouldBe(
        [
            new("1", "Payment", h.Tenants[1], 1000m, PaymentMethods.Card, null, 0m),
            new("2", "Payment", h.Tenants[0], 500m, PaymentMethods.Card, null, 7.50m),
            new("3", "Payment", h.Tenants[0], 250m, PaymentMethods.Ach, null, -0.40m),
        ]);

        var items = await h.ReadItemsAsync(payout.Id, ct);
        items.Select(x => (x.Item, x.EntryId.HasValue, x.FeeEntryId.HasValue)).ShouldBe([("1", true, false), ("2", true, true), ("3", true, true)]);
        foreach (var op in new[] { clean, shortfall, surplus })
        {
            var settled = await h.ReadAsync(op.Id, ct);
            (settled.Status, settled.Reason).ShouldBe(("Settled", (string?)null));
            settled.JournalId.ShouldBe(items.Single(x => x.ProviderId == ProviderOf(op)).EntryId);
        }
        var effects = await h.ReadEffectsAsync(ct);
        effects.ShouldAllBe(x => x.SettlementId == payout.Id && x.ObservationId == null);
        effects.Select(x => (x.OperationId, x.Kind)).ShouldBe(
        [
            (clean.Id, "Receipt"), (shortfall.Id, "Receipt"), (shortfall.Id, "PaymentFee"), (surplus.Id, "Receipt"), (surplus.Id, "PaymentFee"),
        ], ignoreOrder: true);
        effects.Single(x => x.OperationId == shortfall.Id && x.Kind == "PaymentFee").JournalId.ShouldBe(items[1].FeeEntryId!.Value);

        // A redelivery is the same payout, and posts nothing more.
        (await h.SettleAsync(h.Payout("po_1", 1742.90m,
            Line("3", "Payment", surplus, 257.78m, 7.38m, 250.40m),
            Line("1", "Payment", clean, 1030.17m, 30.17m, 1000m),
            Line("2", "Payment", shortfall, 515.24m, 22.74m, 492.50m)), ct)).Id.ShouldBe(payout.Id);
        h.Ledger.Settlements.Count.ShouldBe(1);
        // A payment a payout receipted is not receipted again by its own bank credit.
        await h.EmitAsync(h.Observation(clean, "BankCredit"), ct);
        (await h.ReadAsync(clean.Id, ct)).Status.ShouldBe("Settled");
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_standalone_processor_fee_posts_as_all_fee_difference_with_no_payment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);

        var payout = await h.SettleAsync(h.Payout("po_1", 98.75m, Line("1", "Payment", op, 100m, 0m, 100m), FeeLine("2", 1.25m)), ct);

        payout.Status.ShouldBe("Posted");
        h.Ledger.Settlements.Single().Lines.ShouldBe(
        [
            new("1", "Payment", h.Tenants[0], 100m, PaymentMethods.Ach, null, 0m),
            new("2", "Fee", null, 0m, "", null, 1.25m),
        ]);
        var items = await h.ReadItemsAsync(payout.Id, ct);
        (items[1].EntryId, items[1].FeeEntryId.HasValue).ShouldBe(((Guid?)null, true));
        (await h.ReadEffectsAsync(ct)).ShouldHaveSingleItem().Kind.ShouldBe("Receipt");
    }

    public static TheoryData<string, string> Held() => new()
    {
        { "bank amount a cent off", "settlement_untied" },
        { "a charge that is not the payment's", "settlement_untied" },
        { "a net that is not gross less fee", "settlement_untied" },
        { "a negative fee", "settlement_untied" },
        { "a payment that nets nothing", "settlement_untied" },
        { "a fee line that is not all fee", "settlement_untied" },
        { "a fee line of nothing", "settlement_untied" },
        { "a payment nobody knows", "settlement_incomplete" },
        { "no lines", "settlement_incomplete" },
        { "a refund", "unsupported_settlement" },
        { "an instant payout", "unsupported_settlement" },
        { "a payout in another currency", "unsupported_settlement" },
        { "a line in another currency", "unsupported_settlement" },
        { "another bank", "unsupported_settlement" },
        { "no bank date", "unsupported_settlement" },
        { "a payment named twice", "conflicting_evidence" },
        { "a payment already receipted", "conflicting_evidence" },
        { "a payment in review", "conflicting_evidence" },
        { "a failure stored and not judged", "conflicting_evidence" },
        { "a dispute stored and not judged", "conflicting_evidence" },
    };

    [Theory]
    [MemberData(nameof(Held))]
    public async Task A_payout_that_cannot_post_says_why_and_posts_nothing(string scenario, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var first = await h.AcceptedAsync(600m, ct);
        var second = await h.AcceptedAsync(400m, ct, tenant: 1);
        var lines = new List<ProcessorSettlementItem>
        {
            Line("1", "Payment", first, 600m, 0m, 600m),
            Line("2", "Payment", second, 400m, 0m, 400m),
        };
        var evidence = h.Payout("po_1", 1000m, [.. lines]);
        evidence = scenario switch
        {
            "bank amount a cent off" => evidence with { BankAmount = 999.99m },
            "a charge that is not the payment's" => evidence with { Items = [lines[0], Line("2", "Payment", second, 410m, 10m, 400m)] },
            "a net that is not gross less fee" => evidence with { Items = [lines[0], Line("2", "Payment", second, 400m, 1m, 400m)] },
            "a negative fee" => evidence with { Items = [lines[0], Line("2", "Payment", second, 400m, -1m, 401m)], BankAmount = 1001m },
            "a payment that nets nothing" => evidence with { Items = [lines[0], Line("2", "Payment", second, 400m, 400m, 0m)], BankAmount = 600m },
            "a fee line that is not all fee" => evidence with { Items = [.. lines, FeeLine("3", 1m) with { Net = -0.5m }], BankAmount = 999.50m },
            "a fee line of nothing" => evidence with { Items = [.. lines, FeeLine("3", 0m)] },
            "a payment nobody knows" => evidence with { Items = [lines[0], lines[1] with { ProviderId = "prov_unknown" }] },
            "no lines" => evidence with { Items = [], BankAmount = 0m },
            "a refund" => evidence with { Items = [lines[0], lines[1] with { Kind = "Refund", Net = -400m }], BankAmount = 200m },
            "an instant payout" => evidence with { PayoutType = "instant" },
            "a payout in another currency" => evidence with { Currency = "CAD" },
            "a line in another currency" => evidence with { Items = [lines[0], lines[1] with { Currency = "CAD" }] },
            "another bank" => evidence with { BankId = Guid.NewGuid() },
            "no bank date" => evidence with { BankDate = default },
            "a payment named twice" => evidence with { Items = [.. lines, lines[1] with { Item = "3" }], BankAmount = 1400m },
            _ => evidence,
        };
        switch (scenario)
        {
            case "a payment already receipted": await h.EmitAsync(h.Observation(second, "BankCredit"), ct); break;
            case "a payment in review": await h.EmitAsync(h.Observation(second, "Dispute"), ct); break;
            case "a failure stored and not judged": await h.ObserveAsync(h.Observation(second, "Failed"), ct); break;
            case "a dispute stored and not judged": await h.ObserveAsync(h.Observation(second, "Dispute"), ct); break;
        }
        var receiptsBefore = h.Ledger.Receipts.Count;

        var payout = await h.SettleAsync(evidence, ct);

        (payout.Status, payout.Reason, payout.ReasonItem).ShouldBe(("NeedsReview", reason, (string?)null));
        h.Ledger.Settlements.ShouldBeEmpty();
        h.Ledger.Receipts.Count.ShouldBe(receiptsBefore);
        (await h.ReadItemsAsync(payout.Id, ct)).ShouldAllBe(x => x.EntryId == null && x.FeeEntryId == null);
        (await h.ReadEffectsAsync(ct)).ShouldNotContain(x => x.SettlementId != null);
        // All or nothing: the clean first line did not settle its payment either.
        (await h.ReadAsync(first.Id, ct)).Status.ShouldBe("Processing");
    }

    [Fact]
    public async Task A_refusal_by_an_accounting_rule_holds_the_payout_on_the_item_it_stopped_on()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(400m, ct);
        h.Ledger.SettlementRefusal = ("pm_fees_insufficient", "1");

        var payout = await h.SettleAsync(h.Payout("po_1", 392.50m, Line("1", "Payment", op, 400m, 7.50m, 392.50m)), ct);

        (payout.Status, payout.Reason, payout.ReasonItem).ShouldBe(("NeedsReview", "pm_fees_insufficient", "1"));
        h.Ledger.Settlements.ShouldHaveSingleItem().Lines.ShouldBe([new("1", "Payment", h.Tenants[0], 400m, PaymentMethods.Ach, null, 7.50m)]);
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Processing");
        (await h.ReadEffectsAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_payout_in_review_is_checked_again_only_when_an_administrator_posts_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(400m, ct);
        h.Ledger.SettlementRefusal = ("pm_fees_insufficient", "1");
        var payout = await h.SettleAsync(h.Payout("po_1", 392.50m, Line("1", "Payment", op, 400m, 7.50m, 392.50m)), ct);
        h.Ledger.SettlementRefusal = null;

        // The cause has gone, and the same notice arrives again: it is not posted unattended.
        var again = await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, payout.Id, null, ct), ct);
        (again!.Status, again.Reason).ShouldBe(("NeedsReview", "pm_fees_insufficient"));
        h.Ledger.Settlements.Count.ShouldBe(1);

        var admin = UuidV7.NewId();
        var posted = await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, payout.Id, admin, ct), ct);

        (posted!.Status, posted.Reason, posted.ReasonItem, posted.PostedBy).ShouldBe(("Posted", (string?)null, (string?)null, (Guid?)admin));
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Settled");
        // Posted is final: asking again returns it and posts nothing.
        (await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, payout.Id, admin, ct), ct))!.Status.ShouldBe("Posted");
        h.Ledger.Settlements.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_payout_with_a_return_waits_for_an_administrator_who_posts_the_whole_of_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var returned = await h.AcceptedAsync(600m, ct, fee: 6m);
        await h.SettleAsync(h.Payout("po_1", 600m, Line("1", "Payment", returned, 606m, 6m, 600m)), ct);
        var receipt = (await h.ReadAsync(returned.Id, ct)).JournalId!.Value;
        var later = await h.AcceptedAsync(400m, ct, tenant: 1);

        // 400.00 arrives and the earlier charge of 606.00 goes back, the processor giving back 4.00 of
        // its 6.00 fee: the bank is debited 602.00 for it, so the payout nets -202.00.
        var evidence = h.Payout("po_2", -202m,
            Line("1", "Payment", later, 400m, 0m, 400m),
            Line("2", "Return", returned, 606m, 4m, -602m));
        var waiting = await h.SettleAsync(evidence, ct);

        (waiting.Status, waiting.Reason).ShouldBe(("NeedsReview", "settlement_requires_confirmation"));
        h.Ledger.Settlements.Count.ShouldBe(1);
        (await h.ReadAsync(later.Id, ct)).Status.ShouldBe("Processing");

        var admin = UuidV7.NewId();
        var posted = await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, waiting.Id, admin, ct), ct);

        (posted!.Status, posted.PostedBy).ShouldBe(("Posted", (Guid?)admin));
        h.Ledger.Settlements[1].Lines.ShouldBe(
        [
            new("1", "Payment", h.Tenants[1], 400m, PaymentMethods.Ach, null, 0m),
            // What the processor kept of its fee on the return is a shortfall: 6.00 quoted, 4.00 back.
            new("2", "Return", null, 0m, "", receipt, 2m),
        ]);
        var back = await h.ReadAsync(returned.Id, ct);
        (back.Status, back.Reason, back.JournalId).ShouldBe(("Returned", (string?)null, receipt));
        (await h.ReadAsync(later.Id, ct)).Status.ShouldBe("Settled");
        (await h.ReadEffectsAsync(ct)).Where(x => x.OperationId == returned.Id).Select(x => x.Kind)
            .ShouldBe(["Receipt", "Return", "ReturnFee"], ignoreOrder: true);

        // Later progress about the returned payment keeps it returned.
        await h.EmitAsync(h.Observation(returned, "Processing", "late-progress"), ct);
        (await h.ReadAsync(returned.Id, ct)).Status.ShouldBe("Returned");
    }

    [Fact]
    public async Task A_return_whose_return_notice_is_in_review_posts_and_the_two_are_one_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var returned = await h.AcceptedAsync(600m, ct);
        await h.SettleAsync(h.Payout("po_1", 600m, Line("1", "Payment", returned, 600m, 0m, 600m)), ct);
        await h.EmitAsync(h.Observation(returned, "Return"), ct);
        (await h.ReadAsync(returned.Id, ct)).Reason.ShouldBe("return_requires_review");

        var posted = await h.SettleAsync(h.Payout("po_2", -600m, Line("1", "Return", returned, 600m, 0m, -600m)), ct, UuidV7.NewId());

        posted.Status.ShouldBe("Posted");
        (await h.ReadAsync(returned.Id, ct)).Status.ShouldBe("Returned");
        // The notice and the payout are the same return seen twice: judged again, it stays returned.
        await h.EmitAsync(h.Observation(returned, "Return", "return-again"), ct);
        (await h.ReadAsync(returned.Id, ct)).Status.ShouldBe("Returned");
    }

    public static TheoryData<string, string> ReturnHeld() => new()
    {
        { "never receipted", "settlement_incomplete" },
        { "part of the charge", "unsupported_settlement" },
        { "more fee back than was quoted", "settlement_untied" },
        { "a negative fee back", "settlement_untied" },
        { "a net that is not the charge less the fee back", "settlement_untied" },
        { "already returned", "conflicting_evidence" },
        { "a refund stored", "conflicting_evidence" },
        { "a failure stored", "conflicting_evidence" },
        { "in review for something else", "conflicting_evidence" },
    };

    [Theory]
    [MemberData(nameof(ReturnHeld))]
    public async Task A_return_line_that_cannot_post_holds_the_payout(string scenario, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(600m, ct, fee: 6m);
        if (scenario != "never receipted")
        {
            await h.SettleAsync(h.Payout("po_1", 600m, Line("1", "Payment", op, 606m, 6m, 600m)), ct);
        }
        var line = Line("1", "Return", op, 606m, 6m, -600m);
        switch (scenario)
        {
            case "part of the charge": line = line with { Gross = 300m, Net = -294m }; break;
            case "more fee back than was quoted": line = line with { Fee = 6.01m, Net = -599.99m }; break;
            case "a negative fee back": line = line with { Fee = -1m, Net = -607m }; break;
            case "a net that is not the charge less the fee back": line = line with { Net = -606m }; break;
            case "already returned":
                await h.SettleAsync(h.Payout("po_r", -600m, line), ct, UuidV7.NewId());
                break;
            case "a refund stored": await h.ObserveAsync(h.Observation(op, "Refund"), ct); break;
            case "a failure stored": await h.ObserveAsync(h.Observation(op, "Failed"), ct); break;
            case "in review for something else": await h.EmitAsync(h.Observation(op, "Dispute"), ct); break;
        }
        var postedBefore = h.Ledger.Settlements.Count;

        var payout = await h.SettleAsync(h.Payout("po_2", line.Net, line), ct, UuidV7.NewId());

        (payout.Status, payout.Reason).ShouldBe(("NeedsReview", reason));
        h.Ledger.Settlements.Count.ShouldBe(postedBefore);
    }

    [Fact]
    public async Task The_same_payout_with_other_content_is_kept_out_and_marks_it_conflicting_unless_it_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var a = await h.AcceptedAsync(100m, ct);
        var b = await h.AcceptedAsync(200m, ct);
        h.Ledger.SettlementRefusal = ("settlement_period_locked", null);
        var held = await h.SettleAsync(h.Payout("po_1", 100m, Line("1", "Payment", a, 100m, 0m, 100m)), ct);
        var posted = await h.SettleAsync(h.Payout("po_2", 200m, Line("1", "Payment", b, 200m, 0m, 200m)), ct);
        posted.Status.ShouldBe("NeedsReview");
        h.Ledger.SettlementRefusal = null;
        posted = (await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, posted.Id, UuidV7.NewId(), ct), ct))!;
        posted.Status.ShouldBe("Posted");

        (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, h.Payout("po_1", 100.01m,
            Line("1", "Payment", a, 100m, 0m, 100m)), ct), ct)).ShouldBe(held.Id);
        (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, h.Payout("po_2", 200.01m,
            Line("1", "Payment", b, 200m, 0m, 200m)), ct), ct)).ShouldBe(posted.Id);

        var conflicted = await h.ReadSettlementAsync(held.Id, ct);
        (conflicted.Status, conflicted.Reason, conflicted.BankAmount).ShouldBe(("NeedsReview", "conflicting_evidence", 100m));
        (await h.ReadSettlementAsync(posted.Id, ct)).Status.ShouldBe("Posted");
        // Conflicting evidence is never posted, whoever asks.
        await Should.ThrowAsync<PaymentConflictException>(() =>
            h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, held.Id, UuidV7.NewId(), ct), ct));
    }

    [Fact]
    public async Task A_payout_still_received_is_marked_conflicting_by_other_content()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var a = await h.AcceptedAsync(100m, ct);
        var evidence = h.Payout("po_1", 100m, Line("1", "Payment", a, 100m, 0m, 100m));
        var id = (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, evidence, ct), ct))!.Value;
        (await h.ReadSettlementAsync(id, ct)).Status.ShouldBe("Received");

        // Delivered again later, observed at another time: the same evidence.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, evidence with { ObservedAt = h.Clock.GetUtcNow().UtcDateTime }, ct), ct))
            .ShouldBe(id);
        (await h.ReadSettlementAsync(id, ct)).Status.ShouldBe("Received");

        await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, evidence with { EvidenceId = "bank-other" }, ct), ct);

        var stored = await h.ReadSettlementAsync(id, ct);
        (stored.Status, stored.Reason, stored.EvidenceId).ShouldBe(("NeedsReview", "conflicting_evidence", "bank-po_1"));
    }

    public static TheoryData<string> Malformed() =>
        ["a colon in the reference", "a line named twice", "no evidence reference", "a long payout type", "a two-letter currency",
         "a line with no kind"];

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Evidence_that_cannot_be_stored_as_given_is_not_stored(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var a = await h.AcceptedAsync(100m, ct);
        var line = Line("1", "Payment", a, 100m, 0m, 100m);
        var evidence = h.Payout("po_1", 100m, line);
        evidence = scenario switch
        {
            "a colon in the reference" => evidence with { PayoutId = "po:1" },
            "a line named twice" => evidence with { Items = [line, line with { Gross = 0m, Net = 0m }] },
            "no evidence reference" => evidence with { EvidenceId = " " },
            "a long payout type" => evidence with { PayoutType = new string('s', 21) },
            "a two-letter currency" => evidence with { Currency = "US" },
            _ => evidence with { Items = [line with { Kind = "" }] },
        };

        (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, evidence, ct), ct)).ShouldBeNull();

        (await h.InOrg(() => h.Scope.Db.Set<PaymentSettlement>().CountAsync(ct), ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Evidence_for_another_binding_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var evidence = h.Payout("po_1", 0m);

        foreach (var other in new[] { evidence with { Account = "acct_other" }, evidence with { Mode = PaymentModes.StripeSandbox },
            evidence with { Generation = Guid.NewGuid() } })
        {
            await Should.ThrowAsync<PaymentUnavailableException>(() => h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, other, ct), ct));
        }
    }

    [Fact]
    public async Task A_person_closes_a_payout_with_a_note_and_a_posted_one_cannot_be_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var a = await h.AcceptedAsync(100m, ct);
        var b = await h.AcceptedAsync(200m, ct);
        var held = await h.SettleAsync(h.Payout("po_1", 99m, Line("1", "Payment", a, 100m, 0m, 100m)), ct);
        var posted = await h.SettleAsync(h.Payout("po_2", 200m, Line("1", "Payment", b, 200m, 0m, 200m)), ct);

        var closed = await h.InOrg(() => h.Settlements.CloseAsync(h.Binding, held.Id, h.UserId, "Bank error", ct), ct);

        (closed!.Status, closed.ReviewNote, closed.ReviewClosedBy, closed.ReviewClosedAt)
            .ShouldBe(("Closed", "Bank error", (Guid?)h.UserId, (DateTime?)h.Clock.GetUtcNow().UtcDateTime));
        (await h.InOrg(() => h.Settlements.CloseAsync(h.Binding, held.Id, Guid.NewGuid(), "Again", ct), ct))!.ReviewNote.ShouldBe("Bank error");
        await Should.ThrowAsync<PaymentConflictException>(() =>
            h.InOrg(() => h.Settlements.CloseAsync(h.Binding, posted.Id, h.UserId, "No", ct), ct));
        (await h.InOrg(() => h.Settlements.CloseAsync(h.Binding, Guid.NewGuid(), h.UserId, "No", ct), ct)).ShouldBeNull();
        // A closed payout is never posted, whoever asks.
        await Should.ThrowAsync<PaymentConflictException>(() =>
            h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, held.Id, UuidV7.NewId(), ct), ct));
        (await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, Guid.NewGuid(), null, ct), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_technical_failure_is_recorded_only_on_a_payout_still_received()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var a = await h.AcceptedAsync(100m, ct);
        var b = await h.AcceptedAsync(200m, ct);
        var received = (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding,
            h.Payout("po_1", 100m, Line("1", "Payment", a, 100m, 0m, 100m)), ct), ct))!.Value;
        var posted = await h.SettleAsync(h.Payout("po_2", 200m, Line("1", "Payment", b, 200m, 0m, 200m)), ct);

        await h.InOrg(() => h.Settlements.MarkFailedAsync(h.Binding, received, ct), ct);
        await h.InOrg(() => h.Settlements.MarkFailedAsync(h.Binding, posted.Id, ct), ct);
        await h.InOrg(() => h.Settlements.MarkFailedAsync(h.Binding, Guid.NewGuid(), ct), ct);

        var failed = await h.ReadSettlementAsync(received, ct);
        (failed.Status, failed.Reason).ShouldBe(("NeedsReview", "technical_failure"));
        (await h.ReadSettlementAsync(posted.Id, ct)).Status.ShouldBe("Posted");
        // An administrator can post it once the fault is mended.
        (await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, received, UuidV7.NewId(), ct), ct))!.Status.ShouldBe("Posted");
    }
}
