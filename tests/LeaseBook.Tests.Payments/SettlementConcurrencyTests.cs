using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Payments.Support.PaymentsHarness;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Two pieces of work on one payout, or on a payout and a payment in it, at the same moment, run as
/// <see cref="PaymentsHarness.RaceAsync{T1, T2}"/> runs them: the first held open at the end of its
/// work until the second has finished or is waiting on a lock. A payout is decided under its own
/// lock, the one a posting takes; a payment in it under the payment's.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SettlementConcurrencyTests(PostgresFixture fixture)
{
    private static async Task<bool> Done(Task work)
    {
        await work;
        return true;
    }

    private static async Task<Guid> ReceiveAsync(PaymentsHarness h, ProcessorSettlement evidence, CancellationToken ct) =>
        (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, evidence, ct), ct))!.Value;

    private static async Task<Guid> ReceivePayoutAsync(PaymentsHarness h, ProcessorPayout payout, CancellationToken ct) =>
        (await h.InOrg(() => h.Settlements.ReceivePayoutAsync(h.Binding, payout, ct), ct))!.Value;

    [Fact]
    public async Task Payout_evidence_stored_while_the_processor_reports_the_same_payout_finds_that_payout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var line = Line("1", "Payment", op, 100m, 0m, 100m);

        var race = await h.RaceAsync(
            lane => lane.Settlements.ReceivePayoutAsync(h.Binding, h.ProcessorPayout("po_1", 100m, line), ct),
            lane => lane.Settlements.ReceiveAsync(h.Binding, h.Payout("po_1", 100m, line), ct), ct);

        race.SecondError.ShouldBeNull();
        race.Second.ShouldBe(race.First);
        (await h.InOrg(() => h.Scope.Db.Set<PaymentSettlement>().CountAsync(ct), ct)).ShouldBe(1);
        (await h.ReadSettlementAsync(race.First!.Value, ct)).Status.ShouldBe("AwaitingBank");
    }

    [Fact]
    public async Task Bank_evidence_that_arrives_while_its_payout_is_stored_is_kept_for_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);

        var race = await h.RaceAsync(
            lane => lane.Settlements.ReceivePayoutAsync(h.Binding, h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct),
            lane => lane.Settlements.ReceiveBankEvidenceAsync(h.Binding, h.Bank("po_1", 100m), ct), ct);

        race.SecondError.ShouldBeNull();
        race.Second.ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Stored, race.First, true));
    }

    [Fact]
    public async Task Other_evidence_for_a_payout_that_posts_meanwhile_leaves_it_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var evidence = h.Payout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m));
        var id = await ReceiveAsync(h, evidence, ct);

        var race = await h.RaceAsync(
            lane => lane.Settlements.EvaluateAsync(h.Binding, id, null, ct),
            lane => lane.Settlements.ReceiveAsync(h.Binding, evidence with { EvidenceId = "bank-other" }, ct), ct);

        race.First!.Status.ShouldBe("Posted");
        race.SecondError.ShouldBeNull();
        race.Second.ShouldBe(id);
        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.Reason).ShouldBe(("Posted", (string?)null));
    }

    [Fact]
    public async Task Another_account_of_a_payout_that_posts_meanwhile_leaves_it_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var paid = h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m));
        var id = await ReceivePayoutAsync(h, paid, ct);
        await h.InOrg(() => h.Settlements.ReceiveBankEvidenceAsync(h.Binding, h.Bank("po_1", 100m), ct), ct);

        var race = await h.RaceAsync(
            lane => lane.Settlements.EvaluateAsync(h.Binding, id, null, ct),
            lane => lane.Settlements.ReceivePayoutAsync(h.Binding, paid with { Amount = 100.50m }, ct), ct);

        race.First!.Status.ShouldBe("Posted");
        race.SecondError.ShouldBeNull();
        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.Reason).ShouldBe(("Posted", (string?)null));
    }

    [Fact]
    public async Task Bank_evidence_for_a_payout_closed_meanwhile_is_kept_beside_it_and_does_not_reopen_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = await ReceivePayoutAsync(h, h.ProcessorPayout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);

        var race = await h.RaceAsync(
            lane => lane.Settlements.CloseAsync(h.Binding, id, h.UserId, "Not ours", ct),
            lane => lane.Settlements.ReceiveBankEvidenceAsync(h.Binding, h.Bank("po_1", 100m), ct), ct);

        race.SecondError.ShouldBeNull();
        race.Second.ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Stored, id, false));
        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.BankAmount, payout.EvidenceId).ShouldBe(("Closed", 100m, "bank-po_1"));
    }

    [Fact]
    public async Task A_technical_failure_recorded_while_the_payout_posts_leaves_it_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = await ReceiveAsync(h, h.Payout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);

        var race = await h.RaceAsync(
            lane => lane.Settlements.EvaluateAsync(h.Binding, id, null, ct),
            lane => Done(lane.Settlements.MarkFailedAsync(h.Binding, id, ct)), ct);

        race.SecondError.ShouldBeNull();
        var payout = await h.ReadSettlementAsync(id, ct);
        (payout.Status, payout.Reason).ShouldBe(("Posted", (string?)null));
    }

    [Fact]
    public async Task A_payout_is_not_posted_for_a_payment_sent_to_a_person_while_the_payout_was_checked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.ObserveAsync(h.Observation(op, "Processing"), ct);
        var lease = (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct))!.LeaseClaimId!.Value;
        var id = await ReceiveAsync(h, h.Payout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);

        var race = await h.RaceAsync(
            lane => Done(lane.Payments.FailAsync(h.Binding, op.Id, lease, "processor_rejected", ct)),
            lane => lane.Settlements.EvaluateAsync(h.Binding, id, null, ct), ct);

        race.SecondError.ShouldBeNull();
        (race.Second!.Status, race.Second.Reason).ShouldBe(("NeedsReview", "conflicting_evidence"));
        race.SettlementRequests.ShouldBe(0);
        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason, held.JournalId).ShouldBe(("NeedsReview", "processor_rejected", (Guid?)null));
    }

    public static TheoryData<string> LockOrders() => ["references", "payments"];

    // A payout locks the references its lines name in ordinal order, and then their payments in order
    // of id, so that anything else taking more than one of those locks in that same order can never
    // hold one the payout waits for while waiting for one the payout holds. Nothing in the engines
    // takes more than one today; the stand-in here takes two through the public lock, in the
    // promised order, with the payout between its first and its second.
    [Theory]
    [MemberData(nameof(LockOrders))]
    public async Task A_payout_takes_its_locks_in_the_promised_order_so_a_caller_keeping_that_order_cannot_deadlock_with_it(string locks)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var a = await h.AcceptedAsync(100m, ct);
        var b = await h.AcceptedAsync(200m, ct, tenant: 1);
        var id = await ReceiveAsync(h, h.Payout("po_1", 300m,
            Line("1", "Payment", a, 100m, 0m, 100m), Line("2", "Payment", b, 200m, 0m, 200m)), ct);
        var names = locks == "references"
            ? new[] { ProviderOf(a), ProviderOf(b) }.Order(StringComparer.Ordinal).Select(x => "reference:" + x).ToArray()
            : new[] { a.Id, b.Id }.Order().Select(x => x.ToString()).ToArray();
        await using var caller = h.Lane();
        await using var checker = h.Lane();

        var held = caller.Hold(lane => Done(lane.Payments.LockAsync(names[0], ct)), ct, then: lane => lane.Payments.LockAsync(names[1], ct));
        await held.Done;
        var checking = checker.RunAsync(lane => lane.Settlements.EvaluateAsync(h.Binding, id, null, ct), ct);
        (await h.WaitingOrDoneAsync(checker, checking, ct)).ShouldBeTrue();
        held.Commit();

        (await Lanes.ErrorOf(held.Ended())).ShouldBeNull();
        var (payout, error) = await Lanes.OutcomeOf(checking);
        error.ShouldBeNull();
        payout!.Status.ShouldBe("Posted");
    }

    [Fact]
    public async Task A_payout_is_not_posted_past_a_failure_being_stored_for_one_of_its_payments()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var id = await ReceiveAsync(h, h.Payout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct);
        var failure = h.Observation(op, "Failed");
        await using var holder = h.Lane();
        await using var receiver = h.Lane();
        await using var checker = h.Lane();

        // The failure is stored up to the point between keeping the fact and waking the payment.
        var held = holder.HoldNotice(h.Binding, failure.EventId, ct);
        await held.Done;
        var receiving = receiver.RunAsync(lane => Done(lane.Payments.ReceiveAsync(h.Binding, failure, ct)), ct);
        (await h.WaitingOrDoneAsync(receiver, receiving, ct)).ShouldBeTrue();
        var checking = checker.RunAsync(lane => lane.Settlements.EvaluateAsync(h.Binding, id, null, ct), ct);
        await h.WaitingOrDoneAsync(checker, checking, ct);
        held.Abort();
        await held.Ended();
        await receiving;

        var payout = await checking;
        (payout!.Status, payout.Reason).ShouldBe(("NeedsReview", "conflicting_evidence"));
        checker.Ledger.Settlements.ShouldBeEmpty();
        (await h.ReadAsync(op.Id, ct)).JournalId.ShouldBeNull();
    }
}
