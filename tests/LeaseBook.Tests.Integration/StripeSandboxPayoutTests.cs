using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// Payouts (ADR-054, #513 step 7): what the adapter reads a payout event as, and what it asks Stripe
// to read one. The payout, its fourteen balance lines and its four events are recordings. No payout
// has failed in a sandbox, so `payout.failed` is the recorded payout with its status changed.
public sealed partial class StripeSandboxProcessorTests
{
    internal const string PayoutRun = StripeReplay.RefundRun;
    internal const string PayoutEvents = "029-payout-events.json";
    internal const string PayoutLines = "023-payout-lines.json";
    internal const string RecordedPayout = "po_synthetic000001";
    private const string Lines = "/v1/balance_transactions";

    [Fact]
    public async Task The_recorded_paid_payout_is_read_with_stripes_own_list_of_what_it_contains()
    {
        var h = new Adapter();
        h.Stripe.Answer = RecordedPayoutAnswers();

        var payout = (await ReadPayout(h, PayoutEvent("payout.paid"))).Value.ShouldNotBeNull();

        // Stripe's word for what it paid, on the binding's generation and bank. No bank amount, date or evidence: it has none.
        (payout.PayoutId, payout.Account, payout.Mode, payout.Generation, payout.PayoutType, payout.State, payout.Amount, payout.Currency, payout.BankId)
            .ShouldBe((RecordedPayout, RecordedAccount, PaymentModes.StripeSandbox, h.Binding.Generation, "standard", "Paid", 1470.60m, "USD", h.Binding.BankId));
        payout.ObservedAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1791506838).UtcDateTime);
        // Every line but the payout's own, in Stripe's order, whoever's payment it names. A dispute
        // on an ACH debit is the whole charge going back and, apart from it, Stripe's fee of 15.00.
        payout.Items.ShouldBe(
        [
            new("txn_synthetic000006", "Payment", "pi_synthetic000004", 125.00m, 1.00m, 124.00m, "USD"),
            new("txn_synthetic000007", "Return", "pi_synthetic000005", 200.00m, 0m, -200.00m, "USD"),
            new("txn_synthetic000007.fee", "Fee", "", 0m, 15.00m, -15.00m, "USD"),
            new("txn_synthetic000008", "Payment", "pi_synthetic000005", 200.00m, 1.60m, 198.40m, "USD"),
            new("txn_synthetic000009", "FailedDebit", "pi_synthetic000006", -200.00m, 2.40m, -202.40m, "USD"),
            new("txn_synthetic000010", "Payment", "pi_synthetic000006", 200.00m, 1.60m, 198.40m, "USD"),
            new("txn_synthetic000011", "Payment", "pi_synthetic000007", 504.03m, 4.03m, 500.00m, "USD"),
            new("txn_synthetic000012", "Payment", "pi_synthetic000008", 200.00m, 6.10m, 193.90m, "USD"),
            new("txn_synthetic000013", "Return", "pi_synthetic000009", 200.00m, 0m, -200.00m, "USD"),
            new("txn_synthetic000013.fee", "Fee", "", 0m, 15.00m, -15.00m, "USD"),
            new("txn_synthetic000014", "Payment", "pi_synthetic000009", 200.00m, 1.60m, 198.40m, "USD"),
            new("txn_synthetic000015", "FailedDebit", "pi_synthetic000010", -200.00m, 2.40m, -202.40m, "USD"),
            new("txn_synthetic000016", "Payment", "pi_synthetic000010", 200.00m, 1.60m, 198.40m, "USD"),
            new("txn_synthetic000017", "Payment", "pi_synthetic000011", 504.03m, 4.03m, 500.00m, "USD"),
            new("txn_synthetic000018", "Payment", "pi_synthetic000012", 200.00m, 6.10m, 193.90m, "USD"),
        ]);
        // Nothing was left out: what was read comes to what Stripe says it paid, to the cent.
        payout.Items.Sum(x => x.Net).ShouldBe(payout.Amount);
        // Asked for the payout's lines, then for the charge behind each dispute. By id, and nothing else.
        h.Stripe.Requests.Select(x => x.Path).ShouldBe([Lines, "/v1/charges/py_synthetic000002", "/v1/charges/py_synthetic000005"]);
        ShouldCarryOnlyPayoutIds(h.Stripe.Requests, h.Binding.Account, RecordedPayout);
    }

    // Of the four events Stripe sent about the payout, one is read. That a payout was made, updated
    // or reconciled is grounds for nothing, and none of those asks Stripe anything.
    [Theory]
    [InlineData("payout.created")]
    [InlineData("payout.reconciliation_completed")]
    [InlineData("payout.updated")]
    public async Task The_other_recorded_payout_events_are_acknowledged_and_dropped(string type)
    {
        var h = new Adapter();

        (await ReadPayout(h, PayoutEvent(type))).ShouldBe(ProcessorRead<ProcessorPayout>.NotOurs);
        (await Read(h, PayoutEvent(type))).ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs);

        h.Stripe.Requests.ShouldBeEmpty();
        // No payout event says anything of a payment, the one that is read included.
        (await Read(h, PayoutEvent("payout.paid"))).ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs);
        // And a payment's event says nothing of a payout.
        (await ReadPayout(h, StripeReplay.Delivered("005-webhook-connect.json"))).ShouldBe(ProcessorRead<ProcessorPayout>.NotOurs);
    }

    // Not recorded: no sandbox payout has failed. The recorded payout with Stripe's status for one that did.
    [Fact]
    public async Task A_failed_payout_is_read_as_failed_with_no_lines_and_asks_nothing()
    {
        var h = new Adapter();

        var payout = (await ReadPayout(h, FailedPayout())).Value.ShouldNotBeNull();

        (payout.PayoutId, payout.State, payout.Amount, payout.PayoutType, payout.Generation, payout.BankId)
            .ShouldBe((RecordedPayout, "Failed", 1470.60m, "standard", h.Binding.Generation, h.Binding.BankId));
        payout.Items.ShouldBeEmpty();
        h.Stripe.Requests.ShouldBeEmpty();
    }

    // The adapter never decides what a payout covers. A payout a person made has no list of Stripe's
    // own, so none is asked for and none is made up: it is named manual, which nothing posts.
    [Fact]
    public async Task A_manual_payout_is_read_without_lines_and_is_not_asked_about()
    {
        var h = new Adapter();
        var manual = PayoutEvent("payout.paid");
        manual["data"]!["object"]!["automatic"] = false;

        var payout = (await ReadPayout(h, manual)).Value.ShouldNotBeNull();

        (payout.PayoutType, payout.State, payout.Amount).ShouldBe(("manual", "Paid", 1470.60m));
        payout.Items.ShouldBeEmpty();
        h.Stripe.Requests.ShouldBeEmpty();

        // An automatic payout by Stripe's other method keeps its lines and that method's name.
        h.Stripe.Answer = RecordedPayoutAnswers();
        var instant = PayoutEvent("payout.paid");
        instant["data"]!["object"]!["method"] = "instant";
        var read = (await ReadPayout(h, instant)).Value.ShouldNotBeNull();
        (read.PayoutType, read.Items.Count).ShouldBe(("instant", 15));
    }

    [Theory]
    [InlineData("event marked live")]
    [InlineData("event does not say whether it is live")]
    [InlineData("payout marked live")]
    [InlineData("not a payout")]
    [InlineData("no payout id")]
    [InlineData("a payout id that is not an id")]
    [InlineData("no amount")]
    [InlineData("amount as text")]
    [InlineData("an amount in fractions of a cent")]
    [InlineData("an amount larger than is kept")]
    [InlineData("no currency")]
    [InlineData("a currency that is not three letters")]
    [InlineData("does not say whether it is automatic")]
    [InlineData("no method")]
    [InlineData("a method longer than is kept")]
    public async Task A_payout_event_that_cannot_be_taken_as_it_stands_is_unreadable_and_asks_nothing(string difference)
    {
        var h = new Adapter();
        h.Stripe.Answer = RecordedPayoutAnswers();
        var changed = PayoutEvent("payout.paid");
        var payout = changed["data"]!["object"]!.AsObject();
        switch (difference)
        {
            case "event marked live": changed["livemode"] = true; break;
            case "event does not say whether it is live": changed.Remove("livemode"); break;
            case "payout marked live": payout["livemode"] = true; break;
            case "not a payout": payout["object"] = "transfer"; break;
            case "no payout id": payout.Remove("id"); break;
            case "a payout id that is not an id": payout["id"] = "po_synthetic000001&payout=po_other"; break;
            case "no amount": payout.Remove("amount"); break;
            case "amount as text": payout["amount"] = "147060"; break;
            case "an amount in fractions of a cent": payout["amount"] = 147060.5; break;
            case "an amount larger than is kept": payout["amount"] = 100_000_000_000_000; break;
            case "no currency": payout.Remove("currency"); break;
            case "a currency that is not three letters": payout["currency"] = "dollars"; break;
            case "does not say whether it is automatic": payout.Remove("automatic"); break;
            case "no method": payout.Remove("method"); break;
            case "a method longer than is kept": payout["method"] = new string('m', 21); break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }

        (await ReadPayout(h, changed)).ShouldBe(ProcessorRead<ProcessorPayout>.Of(null));

        h.Stripe.Requests.ShouldBeEmpty();
        // As recorded it is read: the difference is what refused it.
        (await ReadPayout(h, PayoutEvent("payout.paid"))).Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_payout_event_for_an_account_no_fixture_is_bound_to_is_not_ours_and_asks_nothing()
    {
        var h = new Adapter();
        var elsewhere = PayoutEvent("payout.paid");
        elsewhere["account"] = "acct_synthetic000009";

        (await ReadPayout(h, elsewhere)).ShouldBe(ProcessorRead<ProcessorPayout>.NotOurs);
        var platform = PayoutEvent("payout.paid");
        platform.Remove("account");
        (await ReadPayout(h, platform)).ShouldBe(ProcessorRead<ProcessorPayout>.NotOurs);

        h.Stripe.Requests.ShouldBeEmpty();
    }

    // Each is one line beside one good payment line. None is left out, and none can post: only a
    // charge's line, an ACH dispute and that dispute's fee are kinds the engine posts. No refund line
    // has been recorded in a payout, so those two are a recorded charge line under Stripe's type name.
    [Theory]
    [InlineData("a refund", "Refund")]
    [InlineData("a refund of an ACH debit", "Refund")]
    [InlineData("a failed debit taken back", "FailedDebit")]
    [InlineData("a type nobody has named", "Unknown")]
    [InlineData("a card dispute", "Dispute")]
    [InlineData("a dispute whose charge stripe does not hold", "Dispute")]
    [InlineData("a dispute whose charge is another payment's", "Dispute")]
    [InlineData("a dispute that names no charge", "Dispute")]
    [InlineData("a dispute that names no payment", "Dispute")]
    [InlineData("a dispute that gives money back", "Dispute")]
    [InlineData("a dispute whose net is not its amount less its fee", "Dispute")]
    [InlineData("a dispute in another currency", "Dispute")]
    [InlineData("an adjustment for something else", "Unknown")]
    [InlineData("a charge line whose charge is not given", "Unknown")]
    [InlineData("a charge line whose charge names no payment", "Unknown")]
    [InlineData("another payout's line", "Unknown")]
    public async Task A_line_that_is_not_a_payment_or_an_ach_return_is_stored_under_a_kind_nothing_posts(string difference, string kind)
    {
        var h = new Adapter();
        var good = RecordedLine("txn_synthetic000008");
        var odd = RecordedLine(difference.Contains("dispute", StringComparison.Ordinal) ? "txn_synthetic000007"
            : difference == "a failed debit taken back" ? "txn_synthetic000009"
            : difference == "another payout's line" ? "txn_synthetic000005" : "txn_synthetic000012");
        var source = odd["source"]!.AsObject();
        Action<JsonObject>? charge = null;
        var held = true;
        switch (difference)
        {
            case "a refund": odd["type"] = "refund"; break;
            case "a refund of an ACH debit": odd["type"] = "payment_refund"; break;
            case "a failed debit taken back": break;
            case "a type nobody has named": odd["type"] = "reserve_transaction"; break;
            case "a card dispute": charge = x => x["payment_method_details"] = new JsonObject { ["type"] = "card" }; break;
            case "a dispute whose charge stripe does not hold": held = false; break;
            case "a dispute whose charge is another payment's": charge = x => x["payment_intent"] = "pi_synthetic000004"; break;
            case "a dispute that names no charge": source["charge"] = null; break;
            case "a dispute that names no payment": source["payment_intent"] = null; break;
            case "a dispute that gives money back": (odd["amount"], odd["fee"], odd["net"]) = (20000L, 0L, 20000L); break;
            case "a dispute whose net is not its amount less its fee": odd["net"] = -20000L; break;
            case "a dispute in another currency": odd["currency"] = "eur"; break;
            case "an adjustment for something else": odd["type"] = "adjustment"; break;
            case "a charge line whose charge is not given": odd["source"] = "ch_synthetic000004"; break;
            case "a charge line whose charge names no payment": source["payment_intent"] = null; break;
            case "another payout's line": source["id"] = "po_synthetic000002"; break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }
        h.Stripe.Answer = sent => sent.Path == Lines ? LineList(false, RecordedLine("txn_synthetic000005"), good, odd)
            : sent.Path == "/v1/charges/py_synthetic000002" && held ? AchCharge("py_synthetic000002", "pi_synthetic000005", charge)
            : sent.Path.StartsWith("/v1/charges/", StringComparison.Ordinal) ? (404, Error("invalid_request_error", "resource_missing"))
            : throw new InvalidOperationException("Unexpected request " + sent.Path);

        var payout = (await ReadPayout(h, PayoutEvent("payout.paid"))).Value.ShouldNotBeNull();

        payout.Items.Count.ShouldBe(2);
        payout.Items[0].ShouldBe(new("txn_synthetic000008", "Payment", "pi_synthetic000005", 200.00m, 1.60m, 198.40m, "USD"));
        var stored = payout.Items[1];
        stored.Kind.ShouldBe(kind);
        stored.Kind.ShouldNotBeOneOf(SettlementItemKinds.Payment, SettlementItemKinds.Return, SettlementItemKinds.Fee);
        // Stripe's own figures, signed as Stripe signs them, under Stripe's reference for the line.
        (stored.Item, stored.Gross, stored.Fee, stored.Net).ShouldBe(((string)odd["id"]!, (long)odd["amount"]! / 100m, (long)odd["fee"]! / 100m, (long)odd["net"]! / 100m));
        ShouldCarryOnlyPayoutIds(h.Stripe.Requests, h.Binding.Account, RecordedPayout);
    }

    // Part of a list would be a payout that does not come to what Stripe paid. Thrown, so that the
    // delivery is not acknowledged and the sweep asks again; never a verdict on the payout.
    [Theory]
    [InlineData("stripe faults")]
    [InlineData("stripe refuses the key")]
    [InlineData("no connection")]
    [InlineData("not json")]
    [InlineData("not a list")]
    [InlineData("a list that does not say whether there is more")]
    [InlineData("more to come and nothing listed")]
    [InlineData("a line with no amount")]
    [InlineData("a line with an amount in fractions of a cent")]
    [InlineData("a line with no fee")]
    [InlineData("a line with no net")]
    [InlineData("a line with no currency")]
    [InlineData("a line with no type")]
    [InlineData("a line with no id")]
    [InlineData("a line whose id is longer than is kept")]
    [InlineData("a line that is not a balance transaction")]
    [InlineData("the second page faults")]
    public async Task A_payout_whose_lines_cannot_be_had_whole_is_thrown_and_not_judged(string difference)
    {
        var h = new Adapter();
        var line = RecordedLine("txn_synthetic000008");
        var answer = LineList(false, RecordedLine("txn_synthetic000005"), line);
        switch (difference)
        {
            case "stripe faults": answer = (500, Error("api_error", null)); break;
            case "stripe refuses the key": answer = (401, Error("authentication_error", null)); break;
            case "no connection" or "the second page faults": break;
            case "not json": answer = (200, "<html>"); break;
            case "not a list": answer = (200, "[]"); break;
            case "a list that does not say whether there is more": answer = (200, new JsonObject { ["object"] = "list", ["data"] = new JsonArray() }.ToJsonString()); break;
            case "more to come and nothing listed": answer = LineList(true); break;
            case "a line with no amount": line.Remove("amount"); answer = LineList(false, line); break;
            case "a line with an amount in fractions of a cent": line["amount"] = 20000.5; answer = LineList(false, line); break;
            case "a line with no fee": line.Remove("fee"); answer = LineList(false, line); break;
            case "a line with no net": line.Remove("net"); answer = LineList(false, line); break;
            case "a line with no currency": line.Remove("currency"); answer = LineList(false, line); break;
            case "a line with no type": line.Remove("type"); answer = LineList(false, line); break;
            case "a line with no id": line.Remove("id"); answer = LineList(false, line); break;
            case "a line whose id is longer than is kept": line["id"] = "txn_" + new string('a', 53); answer = LineList(false, line); break;
            case "a line that is not a balance transaction": line["object"] = "charge"; answer = LineList(false, line); break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }
        h.Stripe.Answer = sent => difference == "no connection" ? throw new HttpRequestException("No route to the sandbox.")
            : difference == "the second page faults" ? sent.Query.ContainsKey("starting_after") ? (500, Error("api_error", null)) : LineList(true, line)
            : answer;

        var thrown = await Should.ThrowAsync<Exception>(() => ReadPayout(h, PayoutEvent("payout.paid")));

        thrown.ShouldNotBeOfType<PaymentConflictException>();
        thrown.ShouldNotBeOfType<PaymentUnavailableException>();
        // And in a sweep it is passed over and counted, to be asked about again; nothing is kept of it.
        var failing = h.Stripe.Answer;
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? StripeReplay.Recorded(PayoutEvents, PayoutRun) : failing(sent);
        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);
        (swept.Payouts.Count, swept.Observations.Count, swept.Listed, swept.Unreadable, swept.Unasked).ShouldBe((0, 0, 4, 0, 1));
    }

    // A test event whose lines name something live: the key is reading a live account.
    [Fact]
    public async Task A_payout_whose_lines_name_something_stripe_marks_live_is_refused()
    {
        var h = new Adapter();
        var live = RecordedLine("txn_synthetic000008");
        live["source"]!["livemode"] = true;
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? StripeReplay.Recorded(PayoutEvents, PayoutRun) : LineList(false, RecordedLine("txn_synthetic000005"), live);

        (await ReadPayout(h, PayoutEvent("payout.paid"))).ShouldBe(ProcessorRead<ProcessorPayout>.Of(null));
        // In a sweep it refuses the whole list, as anything else live in it does.
        await Should.ThrowAsync<PaymentUnavailableException>(() => h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct));
    }

    [Fact]
    public async Task A_payouts_lines_are_read_page_by_page_and_each_request_names_only_the_payout_and_where_to_go_on_from()
    {
        var h = new Adapter();
        var all = StripeReplay.RecordedBody(PayoutLines, PayoutRun)["data"]!.AsArray().Select(x => x!.AsObject()).ToArray();
        h.Stripe.Answer = sent => sent.Path != Lines ? RecordedPayoutAnswers()(sent)
            : !sent.Query.TryGetValue("starting_after", out var after) ? LineList(true, [.. all.Take(5)])
            : after == "txn_synthetic000009" ? LineList(true, [.. all.Skip(5).Take(5)])
            : after == "txn_synthetic000014" ? LineList(false, [.. all.Skip(10)])
            : throw new InvalidOperationException("Unexpected page " + after);

        var payout = (await ReadPayout(h, PayoutEvent("payout.paid"))).Value.ShouldNotBeNull();

        (payout.Items.Count, payout.Items.Sum(x => x.Net)).ShouldBe((15, 1470.60m));
        var pages = h.Stripe.Requests.Where(x => x.Path == Lines).ToArray();
        pages.Select(x => x.Query.GetValueOrDefault("starting_after")).ShouldBe([null, "txn_synthetic000009", "txn_synthetic000014"]);
        pages.ShouldAllBe(x => x.Query["payout"] == RecordedPayout && x.Query["limit"] == "100" && x.Query["expand[]"] == "data.source");
        ShouldCarryOnlyPayoutIds(h.Stripe.Requests, h.Binding.Account, RecordedPayout);
    }

    // The recorded list of the payout's four events, swept: the one that is read is read as its
    // delivery is, with the same questions asked of Stripe, and the other three are passed over.
    [Fact]
    public async Task A_sweep_reads_a_listed_payout_event_exactly_as_a_delivered_one()
    {
        var h = new Adapter();
        var answers = RecordedPayoutAnswers();
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? StripeReplay.Recorded(PayoutEvents, PayoutRun) : answers(sent);
        var delivered = (await ReadPayout(h, PayoutEvent("payout.paid"))).Value.ShouldNotBeNull();
        var before = h.Stripe.Requests.Count;

        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        (swept.Listed, swept.Unreadable, swept.Unasked, swept.Observations.Count).ShouldBe((4, 0, 0, 0));
        var payout = swept.Payouts.ShouldHaveSingleItem();
        (payout with { Items = [] }).ShouldBe(delivered with { Items = [] });
        payout.Items.ShouldBe(delivered.Items);
        var asked = h.Stripe.Requests.Skip(before).ToArray();
        asked.Select(x => x.Path).ShouldBe(["/v1/events", Lines, "/v1/charges/py_synthetic000002", "/v1/charges/py_synthetic000005"]);
        asked[0].Query.Where(x => x.Key.StartsWith("types[", StringComparison.Ordinal)).Select(x => x.Value).ShouldContain("payout.paid");
        asked[0].Query.Where(x => x.Key.StartsWith("types[", StringComparison.Ordinal)).Select(x => x.Value).ShouldContain("payout.failed");
        ShouldCarryOnlyPayoutIds(asked.Skip(1), h.Binding.Account, RecordedPayout);

        // A list asked of one account that names another is not read for that other, and nothing is asked on the strength of it.
        var elsewhere = PayoutEvent("payout.paid");
        elsewhere["account"] = "acct_synthetic000009";
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? Page(false, elsewhere) : throw new InvalidOperationException("Unexpected request " + sent.Path);
        (await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct)).Payouts.ShouldBeEmpty();
    }

    /// <summary>One of the four recorded events about the payout, as Stripe listed it. A copy, for a test to change.</summary>
    internal static JsonObject PayoutEvent(string type) =>
        (JsonObject)StripeReplay.RecordedBody(PayoutEvents, PayoutRun)["data"]!.AsArray().Single(x => (string)x!["type"]! == type)!.DeepClone();

    /// <summary>
    /// The event Stripe sends when a payout fails. None has been recorded, because no sandbox payout
    /// has failed: this is the recorded payout under that event's type, with the status and the
    /// failure code Stripe documents for one, and an id of its own.
    /// </summary>
    internal static JsonObject FailedPayout(JsonObject? paid = null, string eventId = "evt_built_payout_failed")
    {
        var failed = (JsonObject)(paid ?? PayoutEvent("payout.paid")).DeepClone();
        failed["id"] = eventId;
        failed["type"] = "payout.failed";
        failed["created"] = (long)failed["created"]! + 3600;
        var payout = failed["data"]!["object"]!.AsObject();
        payout["status"] = "failed";
        payout["failure_code"] = "account_closed";
        payout["failure_message"] = "[scrubbed]";
        return failed;
    }

    /// <summary>One recorded balance line of the payout, with the object it came from. A copy.</summary>
    internal static JsonObject RecordedLine(string id) =>
        (JsonObject)StripeReplay.RecordedBody(PayoutLines, PayoutRun)["data"]!.AsArray().Single(x => (string)x!["id"]! == id)!.DeepClone();

    internal static (int Status, string Body) LineList(bool hasMore, params JsonObject[] lines) =>
        (200, new JsonObject { ["object"] = "list", ["has_more"] = hasMore, ["url"] = "/v1/balance_transactions", ["data"] = new JsonArray([.. lines.Select(x => x.DeepClone())]) }.ToJsonString());

    /// <summary>The recorded ACH charge under another id and payment: the charge a recorded dispute line names.</summary>
    internal static (int Status, string Body) AchCharge(string id, string payment, Action<JsonObject>? change = null)
    {
        var charge = StripeReplay.RecordedBody("052-ach-dispute-charge.json");
        ((string)charge["payment_method_details"]!["type"]!).ShouldBe("us_bank_account");
        charge["id"] = id;
        charge["payment_intent"] = payment;
        change?.Invoke(charge);
        return (200, charge.ToJsonString());
    }

    /// <summary>The recorded payout's lines, and the charges behind its two disputes, both ACH debits. Anything else asked for throws.</summary>
    internal static Func<StripeReplay.Sent, (int Status, string Body)> RecordedPayoutAnswers() => sent => sent.Path switch
    {
        Lines => StripeReplay.Recorded(PayoutLines, PayoutRun),
        "/v1/charges/py_synthetic000002" => AchCharge("py_synthetic000002", "pi_synthetic000005"),
        "/v1/charges/py_synthetic000005" => AchCharge("py_synthetic000005", "pi_synthetic000009"),
        _ => throw new InvalidOperationException("Unexpected request " + sent.Path),
    };

    /// <summary>
    /// What reading a payout may ask Stripe: the payout's balance lines, by the payout's id, and one
    /// charge by its id for each dispute among them. On the fixture's account, with no body and
    /// nothing to make idempotent. Nothing about a tenant, a lease or an amount is in any of it.
    /// </summary>
    internal static void ShouldCarryOnlyPayoutIds(IEnumerable<StripeReplay.Sent> requests, string account, params string[] payouts)
    {
        foreach (var sent in requests)
        {
            sent.Method.ShouldBe("GET");
            sent.Form.ShouldBeEmpty(sent.Path);
            sent.Headers["Stripe-Account"].ShouldBe(account);
            sent.Headers.ShouldNotContainKey("Idempotency-Key");
            if (sent.Path != Lines) { ShouldCarryOnlyIds([sent], account); continue; }
            sent.Query.Keys.ShouldBeSubsetOf(["payout", "limit", "expand[]", "starting_after"]);
            payouts.ShouldContain(sent.Query["payout"]);
            (sent.Query["limit"], sent.Query["expand[]"]).ShouldBe(("100", "data.source"));
            if (sent.Query.TryGetValue("starting_after", out var after))
            { System.Text.RegularExpressions.Regex.IsMatch(after, "^[A-Za-z0-9_]+$").ShouldBeTrue(after); }
        }
    }

    private static Task<ProcessorRead<ProcessorPayout>> ReadPayout(Adapter h, JsonNode @event) =>
        h.Processor.ReadPayoutAsync(Authentic(h, Bytes(@event)), Ct);
}

