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
/// The payout list staff read (ADR-053, ADR-054): newest first, each payout with its bank figures only
/// once a bank has spoken, why it waits, its lines and the payment each names, and the two ways out,
/// post and close, offered only where they can succeed.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SettlementListTests(PostgresFixture fixture)
{
    private sealed record Payouts(Guid Posted, Guid Received, Guid Untied, Guid Incomplete, Guid Conflicting, Guid Unsupported,
        Guid Mismatch, Guid Failed, Guid Awaiting, Guid HeldUnbanked, Guid Closed, PaymentOperation PostedPayment);

    private static ProcessorSettlementItem Unknown(string item, decimal amount) =>
        new(item, SettlementItemKinds.Payment, "prov_nobody", amount, 0m, amount, "USD");

    private static async Task<Guid> HeldAsync(PaymentsHarness h, ProcessorPayout payout, CancellationToken ct) =>
        (await h.InOrg(() => h.Settlements.ReceivePayoutAsync(h.Binding, payout, ct), ct))!.Value;

    private static async Task<Payouts> HoldEveryKindAsync(PaymentsHarness h, CancellationToken ct)
    {
        var paid = await h.AcceptedAsync(100m, ct);
        var posted = await h.SettleAsync(h.Payout("po_posted", 99.50m, Line("2", "Payment", paid, 100m, 0m, 100m), FeeLine("1", 0.50m)), ct);

        // Stored, and its check never ran.
        var received = (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, h.Payout("po_received", 7m, Unknown("1", 7m)), ct), ct))!.Value;

        var untied = await h.SettleAsync(h.Payout("po_untied", 49m, Line("1", "Payment", await h.AcceptedAsync(50m, ct), 50m, 0m, 50m)), ct);

        // With the bank's word, and a payment nobody here knows yet: it may be, and the payout post then.
        var incomplete = await h.SettleAsync(h.Payout("po_incomplete", 3m, Unknown("1", 3m)), ct);

        var contradicted = await h.AcceptedAsync(30m, ct);
        var conflicting = await h.SettleAsync(h.Payout("po_conflict", 29m, Line("1", "Payment", contradicted, 30m, 0m, 30m)), ct);
        await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, h.Payout("po_conflict", 30m, Line("1", "Payment", contradicted, 30m, 0m, 30m)), ct), ct);

        // A refund in a payout, with the bank's word for it: something this model never posts.
        var refunded = await h.AcceptedAsync(40m, ct);
        var unsupported = await h.SettleAsync(h.Payout("po_refund", -40m, Line("1", "Refund", refunded, 40m, 0m, -40m)), ct);

        var mismatch = await HeldAsync(h, h.ProcessorPayout("po_mismatch", 20m, Line("1", "Payment", await h.AcceptedAsync(20m, ct), 20m, 0m, 20m)), ct);
        await h.InOrg(() => h.Settlements.ReceiveBankEvidenceAsync(h.Binding, h.Bank("po_mismatch", 19m), ct), ct);
        await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, mismatch, null, ct), ct);

        var failed = await HeldAsync(h, h.ProcessorPayout("po_failed", 15m, Line("1", "Payment", await h.AcceptedAsync(15m, ct), 15m, 0m, 15m)), ct);
        await HeldAsync(h, h.ProcessorPayout("po_failed", 15m) with { State = ProcessorPayoutStates.Failed }, ct);

        var awaiting = await HeldAsync(h, h.ProcessorPayout("po_awaiting", 10m, Line("1", "Payment", await h.AcceptedAsync(10m, ct), 10m, 0m, 10m)), ct);
        await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, awaiting, null, ct), ct);

        // Held for a bank, and its lines could never post: a person has it before any bank speaks.
        var heldUnbanked = await HeldAsync(h, h.ProcessorPayout("po_held", 5m, Unknown("1", 5m)), ct);
        await h.InOrg(() => h.Settlements.EvaluateAsync(h.Binding, heldUnbanked, null, ct), ct);

        var closed = await h.SettleAsync(h.Payout("po_closed", 1m, Line("1", "Payment", await h.AcceptedAsync(2m, ct), 2m, 0m, 2m)), ct);
        await h.InOrg(() => h.Settlements.CloseAsync(h.Binding, closed.Id, h.UserId, "Processor confirmed it was a test.", ct), ct);

        return new(posted.Id, received, untied.Id, incomplete.Id, conflicting.Id, unsupported.Id, mismatch, failed, awaiting, heldUnbanked, closed.Id, paid);
    }

    private static (string Status, string? Reason, bool CanPost, bool CanClose, DateOnly? BankDate, decimal? BankAmount) Shown(SettlementView view) =>
        (view.Status, view.Reason, view.CanPost, view.CanClose, view.BankDate, view.BankAmount);

    [Fact]
    public async Task Staff_see_why_each_payout_waits_and_only_the_ways_out_that_can_succeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var p = await HoldEveryKindAsync(h, ct);
        var today = h.Today;

        var list = (await h.QueryAsync(new GetSettlements(), ct)).ToDictionary(x => x.Id);

        list.Count.ShouldBe(11);
        Shown(list[p.Posted]).ShouldBe(("Posted", null, false, false, today, 99.50m));
        // A check that never finished has both ways out.
        Shown(list[p.Received]).ShouldBe(("Received", null, true, true, today, 7m));
        // A payment not yet known may become known, so that payout can be posted again.
        Shown(list[p.Incomplete]).ShouldBe(("NeedsReview", "settlement_incomplete", true, true, today, 3m));
        // Figures that do not tie, conflicting evidence, something this model does not post, a bank
        // that received another amount, a failed payout and one no bank has spoken for cannot post
        // whatever a person decides; each can only be closed (#630).
        Shown(list[p.Untied]).ShouldBe(("NeedsReview", "settlement_untied", false, true, today, 49m));
        Shown(list[p.Conflicting]).ShouldBe(("NeedsReview", "conflicting_evidence", false, true, today, 29m));
        Shown(list[p.Unsupported]).ShouldBe(("NeedsReview", "unsupported_settlement", false, true, today, -40m));
        Shown(list[p.Mismatch]).ShouldBe(("NeedsReview", SettlementReasons.BankAmountMismatch, false, true, today, 19m));
        Shown(list[p.Failed]).ShouldBe(("NeedsReview", SettlementReasons.PayoutFailed, false, true, null, null));
        Shown(list[p.Awaiting]).ShouldBe(("AwaitingBank", null, false, true, null, null));
        Shown(list[p.HeldUnbanked]).ShouldBe(("NeedsReview", "settlement_incomplete", false, true, null, null));
        Shown(list[p.Closed]).ShouldBe(("Closed", "settlement_untied", false, false, today, 1m));

        // The processor's own figure is shown beside the bank's, and is all there is while the bank is silent.
        (list[p.Awaiting].ProcessorAmount, list[p.Mismatch].ProcessorAmount, list[p.Posted].ProcessorAmount)
            .ShouldBe(((decimal?)10m, (decimal?)20m, (decimal?)null));
        list[p.Failed].FailedAt.ShouldNotBeNull();
        list.Values.Where(x => x.Id != p.Failed).ShouldAllBe(x => x.FailedAt == null);
        list[p.Posted].PostedAt.ShouldNotBeNull();
        list.Values.Where(x => x.Id != p.Posted).ShouldAllBe(x => x.PostedAt == null);
        var closed = list[p.Closed];
        (closed.ReviewNote, closed.ReviewClosedAt).ShouldBe(("Processor confirmed it was a test.", (DateTime?)h.Clock.GetUtcNow().UtcDateTime));
        list[p.Posted].PayoutReference.ShouldBe("po_posted");
    }

    [Fact]
    public async Task Each_line_is_shown_in_item_order_with_the_payment_it_names_and_what_it_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var p = await HoldEveryKindAsync(h, ct);
        var stored = await h.ReadItemsAsync(p.Posted, ct);
        var fee = stored.Single(x => x.Item == "1");
        var receipt = stored.Single(x => x.Item == "2");

        var list = (await h.QueryAsync(new GetSettlements(), ct)).ToDictionary(x => x.Id);

        // A fee belongs to the payout, so it names no payment.
        list[p.Posted].Items.ShouldBe([
            new SettlementItemView("1", SettlementItemKinds.Fee, null, 0m, 0.50m, -0.50m, null, fee.FeeEntryId),
            new SettlementItemView("2", SettlementItemKinds.Payment, p.PostedPayment.Id, 100m, 0m, 100m, receipt.EntryId, null),
        ]);
        fee.FeeEntryId.ShouldNotBeNull();
        receipt.EntryId.ShouldNotBeNull();
        // A line naming a payment LeaseBook does not hold names none.
        list[p.Received].Items.ShouldBe([new SettlementItemView("1", SettlementItemKinds.Payment, null, 7m, 0m, 7m, null, null)]);
        // Every payout shows its own lines and no other's.
        list.Values.ShouldAllBe(x => x.Items.Count == (x.Id == p.Posted ? 2 : 1));
    }

    [Fact]
    public async Task Payouts_are_listed_newest_first_and_one_is_read_by_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(10m, ct);
        var a = await h.SettleAsync(h.Payout("po_a", 9m, Line("1", "Payment", op, 10m, 0m, 10m)), ct);
        var b = await h.SettleAsync(h.Payout("po_b", 8m, Line("1", "Payment", op, 10m, 0m, 10m)), ct);
        var c = await h.SettleAsync(h.Payout("po_c", 7m, Line("1", "Payment", op, 10m, 0m, 10m)), ct);
        var now = h.Clock.GetUtcNow().UtcDateTime;
        await h.RestampAsync<PaymentSettlement>(a.Id, now.AddHours(-2), ct);
        await h.RestampAsync<PaymentSettlement>(b.Id, now.AddHours(-1), ct);
        await h.RestampAsync<PaymentSettlement>(c.Id, now.AddHours(-1), ct);
        Guid[] tied = string.CompareOrdinal(b.Id.ToString(), c.Id.ToString()) > 0 ? [b.Id, c.Id] : [c.Id, b.Id];

        (await h.QueryAsync(new GetSettlements(), ct)).Select(x => x.Id).ShouldBe([.. tied, a.Id]);

        var one = (await h.QueryAsync(new GetSettlements(a.Id), ct)).ShouldHaveSingleItem();
        (one.PayoutReference, one.BankAmount, one.CreatedAt).ShouldBe(("po_a", (decimal?)9m, now.AddHours(-2)));
        one.Items.ShouldHaveSingleItem().PaymentId.ShouldBe(op.Id);
        (await h.QueryAsync(new GetSettlements(UuidV7.NewId()), ct)).ShouldBeEmpty();
    }
}
