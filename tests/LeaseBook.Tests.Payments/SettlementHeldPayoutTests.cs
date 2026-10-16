using System.Text.Json;
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
/// Payouts a processor reports apart from any bank (ADR-054): stored with their lines and held, never
/// posted until a bank's evidence for them arrives, and then posted only when the bank received what
/// the processor said it paid.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SettlementHeldPayoutTests(PostgresFixture fixture)
{
    private static async Task<Guid> ReceivePayoutAsync(PaymentsHarness h, ProcessorPayout payout, CancellationToken ct) =>
        (await h.InOrg(() => h.Settlements.ReceivePayoutAsync(h.Binding, payout, ct), ct))!.Value;

    private static Task<PaymentSettlement?> EvaluateAsync(PaymentsHarness h, Guid id, CancellationToken ct, Guid? confirmedBy = null) =>
        h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, id, confirmedBy, ct), ct);

    // A time the database stores exactly: it keeps microseconds, and the clock has ticks.
    private static DateTime Whole(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private static Task<BankEvidenceResult> BankAsync(PaymentsHarness h, BankEvidence evidence, CancellationToken ct) =>
        h.InOrg(() => h.Settlements.ReceiveBankEvidenceAsync(h.Binding, evidence, ct), ct);

    [Fact]
    public async Task A_paid_payout_is_held_for_the_bank_and_posts_when_the_bank_received_what_was_paid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct, mode: PaymentModes.StripeSandbox);
        var op = await h.AcceptedAsync(500m, ct, method: PaymentMethods.Card, fee: 15.24m);

        var id = await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 492.50m, Line("1", "Payment", op, 515.24m, 22.74m, 492.50m)), ct);

        var held = await h.ReadSettlementAsync(id, ct);
        (held.Status, held.Reason, held.ProcessorAmount, held.BankAmount, held.EvidenceId, held.Banked)
            .ShouldBe(("AwaitingBank", (string?)null, (decimal?)492.50m, 0m, "", false));
        (held.BankDate, held.FailedAt).ShouldBe((default(DateOnly), (DateTime?)null));
        (await h.ReadItemsAsync(id, ct)).ShouldHaveSingleItem().Gross.ShouldBe(515.24m);
        // Checked, it could post, but no bank has spoken: it keeps waiting, whoever asks.
        (await EvaluateAsync(h, id, ct))!.Status.ShouldBe("AwaitingBank");
        (await EvaluateAsync(h, id, ct, UuidV7.NewId()))!.Status.ShouldBe("AwaitingBank");
        h.Ledger.Settlements.ShouldBeEmpty();

        var stored = await BankAsync(h, h.Bank("po_1", 492.50m, h.Today.AddDays(-1)), ct);

        stored.ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Stored, id, true));
        var banked = await h.ReadSettlementAsync(id, ct);
        (banked.Status, banked.BankAmount, banked.BankDate, banked.EvidenceId).ShouldBe(("Received", 492.50m, h.Today.AddDays(-1), "bank-po_1"));

        var posted = await EvaluateAsync(h, id, ct);

        (posted!.Status, posted.PostedBy).ShouldBe(("Posted", (Guid?)null));
        var request = h.Ledger.Settlements.ShouldHaveSingleItem();
        (request.BankDate, request.PayoutReference, request.ReceiptMemo).ShouldBe((h.Today.AddDays(-1), "po_1", "Stripe sandbox tenant payment"));
        request.Lines.ShouldBe([new("1", "Payment", h.Tenants[0], 500m, PaymentMethods.Card, null, 7.50m)]);
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Settled");
    }

    [Fact]
    public async Task A_bank_that_received_another_amount_than_was_paid_holds_the_payout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);

        (await BankAsync(h, h.Bank("po_1", 99.99m), ct)).Evaluate.ShouldBeTrue();
        var payout = await EvaluateAsync(h, id, ct);

        (payout!.Status, payout.Reason).ShouldBe(("NeedsReview", SettlementReasons.BankAmountMismatch));
        h.Ledger.Settlements.ShouldBeEmpty();
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Processing");
        // An administrator cannot post it either: what the bank received is not what the lines say.
        (await EvaluateAsync(h, id, ct, UuidV7.NewId()))!.Reason.ShouldBe(SettlementReasons.BankAmountMismatch);
        h.Ledger.Settlements.ShouldBeEmpty();
    }

    [Fact]
    public async Task Lines_that_do_not_tie_to_what_the_processor_paid_go_to_a_person_before_any_bank_speaks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 90m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);

        var payout = await EvaluateAsync(h, id, ct);

        (payout!.Status, payout.Reason).ShouldBe(("NeedsReview", "settlement_untied"));
        h.Ledger.Settlements.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bank_evidence_for_a_payout_already_with_a_person_is_kept_beside_it_and_nothing_is_checked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 90m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);
        await EvaluateAsync(h, id, ct);

        var result = await BankAsync(h, h.Bank("po_1", 90m), ct);

        result.ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Stored, id, false));
        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.Reason, payout.BankAmount).ShouldBe(("NeedsReview", "settlement_untied", 90m));
    }

    [Fact]
    public async Task Bank_evidence_is_given_once_and_other_evidence_after_it_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);
        var evidence = h.Bank("po_1", 100m);
        await BankAsync(h, evidence, ct);

        (await BankAsync(h, evidence, ct)).ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Same, id));
        (await h.ReadSettlementAsync(id, ct)).Status.ShouldBe("Received");

        foreach (var other in new[] { evidence with { BankAmount = 100.01m }, evidence with { BankDate = h.Today.AddDays(-1) },
            evidence with { EvidenceId = "bank-other" } })
        {
            (await BankAsync(h, other, ct)).ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Conflicting, id));
        }
        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.Reason, payout.BankAmount, payout.EvidenceId).ShouldBe(("NeedsReview", "conflicting_evidence", 100m, "bank-po_1"));
    }

    [Fact]
    public async Task Other_bank_evidence_for_a_payout_that_posted_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);
        await BankAsync(h, h.Bank("po_1", 100m), ct);
        await EvaluateAsync(h, id, ct);

        (await BankAsync(h, h.Bank("po_1", 100m, evidenceId: "bank-other"), ct)).Outcome.ShouldBe(BankEvidenceOutcomes.Conflicting);

        (await h.ReadSettlementAsync(id, ct)).Status.ShouldBe("Posted");
    }

    public static TheoryData<string, string> NotStorable() => new()
    {
        { "tomorrow", BankEvidenceOutcomes.Malformed },
        { "no date", BankEvidenceOutcomes.Malformed },
        { "a fraction of a cent", BankEvidenceOutcomes.Malformed },
        { "too large", BankEvidenceOutcomes.Malformed },
        { "no reference", BankEvidenceOutcomes.Malformed },
        { "a long payout reference", BankEvidenceOutcomes.Malformed },
        { "an unknown payout", BankEvidenceOutcomes.Unknown },
        { "a payout that came with its bank", BankEvidenceOutcomes.Unknown },
    };

    [Theory]
    [MemberData(nameof(NotStorable))]
    public async Task Bank_evidence_that_cannot_be_kept_changes_nothing(string scenario, string outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);
        var withBank = (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, h.Payout("po_2", 0m), ct), ct))!.Value;
        var evidence = h.Bank("po_1", 100m);
        evidence = scenario switch
        {
            "tomorrow" => evidence with { BankDate = h.Today.AddDays(1) },
            "no date" => evidence with { BankDate = default },
            "a fraction of a cent" => evidence with { BankAmount = 100.001m },
            "too large" => evidence with { BankAmount = 1_000_000_000_000m },
            "no reference" => evidence with { EvidenceId = "" },
            "a long payout reference" => evidence with { PayoutId = new string('p', 101) },
            "an unknown payout" => evidence with { PayoutId = "po_unknown" },
            _ => evidence with { PayoutId = "po_2" },
        };

        var result = await BankAsync(h, evidence, ct);

        result.ShouldBe(new BankEvidenceResult(outcome));
        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.EvidenceId).ShouldBe(("AwaitingBank", ""));
        (await h.ReadSettlementAsync(withBank, ct)).EvidenceId.ShouldBe("bank-po_2");
    }

    [Fact]
    public async Task Bank_evidence_dated_today_is_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);

        (await BankAsync(h, h.Bank("po_1", 100m, h.Today), ct)).Outcome.ShouldBe(BankEvidenceOutcomes.Stored);
    }

    [Fact]
    public async Task A_failed_payout_goes_to_a_person_once_whatever_state_it_was_in()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var paid = h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m));
        var id = await ReceivePayoutAsync(h, paid, ct);
        await BankAsync(h, h.Bank("po_1", 100m), ct);
        (await EvaluateAsync(h, id, ct))!.Status.ShouldBe("Posted");
        var failedAt = Whole(h.Clock.GetUtcNow().UtcDateTime.AddMinutes(1));
        var failure = paid with { State = ProcessorPayoutStates.Failed, ObservedAt = failedAt, Items = [] };

        (await ReceivePayoutAsync(h, failure, ct)).ShouldBe(id);

        // Sent to a person and nothing reversed for it: the receipt the payout posted stays.
        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.Reason, payout.FailedAt).ShouldBe(("NeedsReview", SettlementReasons.PayoutFailed, (DateTime?)failedAt));
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Settled");
        await Should.ThrowAsync<PaymentConflictException>(() => EvaluateAsync(h, id, ct, UuidV7.NewId()));
        h.Ledger.Settlements.Count.ShouldBe(1);

        // Closed by a person, a second notice of the same failure does not reopen it, and nor does a
        // late notice that it was paid.
        await h.InOrg(() => h.Settlements.CloseAsync(h.Binding, id, h.UserId, "Bank confirmed", ct), ct);
        await ReceivePayoutAsync(h, failure with { ObservedAt = failedAt.AddHours(1) }, ct);
        await ReceivePayoutAsync(h, paid with { Amount = 101m }, ct);
        var closed = await h.ReadSettlementAsync(id, ct);
        (closed.Status, closed.FailedAt).ShouldBe(("Closed", (DateTime?)failedAt));
    }

    [Fact]
    public async Task A_payout_reported_failed_before_anything_else_is_stored_with_a_person()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var failedAt = Whole(h.Clock.GetUtcNow().UtcDateTime);
        var failure = h.ProcessorPayout("po_1", 100m) with { State = ProcessorPayoutStates.Failed, ObservedAt = failedAt };

        var id = await ReceivePayoutAsync(h, failure, ct);

        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.Reason, payout.FailedAt, payout.ProcessorAmount).ShouldBe(("NeedsReview", SettlementReasons.PayoutFailed, (DateTime?)failedAt, (decimal?)100m));
        // Its bank evidence is kept beside it, and it still does not post.
        (await BankAsync(h, h.Bank("po_1", 100m), ct)).ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Stored, id, false));
        await Should.ThrowAsync<PaymentConflictException>(() => EvaluateAsync(h, id, ct, UuidV7.NewId()));
    }

    [Fact]
    public async Task A_different_account_of_a_payout_still_awaiting_a_decision_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var paid = h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m));
        var id = await ReceivePayoutAsync(h, paid, ct);

        // The same account again, under another notice and time: no change.
        (await ReceivePayoutAsync(h, paid with { ObservedAt = paid.ObservedAt.AddMinutes(5) }, ct)).ShouldBe(id);
        (await h.ReadSettlementAsync(id, ct)).Status.ShouldBe("AwaitingBank");

        await ReceivePayoutAsync(h, paid with { Amount = 100.5m }, ct);

        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.Reason, payout.ProcessorAmount).ShouldBe(("NeedsReview", "conflicting_evidence", (decimal?)100m));
        (await BankAsync(h, h.Bank("po_1", 100m), ct)).Evaluate.ShouldBeFalse();
        await Should.ThrowAsync<PaymentConflictException>(() => EvaluateAsync(h, id, ct, UuidV7.NewId()));
    }

    public static TheoryData<string> MalformedPayouts() =>
        ["a colon in the reference", "a local time", "an unknown state", "a fraction of a cent", "a line of a fraction of a cent",
         "a line named twice", "a long reference", "a line with no reference", "a line with no kind",
         "a line with a long processor reference", "a line in a two-letter currency", "a line whose gross is a fraction of a cent",
         "a line whose net is a fraction of a cent"];

    [Theory]
    [MemberData(nameof(MalformedPayouts))]
    public async Task A_processor_payout_that_cannot_be_stored_as_given_is_not_stored(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var line = Line("1", "Payment", op, 100m, 0m, 100m);
        var payout = h.ProcessorPayout("po_1", 100m, line);
        payout = scenario switch
        {
            "a colon in the reference" => payout with { PayoutId = "po:1" },
            "a local time" => payout with { ObservedAt = DateTime.SpecifyKind(payout.ObservedAt, DateTimeKind.Local) },
            "an unknown state" => payout with { State = "InTransit" },
            "a fraction of a cent" => payout with { Amount = 100.005m },
            "a line of a fraction of a cent" => payout with { Items = [line with { Fee = 0.001m }] },
            "a line named twice" => payout with { Items = [line, line] },
            "a line with no reference" => payout with { Items = [line with { Item = "" }] },
            "a line with no kind" => payout with { Items = [line with { Kind = " " }] },
            "a line with a long processor reference" => payout with { Items = [line with { ProviderId = new string('p', 101) }] },
            "a line in a two-letter currency" => payout with { Items = [line with { Currency = "US" }] },
            "a line whose gross is a fraction of a cent" => payout with { Items = [line with { Gross = 100.001m }] },
            "a line whose net is a fraction of a cent" => payout with { Items = [line with { Net = 100.001m }] },
            _ => payout with { PayoutId = new string('p', 101) },
        };

        (await h.InOrg(() => h.Settlements.ReceivePayoutAsync(h.Binding, payout, ct), ct)).ShouldBeNull();

        (await h.InOrg(() => h.Scope.Db.Set<PaymentSettlement>().CountAsync(ct), ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_processor_payout_as_large_and_as_long_as_its_columns_allow_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var line = new ProcessorSettlementItem(new string('i', 60), new string('k', 20), new string('p', 100),
            999_999_999_999.99m, 0m, 999_999_999_999.99m, "USD");

        var id = await h.InOrg(() => h.Settlements.ReceivePayoutAsync(h.Binding,
            h.ProcessorPayout(new string('o', 100), 999_999_999_999.99m, line), ct), ct);

        id.ShouldNotBeNull();
        (await h.ReadSettlementAsync(id.Value, ct)).ProcessorAmount.ShouldBe(999_999_999_999.99m);
    }

    [Fact]
    public async Task A_processor_payout_is_fingerprinted_by_what_it_says_with_its_lines_in_order_of_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var second = new ProcessorSettlementItem("2", "Payment", "prov_b", 200m, 0m, 200m, "USD");
        var first = new ProcessorSettlementItem("1", "Payment", "prov_a", 100m, 0m, 100m, "USD");
        var payout = h.ProcessorPayout("po_1", 300m, second, first);

        var id = await ReceivePayoutAsync(h, payout, ct);

        // Stored, and compared with every later notice of the payout, perhaps by a later release.
        (await h.ReadSettlementAsync(id, ct)).Fingerprint.ShouldBe(PaymentEngine.Hash(JsonSerializer.Serialize(new
        {
            payout.PayoutId,
            payout.Account,
            payout.Mode,
            payout.Generation,
            payout.PayoutType,
            payout.Amount,
            payout.Currency,
            payout.BankId,
            Items = new[] { first, second },
        })));
    }

    [Fact]
    public async Task A_payout_awaiting_its_bank_whose_cause_for_review_has_gone_waits_for_the_bank_again_when_an_administrator_checks_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        // Not yet accepted: no payment holds the reference its line names.
        var op = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);
        var id = await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);
        (await EvaluateAsync(h, id, ct))!.Reason.ShouldBe("settlement_incomplete");
        (await h.JudgeAsync(op.Id, ct, accept: ProviderOf(op))).ShouldBeTrue();

        (await EvaluateAsync(h, id, ct, UuidV7.NewId()))!.Status.ShouldBe("AwaitingBank");

        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.Reason).ShouldBe(("AwaitingBank", (string?)null));
        h.Ledger.Settlements.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_processor_payout_for_another_binding_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var payout = h.ProcessorPayout("po_1", 0m);
        var bank = h.Bank("po_1", 0m);

        foreach (var other in new[] { payout with { Account = "acct_other" }, payout with { Mode = PaymentModes.StripeSandbox },
            payout with { Generation = Guid.NewGuid() }, payout with { BankId = Guid.NewGuid() } })
        {
            await Should.ThrowAsync<PaymentUnavailableException>(() => h.InOrg(() => h.Settlements.ReceivePayoutAsync(h.Binding, other, ct), ct));
        }
        foreach (var other in new[] { bank with { Account = "acct_other" }, bank with { Mode = PaymentModes.StripeSandbox },
            bank with { Generation = Guid.NewGuid() } })
        {
            await Should.ThrowAsync<PaymentUnavailableException>(() => BankAsync(h, other, ct));
        }
    }
}