// Payouts in a whole sandbox host: a payout Stripe marks paid is stored and held, the fixture's bank
// command releases it, and everything that is not a clean payout goes to a person. Nothing posts
// before a bank has spoken, and nothing posts in part.
public sealed partial class StripeSandboxHostTests
{
    private const string Payout = "po_host000001";

    [Fact]
    public async Task A_payout_stripe_marks_paid_is_held_for_the_bank_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        var entries = await h.JournalEntries(ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        var before = sandbox.Replay.Requests.Count;

        var delivered = await h.Deliver(PaidPayout(Payout, 198.40m), ct);

        delivered.Status.ShouldBe(HttpStatusCode.NoContent, delivered.Body);
        var (payout, items) = await Stored(h, Payout, ct);
        // Stripe's figure and Stripe's lines. No bank amount, date or evidence: no bank has spoken.
        (payout.Status, payout.Reason, payout.Mode, payout.Account, payout.Generation, payout.BankId, payout.PayoutType, payout.Currency)
            .ShouldBe(("AwaitingBank", null, PaymentModes.StripeSandbox, Account, h.Binding.Generation, h.Binding.BankId, "standard", "USD"));
        (payout.ProcessorAmount, payout.BankAmount, payout.BankDate, payout.EvidenceId, payout.Banked, payout.PostedAt, payout.FailedAt)
            .ShouldBe((198.40m, 0m, default, "", false, null, null));
        var item = items.ShouldHaveSingleItem();
        (item.Kind, item.ProviderId, item.Gross, item.Fee, item.Net, item.EntryId, item.FeeEntryId)
            .ShouldBe(("Payment", Sandbox.IdFor(payment.Id), 200.00m, 1.60m, 198.40m, null, null));
        // Paid is Stripe's word and not a bank's: nothing is on the ledger, and the payment still waits.
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Effects(h, ct)).ShouldBe(0);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing");
        // One question asked of Stripe to read it: the payout's lines, by the payout's id.
        var asked = sandbox.Replay.Requests.Skip(before).ToArray();
        asked.ShouldHaveSingleItem().Path.ShouldBe("/v1/balance_transactions");
        StripeSandboxProcessorTests.ShouldCarryOnlyPayoutIds(asked, Account, Payout);

