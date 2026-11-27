using System.Text.Json.Nodes;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.Web.Payments;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// The recording at the API version the adapter is pinned to (ADR-054). The rest of the suite replays
// the three earlier runs, so these read this run's payloads through the same adapter: every event, and
// a payout against the same payout as the earlier recording read it.
public sealed partial class StripeSandboxProcessorTests
{
    private const string PinnedRun = StripeReplay.PinnedVersionRun;
    private const string PinnedGeneration = "e419a059-54d8-48f3-8dad-dbb21c485f3d";

    [Fact]
    public async Task Every_delivery_at_the_pinned_version_is_read_as_the_earlier_recordings_were()
    {
        var h = new Adapter(("Payments:Fixtures:0:Generation", PinnedGeneration));
        h.Stripe.Answer = sent => sent.Path switch
        {
            "/v1/payment_intents/pi_synthetic000006" => (200, StripeReplay.RecordedBody("051-ach-dispute-final.json", PinnedRun).ToJsonString()),
            "/v1/charges/py_synthetic000003" => StripeReplay.Recorded("052-ach-dispute-charge.json", PinnedRun),
            "/v1/payment_intents/pi_synthetic000007" => (200, StripeReplay.RecordedBody("053-card-dispute-create.json", PinnedRun).ToJsonString()),
            "/v1/charges/ch_synthetic000004" => StripeReplay.Recorded("059-card-dispute-charge.json", PinnedRun),
            _ => throw new InvalidOperationException("Unexpected request " + sent.Path),
        };

        var deliveries = StripeReplay.Deliveries(PinnedRun);
        deliveries.Count.ShouldBe(61);
        var kinds = new List<string>();
        foreach (var (name, recorded) in deliveries)
        {
            ((string)recorded["api_version"]!).ShouldBe("2026-11-18.endive", name);
            var result = await Read(h, recorded);
            var type = (string)recorded["type"]!;
            var ours = type is "payment_intent.succeeded" or "payment_intent.processing" or "payment_intent.payment_failed"
                or "charge.refunded" or "charge.dispute.created" or "charge.dispute.funds_withdrawn" or "charge.dispute.closed";
            if (!ours)
            {
                result.ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs, name + " " + type);
                continue;
            }
            var read = result.Value.ShouldNotBeNull(name + " " + type);
            (read.EventId, read.Account, read.Mode, read.Generation).ShouldBe(((string)recorded["id"]!, RecordedAccount, PaymentModes.StripeSandbox, h.Binding.Generation), name);
            read.Currency.ShouldBe("USD", name);
            kinds.Add(read.Kind);
        }