        // An administrator cannot post it either. Only a bank's evidence releases it, and no route supplies that.
        var view = await View(h, payout.Id, ct);
        (view.Status, view.CanPost, view.CanClose, view.BankAmount, view.ProcessorAmount, view.FailedAt).ShouldBe(("AwaitingBank", false, true, 0m, 198.40m, null));
        (await Post(h, payout.Id, ct)).Refusal.ShouldBe("bank_evidence_missing");
        (await Stored(h, Payout, ct)).Payout.Status.ShouldBe("AwaitingBank");
        (await h.JournalEntries(ct)).ShouldBe(entries);
        h.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText).Where(x => x!.StartsWith("/callbacks/", StringComparison.Ordinal))
            .ShouldBe(["/callbacks/payments/stripe"]);
    }

    [Fact]
    public async Task Bank_evidence_equal_to_stripes_net_posts_every_line_and_a_fee_difference_has_no_owner_property_or_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var first = await Paid(h, 200m, ct);
        var second = await Paid(h, 300m, ct);
        var entries = await h.JournalEntries(ct);
        // Stripe kept 0.8% of each, and the tenant was quoted nothing: 1.60 and 2.40 are the PM's to bear.
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 496.00m), PaymentLine(h, first, 1.60m), PaymentLine(h, second, 2.40m));
        (await h.Deliver(PaidPayout(Payout, 496.00m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await h.JournalEntries(ct)).ShouldBe(entries);

        (await Bank(h, Payout, 496.00m, ct)).ShouldBe(0);

        var (payout, items) = await Stored(h, Payout, ct);
        (payout.Status, payout.Reason, payout.BankAmount, payout.BankDate, payout.ProcessorAmount, payout.Banked, payout.PostedBy)
            .ShouldBe(("Posted", null, 496.00m, Today(h), 496.00m, true, null));
        payout.PostedAt.ShouldNotBeNull();
        items.ShouldAllBe(x => x.EntryId != null && x.FeeEntryId != null);
        // Two receipts for what each tenant paid toward the ledger, and two fee differences beside them.
        (await h.JournalEntries(ct)).ShouldBe(entries + 4);
        foreach (var payment in new[] { first, second })
        {
            var operation = await h.Operation(payment.Id, ct);
            (operation.Status, operation.Reason, operation.JournalId.HasValue).ShouldBe(("Settled", null, true));
        }
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<PaymentEffect>().Where(x => x.SettlementId == payout.Id).Select(x => x.Kind).ToListAsync(ct))
                .ShouldBe(["Receipt", "Receipt", "PaymentFee", "PaymentFee"], ignoreOrder: true);
            var posted = await db.Set<JournalEntry>().Include(x => x.Lines)
                .Where(x => x.SourceRef != null && x.SourceRef.StartsWith("payout:" + Payout + ":")).ToListAsync(ct);
            posted.Count.ShouldBe(4);
            posted.ShouldAllBe(x => x.EntryDate == Today(h));
            // The fee difference is the one template that posts it, and nothing it posts can reach an owner, a property or a tenant.
            var fees = posted.Where(x => x.SourceRef!.EndsWith(":fee", StringComparison.Ordinal)).ToArray();
            fees.Length.ShouldBe(2);
            fees.ShouldAllBe(x => x.EventType == nameof(ProcessorFeeDifference));
            fees.SelectMany(x => x.Lines).ShouldAllBe(x => x.OwnerId == null && x.PropertyId == null && x.TenantId == null && x.UnitId == null
                && x.BankAccountId == h.Binding.BankId);
            fees.Select(x => x.Lines.Sum(l => l.Debit?.Amount ?? 0m)).ShouldBe([1.60m, 2.40m], ignoreOrder: true);
            posted.Except(fees).ShouldAllBe(x => x.EventType != nameof(ProcessorFeeDifference));
            (await new InvariantChecks(db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);

        // Given once. The same again changes nothing; a different one is refused and a posted payout keeps its outcome.
        (await Bank(h, Payout, 496.00m, ct)).ShouldBe(0);
        (await Bank(h, Payout, 497.00m, ct)).ShouldBe(1);
        (await Bank(h, Payout, 496.00m, ct, Today(h).AddDays(-1))).ShouldBe(1);
        (await h.JournalEntries(ct)).ShouldBe(entries + 4);
        var after = (await Stored(h, Payout, ct)).Payout;
        (after.Status, after.Reason, after.BankAmount, after.BankDate).ShouldBe(("Posted", null, 496.00m, Today(h)));
        // And the events of the payout arriving again post nothing more.
        (await h.Deliver(PaidPayout(Payout, 496.00m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await h.JournalEntries(ct)).ShouldBe(entries + 4);
    }

    [Theory]
    [InlineData(495.99)]
    [InlineData(496.01)]
    [InlineData(0.00)]
    [InlineData(-496.00)]
    public async Task Bank_evidence_for_any_other_amount_posts_nothing_and_names_its_reason(double bank)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var first = await Paid(h, 200m, ct);
        var second = await Paid(h, 300m, ct);
        var entries = await h.JournalEntries(ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 496.00m), PaymentLine(h, first, 1.60m), PaymentLine(h, second, 2.40m));
        (await h.Deliver(PaidPayout(Payout, 496.00m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        // Kept: it is what the bank said. And then nothing in the payout can be trusted to be what reached it.
        (await Bank(h, Payout, (decimal)bank, ct)).ShouldBe(0);

        var (payout, items) = await Stored(h, Payout, ct);
        (payout.Status, payout.Reason, payout.BankAmount, payout.ProcessorAmount, payout.PostedAt).ShouldBe(("NeedsReview", "bank_amount_mismatch", (decimal)bank, 496.00m, null));
        items.ShouldAllBe(x => x.EntryId == null && x.FeeEntryId == null);
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Effects(h, ct)).ShouldBe(0);
        (await h.Operation(first.Id, ct)).Status.ShouldBe("Processing");
        var view = await View(h, payout.Id, ct);
        (view.CanPost, view.CanClose).ShouldBe((false, true));
        (await Post(h, payout.Id, ct)).Refusal.ShouldBe("bank_amount_mismatch");
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // The right amount afterwards is different evidence, not a correction: given once.
        (await Bank(h, Payout, 496.00m, ct)).ShouldBe(1);
        var conflicting = (await Stored(h, Payout, ct)).Payout;
        (conflicting.Status, conflicting.Reason, conflicting.BankAmount).ShouldBe(("NeedsReview", "conflicting_evidence", (decimal)bank));
        await Should.ThrowAsync<PaymentConflictException>(() => Post(h, payout.Id, ct));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Close(h, payout.Id, ct)).Settlement.Status.ShouldBe("Closed");
    }

    [Fact]
    public async Task A_fee_shortfall_the_held_fees_do_not_cover_names_pm_fees_insufficient_and_leaves_the_payout_unposted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var first = await Paid(h, 200m, ct);
        var second = await Paid(h, 300m, ct);
        var entries = await h.JournalEntries(ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 496.00m), PaymentLine(h, first, 1.60m), PaymentLine(h, second, 2.40m));
        (await h.Deliver(PaidPayout(Payout, 496.00m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        (await Bank(h, Payout, 496.00m, ct)).ShouldBe(0);

        // The bank agrees with Stripe and still nothing posts, the two receipts included: all or none.
        var (payout, items) = await Stored(h, Payout, ct);
        (payout.Status, payout.Reason, payout.BankAmount).ShouldBe(("NeedsReview", "pm_fees_insufficient", 496.00m));
        payout.ReasonItem.ShouldNotBeNull();
        items.Select(x => x.Item).ShouldContain(payout.ReasonItem);
        items.ShouldAllBe(x => x.EntryId == null && x.FeeEntryId == null);
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Effects(h, ct)).ShouldBe(0);
        (await h.Operation(first.Id, ct)).Status.ShouldBe("Processing");
        (await Post(h, payout.Id, ct)).Refusal.ShouldBe("pm_fees_insufficient");
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // Funded, an administrator posts it through the same path.
        await HoldFees(h, 10m, ct);
        var posted = await Post(h, payout.Id, ct);
        (posted.Refusal, posted.Settlement.Status).ShouldBe((null, "Posted"));
        (await h.JournalEntries(ct)).ShouldBe(entries + 1 + 4);
        await h.InOrg(async sp => (await new InvariantChecks(sp.GetRequiredService<AppDbContext>()).CheckCoreAsync(ct)).ShouldBeEmpty(), ct);
    }

    // The payment was banked by one payout and comes back in another, as the dispute on an ACH debit
    // that the bank's return is. Stripe's word for the return is never complete, so only the payout
    // can carry it to the ledger, and only when an administrator posts the payout.
    [Theory]
    [InlineData("posts")]
    [InlineData("closes")]
    public async Task A_payout_containing_an_ach_return_waits_for_an_administrator_who_posts_or_closes_it(string decision)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 25m, ct);
        var returned = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, returned, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);
        (await h.Operation(returned.Id, ct)).Status.ShouldBe("Settled");
        sandbox.HoldsCharge(Charge(h, returned, "052-ach-dispute-charge.json"));
        foreach (var file in new[] { DisputeCreated, DisputeWithdrawn, DisputeClosed })
        { (await h.Deliver(Disputed(StripeReplay.Delivered(file), returned), ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        await h.Run(ct);
        var disputed = await h.Operation(returned.Id, ct);
        (disputed.Status, disputed.Reason).ShouldBe(("NeedsReview", "return_requires_review"));
        // Stripe's word and not a bank's: never complete, so the single-payment return still refuses it.
        (await h.Facts(ct)).Where(x => x.Kind == "Return").ShouldAllBe(x => !x.Complete);
        await h.InOrg(async sp => (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>()
            .Send(new PostPaymentReturn(h.Binding, returned.Id), ct)).ShouldNotBeNull().Refusal.ShouldBe("return_partial_unsupported"), ct);
        var refused = await h.Operation(returned.Id, ct);
        (refused.Status, refused.Reason).ShouldBe(("NeedsReview", "return_partial_unsupported"));
        var other = await Paid(h, 300m, ct);
        var entries = await h.JournalEntries(ct);

        // The next payout: the other payment, less the 200.00 taken back and Stripe's 15.00 for the dispute.
        const string second = "po_host000002";
        sandbox.Lines[second] = LinesOf(PayoutLine(second, 82.60m), PaymentLine(h, other, 2.40m), DisputeLine(returned, "txn_dispute"));
        (await h.Deliver(PaidPayout(second, 82.60m, "evt_second_payout"), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        var (held, lines) = await Stored(h, second, ct);
        held.Status.ShouldBe("AwaitingBank");
        lines.Select(x => (x.Item, x.Kind, x.ProviderId, x.Gross, x.Fee, x.Net)).ShouldBe(
        [
            ("txn_dispute", "Return", Sandbox.IdFor(returned.Id), 200.00m, 0m, -200.00m),
            ("txn_dispute.fee", "Fee", "", 0m, 15.00m, -15.00m),
            (lines.Single(x => x.Kind == "Payment").Item, "Payment", Sandbox.IdFor(other.Id), 300.00m, 2.40m, 297.60m),
        ], ignoreOrder: true);

        (await Bank(h, second, 82.60m, ct)).ShouldBe(0);

        // The bank agrees, and it still waits: a return never posts by itself. Nor does the receipt beside it.
        var waiting = (await Stored(h, second, ct)).Payout;
        (waiting.Status, waiting.Reason, waiting.PostedAt).ShouldBe(("NeedsReview", "settlement_requires_confirmation", null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await h.Operation(other.Id, ct)).Status.ShouldBe("Processing");
        var view = await View(h, held.Id, ct);
        (view.CanPost, view.CanClose).ShouldBe((true, true));

        if (decision == "closes")
        {
            (await Close(h, held.Id, ct)).Settlement.Status.ShouldBe("Closed");
            (await h.JournalEntries(ct)).ShouldBe(entries);
            (await h.Operation(returned.Id, ct)).Status.ShouldBe("NeedsReview");
            await Should.ThrowAsync<PaymentConflictException>(() => Post(h, held.Id, ct));
            (await h.JournalEntries(ct)).ShouldBe(entries);
            return;
        }
        var administrator = UuidV7.NewId();
        var posted = await Post(h, held.Id, ct, administrator);

        (posted.Refusal, posted.Settlement.Status).ShouldBe((null, "Posted"));
        // The receipt and its fee difference, the receipt's reversal, and Stripe's dispute fee.
        (await h.JournalEntries(ct)).ShouldBe(entries + 4);
        (await h.Operation(returned.Id, ct)).Status.ShouldBe("Returned");
        (await h.Operation(other.Id, ct)).Status.ShouldBe("Settled");
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<PaymentSettlement>().SingleAsync(x => x.Id == held.Id, ct)).PostedBy.ShouldBe(administrator);
            var reversal = await db.Set<JournalEntry>().SingleAsync(x => x.SourceRef == "payout:" + second + ":txn_dispute", ct);
            reversal.ReversesEntryId.ShouldBe(disputed.JournalId);
            var fee = await db.Set<JournalEntry>().Include(x => x.Lines).SingleAsync(x => x.SourceRef == "payout:" + second + ":txn_dispute.fee:fee", ct);
            fee.EventType.ShouldBe(nameof(ProcessorFeeDifference));
            fee.Lines.Sum(x => x.Debit?.Amount ?? 0m).ShouldBe(15.00m);
            fee.Lines.ShouldAllBe(x => x.OwnerId == null && x.PropertyId == null && x.TenantId == null);
            (await db.Set<PaymentEffect>().Where(x => x.OperationId == returned.Id).Select(x => x.Kind).ToListAsync(ct))
                .ShouldBe(["Receipt", "PaymentFee", "Return"], ignoreOrder: true);
            (await new InvariantChecks(db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
        // The dispute's own events, judged again, agree with the return the payout posted.
        await h.Run(ct);
        (await h.Operation(returned.Id, ct)).Status.ShouldBe("Returned");
    }

    // Each is a payout of one good payment and one line LeaseBook does not post. It goes to a person
    // when it arrives, bank evidence equal to Stripe's net changes nothing, and nothing ever posts.
    [Theory]
    [InlineData("a refund")]
    [InlineData("a card dispute")]
    [InlineData("a failed debit taken back")]
    [InlineData("a type nobody has named")]
    public async Task A_payout_with_a_line_that_is_not_posted_is_stored_for_review_and_posts_nothing(string line)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 50m, ct);
        var good = await Paid(h, 300m, ct);
        var other = await Paid(h, 200m, ct);
        var entries = await h.JournalEntries(ct);
        JsonObject odd;
        string kind;
        switch (line)
        {
            case "a refund":
                // No refund line has been recorded in a payout: a recorded charge line under Stripe's type for one.
                odd = PaymentLine(h, other, 0m, "txn_odd");
                (odd["type"], odd["amount"], odd["net"]) = ("refund", -20000L, -20000L);
                kind = "Refund";
                break;
            case "a card dispute":
                sandbox.HoldsCharge(Charge(h, other, "009-card-dispute-charge.json", StripeReplay.CardDisputeRun));
                odd = DisputeLine(other, "txn_odd");
                kind = "Dispute";
                break;
            case "a failed debit taken back":
                odd = StripeSandboxProcessorTests.RecordedLine("txn_synthetic000009");
                odd["id"] = "txn_odd";
                Point(odd["source"]!.AsObject(), h, other);
                kind = "FailedDebit";
                break;
            default:
                odd = PaymentLine(h, other, 0m, "txn_odd");
                (odd["type"], odd["amount"], odd["net"]) = ("reserve_transaction", -5000L, -5000L);
                kind = "Unknown";
                break;
        }
        var net = 297.60m + (long)odd["net"]! / 100m;
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, net), PaymentLine(h, good, 2.40m), odd);

        (await h.Deliver(PaidPayout(Payout, net), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        var (payout, items) = await Stored(h, Payout, ct);
        (payout.Status, payout.Reason, payout.ProcessorAmount, payout.Banked).ShouldBe(("NeedsReview", "unsupported_settlement", net, false));
        // The line is there, as Stripe gave it, so the payout still comes to what Stripe paid.
        items.Select(x => (x.Item, x.Kind)).ShouldBe([("txn_odd", kind), (items.Single(x => x.Kind == "Payment").Item, "Payment")], ignoreOrder: true);
        items.Sum(x => x.Net).ShouldBe(net);
        (await h.JournalEntries(ct)).ShouldBe(entries);
        var view = await View(h, payout.Id, ct);
        (view.CanPost, view.CanClose).ShouldBe((false, true));

        // What the bank says is kept, and the payout stays with a person: nothing is checked again by itself.
        (await Bank(h, Payout, net, ct)).ShouldBe(0);
        var banked = (await Stored(h, Payout, ct)).Payout;
        (banked.Status, banked.Reason, banked.BankAmount, banked.Banked).ShouldBe(("NeedsReview", "unsupported_settlement", net, true));
        (await Post(h, payout.Id, ct)).Refusal.ShouldBe("unsupported_settlement");
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Effects(h, ct)).ShouldBe(0);
        (await h.Operation(good.Id, ct)).Status.ShouldBe("Processing");
        (await Close(h, payout.Id, ct)).Settlement.Status.ShouldBe("Closed");
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    // The payout Stripe really made, whole: fourteen lines, among them two disputes, two failed
    // debits and the charges of three fixture generations, of which this host is one.
    [Fact]
    public async Task The_recorded_payout_as_a_whole_goes_to_review_with_every_line_stored_and_nothing_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var entries = await h.JournalEntries(ct);
        sandbox.Lines[StripeSandboxProcessorTests.RecordedPayout] = StripeReplay.Recorded(StripeSandboxProcessorTests.PayoutLines, StripeSandboxProcessorTests.PayoutRun);
        var charges = StripeSandboxProcessorTests.RecordedPayoutAnswers();
        foreach (var charge in new[] { "py_synthetic000002", "py_synthetic000005" })
        { sandbox.HoldsCharge(JsonNode.Parse(charges(new("GET", "/v1/charges/" + charge, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>())).Body)!.AsObject()); }

        var delivered = await h.Deliver(StripeSandboxProcessorTests.PayoutEvent("payout.paid"), ct);

        delivered.Status.ShouldBe(HttpStatusCode.NoContent, delivered.Body);
        var (payout, items) = await Stored(h, StripeSandboxProcessorTests.RecordedPayout, ct);
        (payout.Status, payout.Reason, payout.ProcessorAmount, payout.Banked).ShouldBe(("NeedsReview", "unsupported_settlement", 1470.60m, false));
        // Thirteen lines beside the payout's own, two of them a return and its fee apart: none skipped, none filtered.
        items.Count.ShouldBe(15);
        items.Sum(x => x.Net).ShouldBe(1470.60m);
        items.GroupBy(x => x.Kind).ToDictionary(x => x.Key, x => x.Count()).ShouldBe(
            new Dictionary<string, int> { ["Payment"] = 9, ["Return"] = 2, ["Fee"] = 2, ["FailedDebit"] = 2 }, ignoreOrder: true);
        items.ShouldAllBe(x => x.EntryId == null && x.FeeEntryId == null);
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // What the bank received, to the cent, releases nothing.
        (await Bank(h, StripeSandboxProcessorTests.RecordedPayout, 1470.60m, ct)).ShouldBe(0);
        var banked = (await Stored(h, StripeSandboxProcessorTests.RecordedPayout, ct)).Payout;
        (banked.Status, banked.Reason).ShouldBe(("NeedsReview", "unsupported_settlement"));
        (await Post(h, payout.Id, ct)).Refusal.ShouldBe("unsupported_settlement");
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    // The connected account is shared with the probe and with older fixture generations. A line for
    // one of their charges is stored as Stripe gave it and names no payment LeaseBook knows, so the
    // payout cannot be identified whole. Left out, it would post and no longer tie to the bank.
    [Fact]
    public async Task A_payout_with_a_line_for_another_generations_charge_is_stored_whole_and_goes_to_review_as_incomplete()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var own = await Paid(h, 200m, ct);
        var entries = await h.JournalEntries(ct);
        var theirs = StripeSandboxProcessorTests.RecordedLine("txn_synthetic000006");
        ((string)theirs["source"]!["metadata"]!["leasebook_generation"]!).ShouldNotBe(h.Binding.Generation.ToString("D"));
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 322.40m), PaymentLine(h, own, 1.60m), theirs);

        (await h.Deliver(PaidPayout(Payout, 322.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        var (payout, items) = await Stored(h, Payout, ct);
        (payout.Status, payout.Reason).ShouldBe(("NeedsReview", "settlement_incomplete"));
        items.Select(x => (x.Kind, x.ProviderId, x.Net)).ShouldBe(
            [("Payment", Sandbox.IdFor(own.Id), 198.40m), ("Payment", "pi_synthetic000004", 124.00m)], ignoreOrder: true);
        (await Bank(h, Payout, 322.40m, ct)).ShouldBe(0);
        (await Post(h, payout.Id, ct)).Refusal.ShouldBe("settlement_incomplete");
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await h.Operation(own.Id, ct)).Status.ShouldBe("Processing");

        // Without that line the same payout is held and posts: the review above is the line's doing.
        const string clean = "po_host000002";
        sandbox.Lines[clean] = LinesOf(PayoutLine(clean, 198.40m), PaymentLine(h, own, 1.60m));
        (await h.Deliver(PaidPayout(clean, 198.40m, "evt_clean_payout"), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await Stored(h, clean, ct)).Payout.Status.ShouldBe("AwaitingBank");
        (await Bank(h, clean, 198.40m, ct)).ShouldBe(0);
        (await Stored(h, clean, ct)).Payout.Status.ShouldBe("Posted");
    }

    [Fact]
    public async Task A_manual_payout_is_stored_for_review_without_lines_and_is_never_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await Paid(h, 200m, ct);
        var entries = await h.JournalEntries(ct);
        var manual = PaidPayout(Payout, 200.00m);
        manual["data"]!["object"]!["automatic"] = false;

        (await h.Deliver(manual, ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        var (payout, items) = await Stored(h, Payout, ct);
        (payout.Status, payout.Reason, payout.PayoutType, payout.ProcessorAmount).ShouldBe(("NeedsReview", "unsupported_settlement", "manual", 200.00m));
        items.ShouldBeEmpty();
        // Stripe was not asked what it covers, and nobody here worked it out.
        sandbox.LineRequests.ShouldBeEmpty();
        (await Bank(h, Payout, 200.00m, ct)).ShouldBe(0);
        (await Post(h, payout.Id, ct)).Refusal.ShouldBe("unsupported_settlement");
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing");
    }

    // Stripe can mark a payout paid and later failed. Whenever the failure arrives it is stored and
    // sends the payout to a person. Nothing is reversed by it, and nothing can post afterwards.
    [Theory]
    [InlineData("before anything else is known of it")]
    [InlineData("while it waits for the bank")]
    [InlineData("after it has posted")]
    [InlineData("after its review was closed")]
    public async Task A_failed_payout_is_stored_and_goes_to_review_whenever_it_fails_and_nothing_is_reversed(string when)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        var paid = PaidPayout(Payout, 198.40m);
        if (when != "before anything else is known of it") { (await h.Deliver(paid, ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        if (when == "after it has posted") { (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0); }
        if (when == "after its review was closed") { await Close(h, (await Stored(h, Payout, ct)).Payout.Id, ct); }
        var entries = await h.JournalEntries(ct);
        var status = (await h.Operation(payment.Id, ct)).Status;
        status.ShouldBe(when == "after it has posted" ? "Settled" : "Processing");
        var failed = StripeSandboxProcessorTests.FailedPayout(paid);
        var asked = sandbox.LineRequests.Count;

        var delivered = await h.Deliver(failed, ct);

        delivered.Status.ShouldBe(HttpStatusCode.NoContent, delivered.Body);
        sandbox.LineRequests.Count.ShouldBe(asked);
        var (payout, items) = await Stored(h, Payout, ct);
        (payout.Status, payout.Reason, payout.ProcessorAmount).ShouldBe(("NeedsReview", "payout_failed", 198.40m));
        payout.FailedAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds((long)failed["created"]!).UtcDateTime);
        items.Count.ShouldBe(when == "before anything else is known of it" ? 0 : 1);
        // Nothing reversed: what was posted is still posted, and the payment is where it was.
        (payout.PostedAt.HasValue, items.All(x => x.EntryId != null)).ShouldBe((when == "after it has posted", when is "after it has posted" or "before anything else is known of it"));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe(status);
        var view = await View(h, payout.Id, ct);
        (view.CanPost, view.CanClose, view.FailedAt, view.ProcessorAmount).ShouldBe((false, true, payout.FailedAt, 198.40m));

        // Nothing that comes afterwards posts: the paid event again or for the first time, a bank's
        // evidence, an administrator. The failure again does not reopen what a person closes.
        (await h.Deliver(paid, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await Bank(h, Payout, 198.40m, ct);
        await Should.ThrowAsync<PaymentConflictException>(() => Post(h, payout.Id, ct));
        var after = (await Stored(h, Payout, ct)).Payout;
        (after.Status, after.Reason, after.FailedAt).ShouldBe(("NeedsReview", "payout_failed", payout.FailedAt));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Close(h, payout.Id, ct)).Settlement.Status.ShouldBe("Closed");
        (await h.Deliver(failed, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await h.Deliver(StripeSandboxProcessorTests.FailedPayout(paid, "evt_built_payout_failed_again"), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await Stored(h, Payout, ct)).Payout.Status.ShouldBe("Closed");
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe(status);
    }

    // Stripe promises no order and delivers more than once; the sweep finds what a delivery also
    // brought. However the same payout arrives, it is one payout, held once.
    [Theory]
    [InlineData("delivered twice")]
    [InlineData("delivered, then swept")]
    [InlineData("swept, then delivered")]
    [InlineData("swept twice")]
    [InlineData("two events that say the same")]
    [InlineData("its other events first")]
    public async Task A_payout_that_arrives_more_than_once_or_out_of_order_is_one_payout_held_once(string how)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing", Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        await Sweep(h, ct);
        var entries = await h.JournalEntries(ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        var paid = PaidPayout(Payout, 198.40m);
        // The payout's other three events as recorded, about this payout: listed newest first, as Stripe lists.
        JsonObject Other(string type)
        {
            var other = StripeSandboxProcessorTests.PayoutEvent(type);
            (other["data"]!["object"]!["id"], other["data"]!["object"]!["amount"]) = (Payout, 19840);
            return other;
        }
        sandbox.Events = _ => StripeSandboxProcessorTests.Page(false, Other("payout.updated"), paid, Other("payout.reconciliation_completed"), Other("payout.created"));
        async Task Swept() { h.Clock.Advance(SweepInterval); await Sweep(h, ct); }
        async Task Delivered(JsonObject @event) => (await h.Deliver(@event, ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        switch (how)
        {
            case "delivered twice": await Delivered(paid); await Delivered(paid); break;
            case "delivered, then swept": await Delivered(paid); await Swept(); break;
            case "swept, then delivered": await Swept(); await Delivered(paid); break;
            case "swept twice": await Swept(); await Swept(); break;
            case "two events that say the same":
                await Delivered(paid);
                var again = (JsonObject)paid.DeepClone();
                (again["id"], again["created"]) = ("evt_same_payout_again", (long)paid["created"]! + 60);
                await Delivered(again);
                break;
            default:
                foreach (var type in new[] { "payout.updated", "payout.reconciliation_completed", "payout.created" }) { await Delivered(Other(type)); }
                (await Payouts(h, ct)).ShouldBeEmpty();
                await Delivered(paid);
                break;
        }

        var payout = (await Payouts(h, ct)).ShouldHaveSingleItem();
        (payout.PayoutId, payout.Status, payout.Reason, payout.ProcessorAmount).ShouldBe((Payout, "AwaitingBank", null, 198.40m));
        (await Stored(h, Payout, ct)).Items.ShouldHaveSingleItem();
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // One effect: the bank's evidence posts it once, and whatever arrives after that posts nothing.
        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);
        (await h.JournalEntries(ct)).ShouldBe(entries + 2);
        await Delivered(paid);
        await Swept();
        (await Payouts(h, ct)).ShouldHaveSingleItem().Status.ShouldBe("Posted");
        (await h.JournalEntries(ct)).ShouldBe(entries + 2);
        StripeSandboxProcessorTests.ShouldCarryOnlyPayoutIds(sandbox.LineRequests, Account, Payout);
    }

    // The same payout said to be something else is not stored over what is held, and is no longer anyone's to post.
    [Theory]
    [InlineData("another amount")]
    [InlineData("another line")]
    public async Task The_same_payout_reported_with_different_content_is_conflicting_and_posts_nothing(string difference)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        var entries = await h.JournalEntries(ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        if (difference == "another line") { sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.50m)); }
        (await h.Deliver(PaidPayout(Payout, difference == "another amount" ? 198.50m : 198.40m, "evt_other_content"), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        var (payout, items) = await Stored(h, Payout, ct);
        (payout.Status, payout.Reason, payout.ProcessorAmount).ShouldBe(("NeedsReview", "conflicting_evidence", 198.40m));
        items.ShouldHaveSingleItem().Fee.ShouldBe(1.60m);
        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);
        await Should.ThrowAsync<PaymentConflictException>(() => Post(h, payout.Id, ct));
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    // Stripe could not be asked for the lines, so the delivery is not acknowledged and nothing is
    // kept: half a payout is not a payout. The sweep finds the event and keeps it when Stripe answers.
    [Fact]
    public async Task A_paid_payout_whose_lines_cannot_be_asked_for_is_not_acknowledged_and_the_sweep_keeps_it_later()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing", Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await Paid(h, 200m, ct);
        await Sweep(h, ct);
        var entries = await h.JournalEntries(ct);
        sandbox.Lines[Payout] = Fault;
        var paid = PaidPayout(Payout, 198.40m);

        var delivered = await h.Deliver(paid, ct);

        delivered.Status.ShouldBe(HttpStatusCode.InternalServerError, delivered.Body);
        delivered.Commands.ShouldBe(0);
        (await Payouts(h, ct)).ShouldBeEmpty();

        // Still refused, the sweep passes over it; the window is asked for again, and then it is kept.
        sandbox.Events = _ => StripeSandboxProcessorTests.Page(false, paid);
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        (await Payouts(h, ct)).ShouldBeEmpty();
        var from = sandbox.Sweeps[^1].Query["created[gte]"];
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);

        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(from);
        (await Payouts(h, ct)).ShouldHaveSingleItem().Status.ShouldBe("AwaitingBank");
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    [Theory]
    [InlineData("marked live")]
    [InlineData("for an account no fixture is bound to")]
    public async Task A_payout_event_that_is_live_or_for_an_unknown_account_stores_nothing_and_reads_no_organization(string difference)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        var changed = PaidPayout(Payout, 198.40m);
        if (difference == "marked live") { changed["livemode"] = true; } else { changed["account"] = "acct_synthetic000009"; }

        var delivered = await h.Deliver(changed, ct);

        // A live event is refused; one for nobody here is acknowledged. Neither opens an organization or asks Stripe anything.
        delivered.Status.ShouldBe(difference == "marked live" ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent, delivered.Body);
        delivered.Commands.ShouldBe(0);
        sandbox.LineRequests.ShouldBeEmpty();
        (await Payouts(h, ct)).ShouldBeEmpty();

        // As Stripe sent it, it is kept: the difference is what dropped it.
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Commands.ShouldBeGreaterThan(0);
        (await Payouts(h, ct)).ShouldHaveSingleItem();
    }

    // The fixture key signs it, as it signs what the simulator's drivers deliver. Without that
    // signature, over exactly that body and recently, nothing is taken and no organization is read.
    [Theory]
    [InlineData("unsigned")]
    [InlineData("no signature at all")]
    [InlineData("a signature that is not one")]
    [InlineData("one character of the signature changed")]
    [InlineData("a good signature over another amount")]
    [InlineData("signed with another key")]
    [InlineData("signed the way the simulator signs a notice")]
    [InlineData("signed six minutes ago")]
    [InlineData("a member nobody named")]
    public async Task Bank_evidence_that_is_unsigned_or_wrongly_signed_is_refused_and_changes_nothing(string difference)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        var entries = await h.JournalEntries(ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        var signer = h.App.Services.GetRequiredService<BankEvidenceSigner>();
        var runner = h.App.Services.GetRequiredService<PaymentRunner>();
        var good = JsonSerializer.SerializeToUtf8Bytes(new BankEvidenceNotice(Account, Payout, 198.40m, Today(h)));
        var body = good;
        string? signature = signer.Sign(good);
        switch (difference)
        {
            case "unsigned": signature = ""; break;
            case "no signature at all": signature = null; break;
            case "a signature that is not one": signature = "bank evidence"; break;
            case "one character of the signature changed": signature = signature[..^1] + (signature[^1] == '0' ? '1' : '0'); break;
            case "a good signature over another amount": body = JsonSerializer.SerializeToUtf8Bytes(new BankEvidenceNotice(Account, Payout, 1984.00m, Today(h))); break;
            case "signed with another key":
                signature = new BankEvidenceSigner(new SimulationSettings { Mode = PaymentModes.StripeSandbox, SigningKey = new string('y', 64) }, h.Clock).Sign(good);
                break;
            case "signed the way the simulator signs a notice":
                var at = h.Clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                signature = at + "." + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(new string('x', 64)), (byte[])[.. Encoding.UTF8.GetBytes(at + "."), .. good]));
                break;
            case "signed six minutes ago": h.Clock.Advance(TimeSpan.FromMinutes(6)); break;
            case "a member nobody named":
                body = Encoding.UTF8.GetBytes($$"""{"Account":"{{Account}}","PayoutId":"{{Payout}}","BankAmount":198.40,"BankDate":"{{Today(h):yyyy-MM-dd}}","Mode":"Simulation"}""");
                signature = signer.Sign(body);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }
        using var counted = h.Commands.Count();

        var delivery = await runner.ReceiveBankEvidenceAsync(body, signature, ct);

        delivery.ShouldBe(new BankEvidenceDelivery(BankEvidenceDelivery.Refused));
        counted.Seen.ShouldBe(0);
        var payout = (await Stored(h, Payout, ct)).Payout;
        (payout.Status, payout.Banked, payout.BankAmount).ShouldBe(("AwaitingBank", false, 0m));
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // Signed with the fixture key, now, the same evidence is taken: the refusal was the signature's doing.
        var taken = await runner.ReceiveBankEvidenceAsync(good, signer.Sign(good), ct);
        (taken.Outcome, taken.Id, taken.Status, taken.Reason).ShouldBe((BankEvidenceOutcomes.Stored, payout.Id, "Posted", null));
        (await h.JournalEntries(ct)).ShouldBe(entries + 2);
    }

    // Evidence names a payout a fixture here holds. For any other it changes nothing, whichever
    // organization is named: a payout is held for the organization whose account it is on, and
    // another fixture organization neither sees it nor can speak for its bank.
    [Fact]
    public async Task Bank_evidence_for_an_unknown_payout_or_another_organizations_is_refused_and_payouts_are_bound_to_their_organization()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct, secondAccount: "acct_synthetic000007");
        var other = h.Second.ShouldNotBeNull();
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        var entries = await h.JournalEntries(ct);
        var theirs = await other.JournalEntries(ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        // No such payout; and this payout, said to be the other organization's.
        (await Bank(h, "po_host000009", 198.40m, ct)).ShouldBe(1);
        (await Bank(other, Payout, 198.40m, ct)).ShouldBe(1);
        // An organization that is no fixture here is not entered at all.
        await Should.ThrowAsync<PaymentUnavailableException>(() => Cli(h.App.Services, ct, "bank", Guid.NewGuid().ToString(),
            Payout, "198.40", Today(h).ToString("yyyy-MM-dd")));
        var unknown = await h.App.Services.GetRequiredService<PaymentRunner>().ReceiveBankEvidenceAsync(
            Signed(h, new BankEvidenceNotice("acct_synthetic000009", Payout, 198.40m, Today(h)), out var signature), signature, ct);
        unknown.Outcome.ShouldBe(BankEvidenceDelivery.Refused);

        var payout = (await Stored(h, Payout, ct)).Payout;
        (payout.Status, payout.Banked).ShouldBe(("AwaitingBank", false));
        (await h.JournalEntries(ct), await other.JournalEntries(ct)).ShouldBe((entries, theirs));
        // The other organization holds no payout, sees none, and cannot post or close this one.
        (await Payouts(other, ct)).ShouldBeEmpty();
        await other.InOrg(async sp =>
        {
            var sender = sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>();
            (await sender.Query(new GetSettlements(), ct)).ShouldBeEmpty();
            (await sender.Query(new GetSettlements(payout.Id), ct)).ShouldBeEmpty();
            (await sender.Send(new PostSettlement(other.Binding, payout.Id, UuidV7.NewId()), ct)).ShouldBeNull();
            (await sender.Send(new CloseSettlementReview(other.Binding, payout.Id, UuidV7.NewId(), "Not ours."), ct)).ShouldBeNull();
            (await sp.GetRequiredService<AppDbContext>().Set<PaymentSettlementItem>().CountAsync(ct)).ShouldBe(0);
        }, ct);
        // Under this organization's binding in the other's transaction, the engine refuses outright.
        await Should.ThrowAsync<PaymentUnavailableException>(() => other.InOrg(sp => sp.GetRequiredService<SettlementEngine>()
            .ReceiveBankEvidenceAsync(h.Binding, new BankEvidence(Payout, Account, PaymentModes.StripeSandbox, h.Binding.Generation, 198.40m, Today(h), "bank-x"), ct), ct));

        // The same payout id on the other organization's account is that organization's own payout.
        var same = PaidPayout(Payout, 50.00m, "evt_other_org_payout");
        same["account"] = other.Binding.Account;
        var manual = same["data"]!["object"]!.AsObject();
        manual["automatic"] = false;
        (await h.Deliver(same, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await Payouts(other, ct)).ShouldHaveSingleItem().ProcessorAmount.ShouldBe(50.00m);
        (await Payouts(h, ct)).ShouldHaveSingleItem().ProcessorAmount.ShouldBe(198.40m);

        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);
        (await Stored(h, Payout, ct)).Payout.Status.ShouldBe("Posted");
        (await other.JournalEntries(ct)).ShouldBe(theirs);
    }

    [Theory]
    [InlineData("bank", "not-an-org", "po_1", "10.00", "2026-10-09")]
    [InlineData("bank", "00000000-0000-0000-0000-000000000001", "po_1:2", "10.00", "2026-10-09")]
    [InlineData("bank", "00000000-0000-0000-0000-000000000001", "po_1?x=1", "10.00", "2026-10-09")]
    [InlineData("bank", "00000000-0000-0000-0000-000000000001", "po_1", "10.005", "2026-10-09")]
    [InlineData("bank", "00000000-0000-0000-0000-000000000001", "po_1", "ten", "2026-10-09")]
    [InlineData("bank", "00000000-0000-0000-0000-000000000001", "po_1", "10.00", "10/09/2026")]
    [InlineData("bank", "00000000-0000-0000-0000-000000000001", "po_1", "10.00")]
    [InlineData("bank", "00000000-0000-0000-0000-000000000001", "po_1", "10.00", "2026-10-09", "--force")]
    public void The_bank_command_takes_an_organization_a_payout_id_a_whole_cent_amount_and_a_date_and_nothing_else(params string[] args)
    {
        new PaymentSimulationVerb().TryCreateInvocation(["payment-simulation", .. args], out _, out var error).ShouldBeFalse();
        error.ShouldContain("bank <org-id> <stripe-payout-id> <bank-amount> <bank-date yyyy-MM-dd>");
    }

    private static DateOnly Today(Harness h) => DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime);

    // A payment a tenant made, accepted by Stripe and reported succeeded: collected, and waiting for the bank.
    private static async Task<PaymentView> Paid(Harness h, decimal amount, CancellationToken ct)
    {
        var payment = await h.Submit(amount, ct);
        await h.Run(ct);
        var succeeded = Event(AchSucceeded, h, payment);
        // The recorded event under an id of its own, so that two payments are not one event said twice.
        succeeded["id"] = "evt_ok" + payment.Id.ToString("N");
        (await h.Deliver(succeeded, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.QuotedFee).ShouldBe(("Processing", null, 0m));
        operation.PaidAt.ShouldNotBeNull();
        return payment;
    }

    // Management fees held in the trust bank from the day before, which is what a fee shortfall is borne by.
    private static Task HoldFees(Harness h, decimal amount, CancellationToken ct) => h.InOrg(sp =>
        sp.GetRequiredService<IAccountingEvents>().PostAsync(new InterestEarned(new Money(amount),
            Today(h).AddDays(-1), h.Binding.BankId, "Fees held", "fees-held-" + UuidV7.NewId().ToString("N")), ct), ct);

    // The fixture command, as an operator runs it: bank evidence for a payout, dated today unless a test says otherwise.
    private static Task<int> Bank(Harness h, string payout, decimal amount, CancellationToken ct, DateOnly? date = null) =>
        Cli(h.App.Services, ct, "bank", h.Binding.OrgId.ToString(), payout, amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            (date ?? Today(h)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    private static byte[] Signed(Harness h, BankEvidenceNotice notice, out string signature)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(notice);
        signature = h.App.Services.GetRequiredService<BankEvidenceSigner>().Sign(body);
        return body;
    }

    private static async Task<IReadOnlyList<PaymentSettlement>> Payouts(Harness h, CancellationToken ct)
    {
        IReadOnlyList<PaymentSettlement> payouts = [];
        await h.InOrg(async sp => payouts = await sp.GetRequiredService<AppDbContext>().Set<PaymentSettlement>().AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct), ct);
        return payouts;
    }

    private static async Task<(PaymentSettlement Payout, IReadOnlyList<PaymentSettlementItem> Items)> Stored(Harness h, string payoutId, CancellationToken ct)
    {
        var payout = (await Payouts(h, ct)).Single(x => x.PayoutId == payoutId);
        IReadOnlyList<PaymentSettlementItem> items = [];
        await h.InOrg(async sp => items = await sp.GetRequiredService<AppDbContext>().Set<PaymentSettlementItem>().AsNoTracking()
            .Where(x => x.SettlementId == payout.Id).OrderBy(x => x.Item).ToListAsync(ct), ct);
        return (payout, items);
    }

    private static async Task<SettlementView> View(Harness h, Guid id, CancellationToken ct)
    {
        SettlementView? view = null;
        await h.InOrg(async sp => view = (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>().Query(new GetSettlements(id), ct)).Single(), ct);
        return view!;
    }

    // What the administrator's two controls send, on an organization transaction that commits as a request's does.
    private static async Task<SettlementReviewResult> Post(Harness h, Guid id, CancellationToken ct, Guid? administrator = null)
    {
        SettlementReviewResult? result = null;
        await h.InOrg(async sp => result = await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>()
            .Send(new PostSettlement(h.Binding, id, administrator ?? UuidV7.NewId()), ct), ct);
        return result.ShouldNotBeNull();
    }

    private static async Task<SettlementReviewResult> Close(Harness h, Guid id, CancellationToken ct)
    {
        SettlementReviewResult? result = null;
        await h.InOrg(async sp => result = await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>()
            .Send(new CloseSettlementReview(h.Binding, id, UuidV7.NewId(), "Corrected by hand."), ct), ct);
        return result.ShouldNotBeNull();
    }

    // The recorded `payout.paid` event about another payout: its id, its amount and the event's own id.
    private static JsonObject PaidPayout(string payoutId, decimal amount, string eventId = "evt_host_payout_paid")
    {
        var paid = StripeSandboxProcessorTests.PayoutEvent("payout.paid");
        ((string)paid["account"]!).ShouldBe(Account);
        paid["id"] = eventId;
        var payout = paid["data"]!["object"]!.AsObject();
        (payout["id"], payout["amount"]) = (payoutId, (long)(amount * 100m));
        return paid;
    }

    private static (int Status, string Body) LinesOf(params JsonObject[] lines) => StripeSandboxProcessorTests.LineList(false, lines);

    // The recorded payout's own line, for another payout: what left Stripe for the bank.
    private static JsonObject PayoutLine(string payoutId, decimal amount)
    {
        var line = StripeSandboxProcessorTests.RecordedLine("txn_synthetic000005");
        (line["amount"], line["net"]) = (-(long)(amount * 100m), -(long)(amount * 100m));
        var payout = line["source"]!.AsObject();
        (payout["id"], payout["amount"]) = (payoutId, (long)(amount * 100m));
        return line;
    }

    // A recorded ACH payment line for a payment this host made: the charge behind it, what was charged, and the fee Stripe kept.
    private static JsonObject PaymentLine(Harness h, PaymentView payment, decimal fee, string? id = null)
    {
        var line = StripeSandboxProcessorTests.RecordedLine("txn_synthetic000006");
        ((string)line["type"]!).ShouldBe("payment");
        var (charged, kept) = ((long)(payment.ChargedAmount * 100m), (long)(fee * 100m));
        line["id"] = id ?? "txn_pay" + payment.Id.ToString("N");
        (line["amount"], line["fee"], line["net"]) = (charged, kept, charged - kept);
        Point(line["source"]!.AsObject(), h, payment);
        return line;
    }

    // A recorded charge made this host's payment's, as the charge inside a balance line.
    private static void Point(JsonObject charge, Harness h, PaymentView payment)
    {
        ((string)charge["object"]!).ShouldBe("charge");
        charge["id"] = Sandbox.ChargeFor(payment.Id);
        charge["payment_intent"] = Sandbox.IdFor(payment.Id);
        charge["amount"] = (long)(payment.ChargedAmount * 100m);
        charge["metadata"] = new JsonObject
        {
            ["leasebook_operation"] = payment.Id.ToString("D"),
            ["leasebook_generation"] = h.Binding.Generation.ToString("D"),
        };
    }

    // The recorded dispute line about a payment this host made: the whole charge taken back, and Stripe's 15.00 fee with it.
    private static JsonObject DisputeLine(PaymentView payment, string id)
    {
        var line = StripeSandboxProcessorTests.RecordedLine("txn_synthetic000007");
        ((string)line["type"]!, (long)line["fee"]!).ShouldBe(("adjustment", 1500L));
        var charged = (long)(payment.ChargedAmount * 100m);
        line["id"] = id;
        (line["amount"], line["net"]) = (-charged, -charged - 1500);
        var dispute = line["source"]!.AsObject();
        ((string)dispute["object"]!).ShouldBe("dispute");
        (dispute["charge"], dispute["payment_intent"], dispute["amount"]) = (Sandbox.ChargeFor(payment.Id), Sandbox.IdFor(payment.Id), charged);
        return line;
    }
}