        // Three ACH events of a return, two card events of a dispute, a refund in full and one in part.
        kinds.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count()).ShouldBe(new Dictionary<string, int>
        {
            ["Succeeded"] = 7,
            ["Processing"] = 3,
            ["Failed"] = 2,
            ["Return"] = 3,
            ["Dispute"] = 2,
            ["Refund"] = 2,
        }, ignoreOrder: true);
        ShouldCarryOnlyIds(h.Stripe.Requests, h.Binding.Account);
    }

    // The run at the version pinned before this one (2026-10-28.endive). It recorded the same payout of
    // 2026-10-16, so a read of it is the earlier answer the pinned version's read must match.
    private const string PreviousPinnedRun = "2026-10-31T01-15-52-187Z";
    private const string PreviousPinnedGeneration = "07a8a0f1-7d1b-4fca-9e50-43ff46d94ee5";

    // The same payout, created on 2026-10-16, read at the previous pinned version and again at this
    // one. Its ids are synthetic for each run, so what must match is everything else, line by line.
    [Fact]
    public async Task A_payout_read_at_the_pinned_version_comes_to_the_same_lines_as_the_earlier_recording()
    {
        var earlier = new Adapter(("Payments:Fixtures:0:Generation", PreviousPinnedGeneration));
        earlier.Stripe.Answer = sent => sent.Path switch
        {
            Lines => StripeReplay.Recorded("095-payout-lines.json", PreviousPinnedRun),
            "/v1/charges/ch_synthetic000009" => CardCharge("ch_synthetic000009", "pi_synthetic000012", PreviousPinnedRun),
            _ => throw new InvalidOperationException("Unexpected request " + sent.Path),
        };
        var earlierPaid = (JsonObject)StripeReplay.RecordedBody("099-payout-events.json", PreviousPinnedRun)["data"]!.AsArray()
            .Single(x => (string)x!["type"]! == "payout.paid" && (string)x["data"]!["object"]!["id"]! == "po_synthetic000001")!.DeepClone();
        var then = (await ReadPayout(earlier, earlierPaid)).Value.ShouldNotBeNull();

        var h = PinnedOctoberPayout();
        var now = (await ReadPayout(h, PinnedPayoutPaid("po_synthetic000003"))).Value.ShouldNotBeNull();

        (then.PayoutId, then.Amount, then.State).ShouldBe(("po_synthetic000001", 665.16m, "Paid"));
        (now.PayoutId, now.Amount, now.State, now.Generation).ShouldBe(("po_synthetic000003", 665.16m, "Paid", h.Binding.Generation));
        now.Items.Select(x => (x.Kind, x.Gross, x.Fee, x.Net, x.Currency)).ShouldBe(then.Items.Select(x => (x.Kind, x.Gross, x.Fee, x.Net, x.Currency)));
        now.Items.Sum(x => x.Net).ShouldBe(now.Amount);
        ShouldCarryOnlyPayoutIds(h.Stripe.Requests, h.Binding.Account, "po_synthetic000003");
    }

    // An adapter answering for the pinned run's payout of 2026-10-16: its lines, and the one charge a
    // line names that the adapter reads, the card payment the dispute line took back.
    private static Adapter PinnedOctoberPayout()
    {
        var h = new Adapter(("Payments:Fixtures:0:Generation", PinnedGeneration));
        h.Stripe.Answer = sent => sent.Path switch
        {
            Lines => StripeReplay.Recorded("095-payout-lines.json", PinnedRun),
            "/v1/charges/ch_synthetic000014" => CardCharge("ch_synthetic000014", "pi_synthetic000020"),
            _ => throw new InvalidOperationException("Unexpected request " + sent.Path),
        };
        return h;
    }

    // A payout.paid event for a payout the pinned run listed. Stripe lists events for 30 days, so the
    // run could not fetch the events of a payout older than that. The envelope is one of the run's own
    // payout.paid events and the payout is the run's own listing of it; in every recorded run an event's
    // payout and the listing of the same payout agree field for field.
    private static JsonObject PinnedPayoutPaid(string payoutId)
    {
        var paid = (JsonObject)StripeReplay.RecordedBody("096-payout-events.json", PinnedRun)["data"]!.AsArray()
            .First(x => (string)x!["type"]! == "payout.paid")!.DeepClone();
        paid["data"]!["object"] = StripeReplay.RecordedBody("091-payouts.json", PinnedRun)["data"]!.AsArray()
            .Single(x => (string)x!["id"]! == payoutId)!.DeepClone();
        return paid;
    }

    // The first refunds recorded in a payout (2026-10-16), each with its own refund as the source.
    // Stored under a kind nothing posts.
    [Fact]
    public async Task The_refund_lines_stripe_recorded_in_a_payout_are_read_as_refunds_of_the_payments_they_name()
    {
        var h = PinnedOctoberPayout();

        var payout = (await ReadPayout(h, PinnedPayoutPaid("po_synthetic000003"))).Value.ShouldNotBeNull();

        (payout.PayoutId, payout.Amount, payout.State).ShouldBe(("po_synthetic000003", 665.16m, "Paid"));
        payout.Items.Where(x => x.Kind == "Refund").ShouldBe(
        [
            new("txn_synthetic000032", "Refund", "pi_synthetic000018", -10.00m, 0m, -10.00m, "USD"),
            new("txn_synthetic000036", "Refund", "pi_synthetic000022", -50.00m, 0m, -50.00m, "USD"),
            new("txn_synthetic000037", "Refund", "pi_synthetic000023", -200.00m, 0m, -200.00m, "USD"),
        ]);
        // A card dispute stays a dispute, and every other line is a payment. Nothing was left out.
        payout.Items.Single(x => x.Kind == "Dispute").ShouldBe(new("txn_synthetic000034", "Dispute", "pi_synthetic000020", -200.00m, 15.00m, -215.00m, "USD"));
        payout.Items.Count(x => x.Kind == "Payment").ShouldBe(9);
        payout.Items.Count.ShouldBe(13);
        payout.Items.Sum(x => x.Net).ShouldBe(payout.Amount);
    }

    // The card dispute's charge as the run recorded it, under the id and payment a line names.
    private static (int Status, string Body) CardCharge(string id, string payment, string run = PinnedRun)
    {
        var charge = StripeReplay.RecordedBody(run == PinnedRun ? "059-card-dispute-charge.json" : "060-card-dispute-charge.json", run);
        ((string)charge["payment_method_details"]!["type"]!).ShouldBe("card");
        charge["id"] = id;
        charge["payment_intent"] = payment;
        return (200, charge.ToJsonString());
    }
}
