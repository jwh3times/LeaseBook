using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// Disputes, refunds and the other facts that are stored for a person and never posted (ADR-054, #513
// step 6): what the adapter reads them as, and what it asks Stripe to read them. A dispute event
// does not say whose payment it is about, so reading one asks; every answer here is a recording.
public sealed partial class StripeSandboxProcessorTests
{
    private const string AchDisputeEvents = "058-webhook-connect.json,059-webhook-connect.json,060-webhook-connect.json";
    private const string DisputedPayment = "/v1/payment_intents/pi_synthetic000006";
    private const string DisputedCharge = "/v1/charges/py_synthetic000003";
    private static readonly Guid RefundGeneration = Guid.Parse("baf0f6bb-248a-4306-9a16-c61587ac53a1");
    private static readonly Guid CardDisputeGeneration = Guid.Parse("54df5b97-25ed-48a3-bd77-5f525ddff0e5");
    // Every type the adapter reads. The sweep's filter must hold exactly these.
    internal static readonly string[] TypesRead =
    [
        "payment_intent.processing", "payment_intent.succeeded", "payment_intent.payment_failed",
        "charge.refunded", "charge.dispute.created", "charge.dispute.funds_withdrawn", "charge.dispute.closed",
    ];

    [Fact]
    public async Task The_three_recorded_events_of_an_ach_dispute_are_read_as_one_return_that_agrees_with_itself()
    {
        var h = new Adapter();
        h.Stripe.Answer = AchDispute();
        var read = new List<ProcessorObservation>();

        foreach (var file in AchDisputeEvents.Split(','))
        {
            var recorded = StripeReplay.Delivered(file);
            var value = (await Read(h, recorded)).Value.ShouldNotBeNull(file);
            // The payment is named by Stripe's reference for it; the amount is the dispute's; the
            // generation and bank are the binding's. Stripe's word and not a bank's: never complete.
            value.ShouldBe(new ProcessorObservation((string)recorded["id"]!, "pi_synthetic000006", RecordedAccount, PaymentModes.StripeSandbox,
                h.Binding.Generation, "Return", 200.00m, 0m, 200.00m, "USD", h.Binding.BankId, new DateOnly(2026, 10, 8),
                "du_synthetic000001", "", false, DateTimeOffset.FromUnixTimeSeconds((long)recorded["created"]!).UtcDateTime), file);
            read.Add(value);
        }

        // Three events, each with a time of its own, and one return: the date is the dispute's.
        read.Select(x => x.EventId).ShouldBe(["evt_synthetic000038", "evt_synthetic000039", "evt_synthetic000040"]);
        read.Select(x => x.ObservedAt).Distinct().Count().ShouldBe(3);
        read.Select(x => (x.EvidenceId, x.BankDate, x.Gross)).Distinct().ShouldHaveSingleItem();
        // Asked twice for each: whose payment it is, and what kind of charge. By id, and nothing else.
        h.Stripe.Requests.Select(x => x.Path).ShouldBe([DisputedPayment, DisputedCharge, DisputedPayment, DisputedCharge, DisputedPayment, DisputedCharge]);
        ShouldCarryOnlyIds(h.Stripe.Requests, h.Binding.Account);
    }

    // The recorded dispute and its events fall on one day. Made to fall on two, the date kept is the
    // dispute's: the next event of it, whenever that comes, then says the same and does not disagree.
    [Fact]
    public async Task A_disputes_date_is_the_day_the_dispute_was_opened_and_not_the_day_of_the_event()
    {
        var h = new Adapter();
        h.Stripe.Answer = AchDispute();
        var late = StripeReplay.Delivered("060-webhook-connect.json");
        // A second before midnight UTC on the day before the run.
        late["data"]!["object"]!["created"] = 1791417599;

        var value = (await Read(h, late)).Value.ShouldNotBeNull();

        value.BankDate.ShouldBe(new DateOnly(2026, 10, 7));
        value.ObservedAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1791497250).UtcDateTime);
        DateOnly.FromDateTime(value.ObservedAt).ShouldBe(new DateOnly(2026, 10, 8));
    }

    [Fact]
    public async Task The_recorded_card_dispute_is_read_as_a_dispute()
    {
        var h = new Adapter(("Payments:Fixtures:0:Generation", CardDisputeGeneration.ToString()));
        h.Stripe.Answer = CardDispute();

        foreach (var file in new[] { "007-webhook-connect.json", "008-webhook-connect.json" })
        {
            var recorded = StripeReplay.Delivered(file, StripeReplay.CardDisputeRun);
            ((string)recorded["data"]!["object"]!["status"]!).ShouldBe("needs_response");
            (await Read(h, recorded)).Value.ShouldBe(new ProcessorObservation((string)recorded["id"]!, "pi_synthetic000001", RecordedAccount,
                PaymentModes.StripeSandbox, CardDisputeGeneration, "Dispute", 200.00m, 0m, 200.00m, "USD", h.Binding.BankId, new DateOnly(2026, 10, 9),
                "du_synthetic000001", "", false, DateTimeOffset.FromUnixTimeSeconds(1791571526).UtcDateTime), file);
        }

        ShouldCarryOnlyIds(h.Stripe.Requests, h.Binding.Account);
        h.Stripe.Requests.Count.ShouldBe(4);
    }

    // What says ACH or card is the charge Stripe holds under the id the dispute names. Not the
    // dispute's own member, which an ACH dispute leaves null, and not the shape of an id.
    [Theory]
    [InlineData("the recorded charge", "Return")]
    [InlineData("a card charge", "Dispute")]
    [InlineData("a kind of charge nobody has named", "Dispute")]
    [InlineData("a charge that does not say", "Dispute")]
    // Held to what the payment is held to: a charge that does not say it is a test is not read as one.
    [InlineData("a bank account charge that does not say whether it is live", "Dispute")]
    [InlineData("a bank account charge whose livemode is null", "Dispute")]
    [InlineData("the dispute says card and the charge says bank account", "Return")]
    [InlineData("the dispute says bank account and the charge says card", "Dispute")]
    // Return needs the charge to say so, for this payment. Stripe's word that there is a dispute stands either way.
    [InlineData("stripe has no such charge", "Dispute")]
    [InlineData("the charge answered is another", "Dispute")]
    [InlineData("the charge is another payment's", "Dispute")]
    // And a charge that cannot be asked for by name is not asked for.
    [InlineData("the dispute names no charge", "Dispute")]
    [InlineData("the dispute names a charge by something that is not an id", "Dispute")]
    public async Task Whether_a_dispute_is_a_return_is_read_from_the_charge_stripe_holds(string difference, string kind)
    {
        var h = new Adapter();
        var dispute = StripeReplay.Delivered("058-webhook-connect.json");
        var said = dispute["data"]!["object"]!.AsObject();
        ((string)said["charge"]!).ShouldStartWith("py_");
        said["payment_method_details"].ShouldBeNull();
        if (difference.StartsWith("the dispute says card", StringComparison.Ordinal)) { said["payment_method_details"] = new JsonObject { ["type"] = "card" }; }
        if (difference.StartsWith("the dispute says bank", StringComparison.Ordinal)) { said["payment_method_details"] = new JsonObject { ["type"] = "us_bank_account" }; }
        if (difference == "the dispute names no charge") { said["charge"] = null; }
        if (difference.EndsWith("not an id", StringComparison.Ordinal)) { said["charge"] = "py_synthetic000003?expand[]=customer"; }
        h.Stripe.Answer = difference == "stripe has no such charge"
            ? sent => sent.Path == DisputedCharge ? (404, Error("invalid_request_error", "resource_missing")) : AchDispute()(sent)
            : AchDispute(charge: charge =>
            {
                var details = charge["payment_method_details"]!.AsObject();
                switch (difference)
                {
                    case "a card charge" or "the dispute says bank account and the charge says card": details["type"] = "card"; break;
                    case "a kind of charge nobody has named": details["type"] = "sepa_debit"; break;
                    case "a charge that does not say": charge.Remove("payment_method_details"); break;
                    case "a bank account charge that does not say whether it is live": charge.Remove("livemode"); break;
                    case "a bank account charge whose livemode is null": charge["livemode"] = null; break;
                    case "the charge answered is another": charge["id"] = "py_synthetic000001"; break;
                    case "the charge is another payment's": charge["payment_intent"] = "pi_synthetic000004"; break;
                    default: break;
                }
            });

        var value = (await Read(h, dispute)).Value.ShouldNotBeNull();

        value.Kind.ShouldBe(kind);
        (value.ProviderId, value.EvidenceId, value.Complete).ShouldBe(("pi_synthetic000006", "du_synthetic000001", false));
        h.Stripe.Requests.Select(x => x.Path).ShouldBe(difference.StartsWith("the dispute names", StringComparison.Ordinal) ? [DisputedPayment] : [DisputedPayment, DisputedCharge]);
        ShouldCarryOnlyIds(h.Stripe.Requests, h.Binding.Account);
    }

    // The same test a payment's own event gets, on the payment Stripe answers with.
    [Theory]
    [InlineData("another generation's", 1)]
    [InlineData("the probe's", 1)]
    [InlineData("one with no operation", 1)]
    [InlineData("one stripe does not hold", 1)]
    [InlineData("none: the dispute names no payment", 0)]
    public async Task A_dispute_about_a_payment_that_is_not_this_fixtures_is_not_ours(string whose, int asked)
    {
        var h = new Adapter();
        var dispute = StripeReplay.Delivered("058-webhook-connect.json");
        if (asked == 0) { dispute["data"]!["object"]!["payment_intent"] = null; }
        h.Stripe.Answer = whose == "one stripe does not hold"
            ? _ => (404, Error("invalid_request_error", "resource_missing"))
            : AchDispute(payment: payment =>
            {
                var metadata = payment["metadata"]!.AsObject();
                if (whose == "another generation's") { metadata["leasebook_generation"] = Guid.NewGuid().ToString("D"); }
                if (whose == "the probe's") { payment["metadata"] = new JsonObject(); }
                if (whose == "one with no operation") { metadata.Remove("leasebook_operation"); }
            });

        (await Read(h, dispute)).ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs);

        // The payment was asked for, once, and the charge never: there was nothing more to learn.
        h.Stripe.Requests.Select(x => x.Path).ShouldBe(Enumerable.Repeat(DisputedPayment, asked));

        // The dispute as recorded, about the payment as recorded, is read: the difference is what dropped it.
        h.Stripe.Answer = AchDispute();
        (await Read(h, StripeReplay.Delivered("058-webhook-connect.json"))).Value.ShouldNotBeNull();
    }

    // Not an answer about the payment, so not a verdict on the event: thrown, and the delivery is not
    // acknowledged. Whether it was the payment or the charge that could not be asked for.
    [Theory]
    [InlineData(DisputedPayment, 401, "authentication_error")]
    [InlineData(DisputedPayment, 403, "permission_error")]
    [InlineData(DisputedPayment, 429, "invalid_request_error")]
    [InlineData(DisputedPayment, 500, "api_error")]
    [InlineData(DisputedPayment, 0, "no connection")]
    [InlineData(DisputedPayment, 200, "not json")]
    [InlineData(DisputedCharge, 403, "permission_error")]
    [InlineData(DisputedCharge, 500, "api_error")]
    [InlineData(DisputedCharge, 0, "no connection")]
    [InlineData(DisputedCharge, 200, "not json")]
    public async Task A_dispute_whose_payment_or_charge_cannot_be_asked_for_is_thrown_and_not_judged(string failing, int status, string type)
    {
        var h = new Adapter();
        h.Stripe.Answer = sent => sent.Path != failing ? AchDispute()(sent)
            : status == 0 ? throw new HttpRequestException("No route to the sandbox.")
            : status == 200 ? (200, "<html>") : (status, Error(type, null));

        var thrown = await Should.ThrowAsync<Exception>(() => Read(h, StripeReplay.Delivered("058-webhook-connect.json")));

        thrown.ShouldNotBeOfType<PaymentConflictException>();
        thrown.ShouldNotBeOfType<PaymentUnavailableException>();
        // Once, and not again by itself.
        h.Stripe.Requests.Count(x => x.Path == failing).ShouldBe(1);
    }

    // A test event about a live payment: the key is reading a live account. Refused as a live event is.
    [Theory]
    [InlineData(DisputedPayment)]
    [InlineData(DisputedCharge)]
    public async Task A_dispute_whose_payment_or_charge_stripe_marks_live_is_refused(string live)
    {
        var h = new Adapter();
        var marked = AchDispute(payment: x => x["livemode"] = live == DisputedPayment, charge: x => x["livemode"] = live == DisputedCharge);
        h.Stripe.Answer = marked;

        (await Read(h, StripeReplay.Delivered("058-webhook-connect.json"))).ShouldBe(ProcessorRead<ProcessorObservation>.Of(null));

        // In a sweep it refuses the whole list, as anything else live in it does.
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? Page(false, Listed("evt_synthetic000002"), Listed("evt_synthetic000038")) : marked(sent);
        await Should.ThrowAsync<PaymentUnavailableException>(() => h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct));

        // In test mode both are read, so each refusal is the flag's doing.
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? Page(false, Listed("evt_synthetic000002"), Listed("evt_synthetic000038")) : AchDispute()(sent);
        (await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct)).Observations.Count.ShouldBe(2);
    }

    // Each differs from a dispute that is read, 058 with its recorded payment and charge, by one thing.
    [Theory]
    [InlineData("a payment id that is not an id")]
    [InlineData("a payment id longer than is kept")]
    [InlineData("the payment answered is another")]
    [InlineData("the answer is not a payment")]
    [InlineData("the payment does not say whether it is live")]
    [InlineData("no dispute id")]
    [InlineData("no time of the dispute")]
    [InlineData("a time of the dispute before time")]
    [InlineData("no amount")]
    [InlineData("zero amount")]
    [InlineData("amount as text")]
    [InlineData("no currency")]
    [InlineData("a currency that is not three letters")]
    [InlineData("dispute marked live")]
    [InlineData("not a dispute")]
    public async Task A_dispute_about_this_fixtures_payment_that_cannot_be_taken_as_it_stands_is_unreadable(string difference)
    {
        var h = new Adapter();
        var changed = StripeReplay.Delivered("058-webhook-connect.json");
        var dispute = changed["data"]!["object"]!.AsObject();
        Action<JsonObject>? payment = null;
        switch (difference)
        {
            case "a payment id that is not an id": dispute["payment_intent"] = "pi_synthetic000006/../../charges"; break;
            case "a payment id longer than is kept": dispute["payment_intent"] = "pi_" + new string('a', 98); break;
            case "the payment answered is another": payment = x => x["id"] = "pi_synthetic000004"; break;
            case "the answer is not a payment": payment = x => x["object"] = "charge"; break;
            case "the payment does not say whether it is live": payment = x => x.Remove("livemode"); break;
            case "no dispute id": dispute.Remove("id"); break;
            case "no time of the dispute": dispute.Remove("created"); break;
            case "a time of the dispute before time": dispute["created"] = 0; break;
            case "no amount": dispute.Remove("amount"); break;
            case "zero amount": dispute["amount"] = 0; break;
            case "amount as text": dispute["amount"] = "20000"; break;
            case "no currency": dispute.Remove("currency"); break;
            case "a currency that is not three letters": dispute["currency"] = "dollars"; break;
            case "dispute marked live": dispute["livemode"] = true; break;
            case "not a dispute": dispute["object"] = "charge"; break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }
        h.Stripe.Answer = AchDispute(payment: payment);

        (await Read(h, changed)).ShouldBe(ProcessorRead<ProcessorObservation>.Of(null));

        // Nothing but ids ever reached Stripe, and an id that is not one never did.
        ShouldCarryOnlyIds(h.Stripe.Requests, h.Binding.Account);
        h.Stripe.Answer = AchDispute();
        (await Read(h, StripeReplay.Delivered("058-webhook-connect.json"))).Value.ShouldNotBeNull();
    }

    // The refunded charge carries the payment's metadata, so a refund is read without asking.
    [Fact]
    public async Task The_recorded_refunds_are_read_from_the_refunded_charge_with_the_total_refunded_so_far()
    {
        var h = new Adapter(("Payments:Fixtures:0:Generation", RefundGeneration.ToString()));
        (string File, string EventId, string ProviderId, string Charge, decimal Refunded, long Created, bool InFull)[] refunds =
        [
            ("017-webhook-connect.json", "evt_synthetic000008", "pi_synthetic000002", "ch_synthetic000001", 200.00m, 1791571488, true),
            ("021-webhook-connect.json", "evt_synthetic000009", "pi_synthetic000003", "ch_synthetic000003", 50.00m, 1791571489, false),
        ];

        var read = 0;
        foreach (var (name, recorded) in StripeReplay.Deliveries(StripeReplay.RefundRun))
        {
            var result = await Read(h, recorded);
            if (refunds.SingleOrDefault(x => x.File == name) is not { File: not null } expected)
            {
                // A payment's own progress is read as before. Everything else, the refund's own event included, is not ours.
                if (((string)recorded["type"]!) != "payment_intent.succeeded") { result.ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs, name + " " + recorded["type"]); }
                continue;
            }
            read++;
            ((string)recorded["type"]!).ShouldBe("charge.refunded");
            ((bool)recorded["data"]!["object"]!["refunded"]!).ShouldBe(expected.InFull);
            result.Value.ShouldBe(new ProcessorObservation(expected.EventId, expected.ProviderId, RecordedAccount, PaymentModes.StripeSandbox,
                RefundGeneration, "Refund", expected.Refunded, 0m, expected.Refunded, "USD", h.Binding.BankId, new DateOnly(2026, 10, 9),
                expected.Charge, "", false, DateTimeOffset.FromUnixTimeSeconds(expected.Created).UtcDateTime), name);
        }

        read.ShouldBe(2);
        StripeReplay.Deliveries(StripeReplay.RefundRun).Select(x => (string)x.Event["type"]!).ShouldContain("refund.created");
        h.Stripe.Requests.ShouldBeEmpty();
    }

    // They say what charge.refunded says, about a refund that carries nothing of LeaseBook's.
    [Fact]
    public async Task The_other_three_events_of_a_refund_are_not_ours_and_ask_nothing()
    {
        var h = new Adapter(("Payments:Fixtures:0:Generation", RefundGeneration.ToString()));
        var listed = StripeReplay.RecordedBody("014-events-api.json", StripeReplay.CardDisputeRun)["data"]!.AsArray();
        string[] types = ["refund.created", "refund.updated", "charge.refund.updated"];

        foreach (var type in types)
        {
            var events = listed.Where(x => (string)x!["type"]! == type).ToArray();
            events.ShouldNotBeEmpty(type);
            foreach (var one in events) { (await Read(h, one!.DeepClone())).ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs, type); }
        }

        h.Stripe.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("another generation", "NotOurs")]
    [InlineData("no metadata", "NotOurs")]
    [InlineData("no payment named", "Unreadable")]
    [InlineData("nothing refunded", "Unreadable")]
    [InlineData("no amount refunded", "Unreadable")]
    [InlineData("charge marked live", "Unreadable")]
    [InlineData("not a charge", "Unreadable")]
    public async Task A_refund_that_is_not_this_fixtures_or_cannot_be_taken_is_dropped_or_refused(string difference, string expected)
    {
        var h = new Adapter(("Payments:Fixtures:0:Generation", RefundGeneration.ToString()));
        var changed = StripeReplay.Delivered("017-webhook-connect.json", StripeReplay.RefundRun);
        var charge = changed["data"]!["object"]!.AsObject();
        switch (difference)
        {
            case "another generation": charge["metadata"]!["leasebook_generation"] = Guid.NewGuid().ToString("D"); break;
            case "no metadata": charge["metadata"] = new JsonObject(); break;
            case "no payment named": charge["payment_intent"] = null; break;
            case "nothing refunded": charge["amount_refunded"] = 0; break;
            case "no amount refunded": charge.Remove("amount_refunded"); break;
            case "charge marked live": charge["livemode"] = true; break;
            case "not a charge": charge["object"] = "payment_intent"; break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }

        (await Read(h, changed)).ShouldBe(expected == "NotOurs" ? ProcessorRead<ProcessorObservation>.NotOurs : ProcessorRead<ProcessorObservation>.Of(null));

        (await Read(h, StripeReplay.Delivered("017-webhook-connect.json", StripeReplay.RefundRun))).Value.ShouldNotBeNull();
        h.Stripe.Requests.ShouldBeEmpty();
    }

    // Carried from step 5. An authentic event for this fixture's own payment in a currency that is
    // not the one charged disagrees with what LeaseBook holds. Kept as that, with what it said.
    [Theory]
    [InlineData("eur", "EUR")]
    [InlineData("cad", "CAD")]
    [InlineData("GBP", "GBP")]
    public async Task A_payments_event_in_another_currency_is_kept_as_conflicting_evidence_with_what_it_said(string currency, string stored)
    {
        var h = new Adapter();
        var changed = StripeReplay.Delivered("005-webhook-connect.json");
        changed["data"]!["object"]!["currency"] = currency;

        var value = (await Read(h, changed)).Value.ShouldNotBeNull();

        // Not a success: nothing can be marked paid on the strength of it. The amount and currency are the event's own.
        value.ShouldBe(new ProcessorObservation("evt_synthetic000002", "pi_synthetic000001", RecordedAccount, PaymentModes.StripeSandbox,
            h.Binding.Generation, "Conflict", 515.24m, 0m, 515.24m, stored, h.Binding.BankId, new DateOnly(2026, 10, 8), "", "", false,
            DateTimeOffset.FromUnixTimeSeconds(1791496584).UtcDateTime));
        (await Read(h, StripeReplay.Delivered("005-webhook-connect.json"))).Value!.Kind.ShouldBe("Succeeded");
    }

    [Fact]
    public async Task A_dispute_or_a_refund_in_another_currency_is_conflicting_evidence_too()
    {
        var h = new Adapter();
        h.Stripe.Answer = AchDispute();
        var dispute = StripeReplay.Delivered("058-webhook-connect.json");
        dispute["data"]!["object"]!["currency"] = "eur";
        var disputed = (await Read(h, dispute)).Value.ShouldNotBeNull();
        (disputed.Kind, disputed.Currency, disputed.Gross, disputed.ProviderId, disputed.EvidenceId).ShouldBe(("Conflict", "EUR", 200.00m, "pi_synthetic000006", "du_synthetic000001"));

        h = new Adapter(("Payments:Fixtures:0:Generation", RefundGeneration.ToString()));
        var refund = StripeReplay.Delivered("021-webhook-connect.json", StripeReplay.RefundRun);
        refund["data"]!["object"]!["currency"] = "eur";
        var refunded = (await Read(h, refund)).Value.ShouldNotBeNull();
        (refunded.Kind, refunded.Currency, refunded.Gross, refunded.ProviderId).ShouldBe(("Conflict", "EUR", 50.00m, "pi_synthetic000003"));
    }

    // Nothing truthful to keep: no amount that can be stored, or no currency that is one.
    [Theory]
    [InlineData("euros, and no amount")]
    [InlineData("euros, and a zero amount")]
    [InlineData("euros, and an amount as text")]
    [InlineData("euros, and an amount larger than is kept")]
    [InlineData("two letters")]
    [InlineData("four letters")]
    [InlineData("three characters that are not letters")]
    [InlineData("three letters that are not ascii")]
    [InlineData("currency not text")]
    public async Task An_event_with_no_usable_amount_or_no_currency_code_stays_unreadable(string difference)
    {
        var h = new Adapter();
        var changed = StripeReplay.Delivered("005-webhook-connect.json");
        var payment = changed["data"]!["object"]!.AsObject();
        switch (difference)
        {
            case "euros, and no amount": payment["currency"] = "eur"; payment.Remove("amount"); break;
            case "euros, and a zero amount": payment["currency"] = "eur"; payment["amount"] = 0; break;
            case "euros, and an amount as text": payment["currency"] = "eur"; payment["amount"] = "51524"; break;
            case "euros, and an amount larger than is kept": payment["currency"] = "eur"; payment["amount"] = 100_000_000_000_000; break;
            case "two letters": payment["currency"] = "us"; break;
            case "four letters": payment["currency"] = "usdt"; break;
            case "three characters that are not letters": payment["currency"] = "u$d"; break;
            case "three letters that are not ascii": payment["currency"] = "eür"; break;
            case "currency not text": payment["currency"] = 840; break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }

        (await Read(h, changed)).ShouldBe(ProcessorRead<ProcessorObservation>.Of(null));
    }

    // A dispute that cannot be asked about must not cost the sweep what it could read: the outcomes
    // of other payments beside it are returned, and the dispute is counted for the caller to ask again.
    [Theory]
    [InlineData(DisputedPayment, 403)]
    [InlineData(DisputedPayment, 500)]
    [InlineData(DisputedPayment, 0)]
    [InlineData(DisputedCharge, 403)]
    [InlineData(DisputedCharge, 200)]
    public async Task A_sweep_passes_over_a_dispute_it_cannot_ask_about_counts_it_and_reads_the_rest(string failing, int status)
    {
        var h = new Adapter();
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? Page(false, Listed("evt_synthetic000038"), Listed("evt_synthetic000026"), Listed("evt_synthetic000002"))
            : sent.Path != failing ? AchDispute()(sent)
            : status == 0 ? throw new HttpRequestException("No route to the sandbox.")
            : status == 200 ? (200, "<html>") : (status, Error("permission_error", null));

        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        swept.Observations.Select(x => x.EventId).ShouldBe(["evt_synthetic000026", "evt_synthetic000002"]);
        (swept.Listed, swept.Unreadable, swept.Unasked).ShouldBe((3, 0, 1));

        // Asked and answered, it is read and nothing is left over.
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? Page(false, Listed("evt_synthetic000038"), Listed("evt_synthetic000026")) : AchDispute()(sent);
        var again = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);
        (again.Observations.Count, again.Unasked).ShouldBe((2, 0));
    }

    // One dispute, three events, swept together: its payment and its charge are each asked for once.
    [Fact]
    public async Task A_sweep_asks_once_for_a_payment_and_a_charge_that_several_of_its_events_name()
    {
        var h = new Adapter();
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? StripeReplay.Recorded(RecordedEvents) : AchDispute()(sent);

        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        swept.Observations.Count(x => x.Kind == "Return").ShouldBe(3);
        h.Stripe.Requests.Select(x => x.Path).ShouldBe(["/v1/events", DisputedPayment, DisputedCharge]);
        ShouldCarryOnlyIds(h.Stripe.Requests.Skip(1), h.Binding.Account);
    }

    // A list asked of one account that names another is not read for that other, and nothing is asked
    // of the other account on the strength of it.
    [Fact]
    public async Task A_dispute_listed_for_another_account_asks_stripe_nothing()
    {
        var h = new Adapter(("Payments:Fixtures:1:OrgId", Guid.NewGuid().ToString()), ("Payments:Fixtures:1:Generation", RecordedGeneration.ToString()),
            ("Payments:Fixtures:1:BankId", Guid.NewGuid().ToString()), ("Payments:Fixtures:1:Account", "acct_synthetic000009"));
        var elsewhere = Listed("evt_synthetic000038");
        elsewhere["account"] = "acct_synthetic000009";
        h.Stripe.Answer = sent => sent.Path == "/v1/events" ? Page(false, elsewhere) : AchDispute()(sent);

        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        swept.Observations.ShouldBeEmpty();
        h.Stripe.Requests.ShouldHaveSingleItem().Path.ShouldBe("/v1/events");
    }

    /// <summary>The recorded payment and charge of the ACH dispute, each changed as a test asks. Anything else asked for throws.</summary>
    internal static Func<StripeReplay.Sent, (int Status, string Body)> AchDispute(Action<JsonObject>? payment = null, Action<JsonObject>? charge = null) => sent =>
    {
        var (file, change) = sent.Path switch
        {
            DisputedPayment => ("051-ach-dispute-final.json", payment),
            DisputedCharge => ("052-ach-dispute-charge.json", charge),
            _ => throw new InvalidOperationException("Unexpected request " + sent.Path),
        };
        var body = StripeReplay.RecordedBody(file);
        change?.Invoke(body);
        return (200, body.ToJsonString());
    };

    // The card dispute's run recorded its payment as the answer to the request that made it, and its charge as read.
    private static Func<StripeReplay.Sent, (int Status, string Body)> CardDispute() => sent => sent.Path switch
    {
        "/v1/payment_intents/pi_synthetic000001" => (200, StripeReplay.RecordedBody("003-card-dispute-create.json", StripeReplay.CardDisputeRun).ToJsonString()),
        "/v1/charges/ch_synthetic000001" => StripeReplay.Recorded("009-card-dispute-charge.json", StripeReplay.CardDisputeRun),
        _ => throw new InvalidOperationException("Unexpected request " + sent.Path),
    };

    /// <summary>
    /// What reading an event may ask Stripe: one payment or one charge, by its id, on the fixture's
    /// account. No query, no body, nothing to make idempotent, and so nothing about anyone.
    /// </summary>
    internal static void ShouldCarryOnlyIds(IEnumerable<StripeReplay.Sent> requests, string account)
    {
        foreach (var sent in requests)
        {
            sent.Method.ShouldBe("GET");
            Regex.IsMatch(sent.Path, "^/v1/(payment_intents|charges)/[A-Za-z0-9_]+$").ShouldBeTrue(sent.Path);
            sent.Query.ShouldBeEmpty(sent.Path);
            sent.Form.ShouldBeEmpty(sent.Path);
            sent.Headers["Stripe-Account"].ShouldBe(account);
            sent.Headers.ShouldNotContainKey("Idempotency-Key");
        }
    }
}

// Returns, disputes, refunds and conflicting facts in a whole sandbox host: what a delivery or a
// sweep stores, what the unchanged engine makes of it, and that none of it posts.
public sealed partial class StripeSandboxHostTests
{
    private const string DisputeCreated = "058-webhook-connect.json";
    private const string DisputeWithdrawn = "059-webhook-connect.json";
    private const string DisputeClosed = "060-webhook-connect.json";
    private static readonly TimeSpan SweepOverlap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EventsKept = TimeSpan.FromDays(29);

    // Stripe promises no order, and the worker may run between any two. One review either way.
    [Theory]
    [InlineData("058,059,060", false)]
    [InlineData("058,059,060", true)]
    [InlineData("060,059,058", false)]
    [InlineData("060,059,058", true)]
    [InlineData("059", false)]
    [InlineData("060,058", true)]
    public async Task An_ach_dispute_after_success_is_one_return_for_review_and_posts_nothing(string order, bool workerBetween)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        (await h.Deliver(Event(AchSucceeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        sandbox.HoldsCharge(Charge(h, payment, "052-ach-dispute-charge.json"));
        var before = sandbox.Replay.Requests.Count;
        var files = order.Split(',').Select(x => x + "-webhook-connect.json").ToArray();

        foreach (var file in files)
        {
            var delivered = await h.Deliver(Disputed(StripeReplay.Delivered(file), payment), ct);
            delivered.Status.ShouldBe(HttpStatusCode.NoContent, delivered.Body);
            if (workerBetween)
            {
                await h.Run(ct);
                var between = await h.Operation(payment.Id, ct);
                (between.Status, between.Reason).ShouldBe(("NeedsReview", "return_requires_review"));
            }
        }
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId, operation.ProcessedCount).ShouldBe(("NeedsReview", "return_requires_review", null, files.Length + 1));
        operation.PaidAt.ShouldNotBeNull();
        var returns = (await h.Facts(ct)).Where(x => x.Kind == "Return").ToArray();
        returns.Length.ShouldBe(files.Length);
        (await h.Facts(ct)).Count.ShouldBe(files.Length + 1);
        // They agree with each other, as the engine compares returns: one dispute, one date, one amount.
        returns.Select(x => (x.EvidenceId, x.BankDate, x.Gross)).Distinct().ShouldHaveSingleItem()
            .ShouldBe(("du_synthetic000001", new DateOnly(2026, 10, 8), payment.ChargedAmount));
        returns.ShouldAllBe(x => !x.Complete && x.PayoutId == "" && x.Fee == 0m && x.Net == payment.ChargedAmount
            && x.ProviderId == Sandbox.IdFor(payment.Id) && x.Currency == "USD" && x.BankId == h.Binding.BankId);
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Effects(h, ct)).ShouldBe(0);
        // Whatever was asked of Stripe to read them, and by the worker they woke, named a payment or a charge and nothing else.
        var asked = sandbox.Replay.Requests.Skip(before).ToArray();
        asked.Count(x => x.Path == "/v1/charges/" + Sandbox.ChargeFor(payment.Id)).ShouldBe(files.Length);
        StripeSandboxProcessorTests.ShouldCarryOnlyIds(asked, Account);

        await h.InOrg(async sp =>
        {
            var sender = sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>();
            var view = (await sender.Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            // Nothing was receipted, so there is nothing a return could reverse: a person can only close it.
            (view.Status, view.Reason, view.CanPostReturn, view.CanCloseReview, view.CanRetry, view.ReceiptRecorded).ShouldBe(
                ("NeedsReview", "return_requires_review", false, true, false, false));
            await Should.ThrowAsync<PaymentConflictException>(() => sender.Send(new PostPaymentReturn(h.Binding, payment.Id), ct));
        }, ct);
        (await h.JournalEntries(ct)).ShouldBe(entries);

        await h.InOrg(async sp =>
        {
            var closed = await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>()
                .Send(new ClosePaymentReview(h.Binding, payment.Id, UuidV7.NewId(), "Tenant repaid by cheque."), ct);
            closed.ShouldNotBeNull().Payment.Status.ShouldBe("ReviewClosed");
        }, ct);
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    // The decision of 2026-10-09: a Stripe dispute is Stripe's word and not a bank's, so it is never
    // complete, and the guarded return will not post from it. Shown where everything else would let
    // it: the payment has a receipt, as a payout will give it in the next step, and the dispute is
    // for exactly the ledger amount.
    [Fact]
    public async Task A_return_stripe_reports_is_refused_by_the_guarded_return_even_for_a_receipted_payment()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct);
        await h.Run(ct);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var operation = await db.Set<PaymentOperation>().SingleAsync(x => x.Id == payment.Id, ct);
            operation.JournalId = await sp.GetRequiredService<IPaymentLedger>().RecordSettledReceiptAsync(operation.TenantId, operation.BankId,
                operation.Amount, new DateOnly(2026, 10, 5), operation.Method, $"sim-payment:{operation.Id:N}:receipt", ct);
            operation.Status = "Settled";
            await db.SaveChangesAsync(ct);
        }, ct);
        var entries = await h.JournalEntries(ct);
        sandbox.HoldsCharge(Charge(h, payment, "052-ach-dispute-charge.json"));

        foreach (var file in new[] { DisputeCreated, DisputeWithdrawn, DisputeClosed })
        { (await h.Deliver(Disputed(StripeReplay.Delivered(file), payment, payment.Amount), ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        await h.Run(ct);

        var returns = (await h.Facts(ct)).Where(x => x.Kind == "Return").ToArray();
        returns.Length.ShouldBe(3);
        returns.ShouldAllBe(x => x.Gross == payment.Amount && x.Net == payment.Amount && x.Fee == 0m && !x.Complete);
        await h.InOrg(async sp =>
        {
            var sender = sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>();
            var view = (await sender.Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            (view.Status, view.Reason, view.CanPostReturn, view.ReceiptRecorded).ShouldBe(("NeedsReview", "return_requires_review", true, true));

            var result = (await sender.Send(new PostPaymentReturn(h.Binding, payment.Id), ct)).ShouldNotBeNull();

            result.Refusal.ShouldBe("return_partial_unsupported");
            (result.Payment.Status, result.Payment.Reason, result.Payment.ReturnEntryId).ShouldBe(("NeedsReview", "return_partial_unsupported", null));
        }, ct);
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Effects(h, ct)).ShouldBe(0);
    }

    // What the engine already did with an observation that arrives after a review is closed.
    [Fact]
    public async Task A_later_event_of_the_same_dispute_reopens_a_review_that_was_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        sandbox.HoldsCharge(Charge(h, payment, "052-ach-dispute-charge.json"));
        (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        await h.InOrg(sp => sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>()
            .Send(new ClosePaymentReview(h.Binding, payment.Id, UuidV7.NewId(), "Tenant repaid by cheque."), ct), ct);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("ReviewClosed");

        // The same event again changes nothing: it is the fact already held.
        (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("ReviewClosed");

        (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeClosed), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.ReviewNote, operation.JournalId).ShouldBe(("NeedsReview", "return_requires_review", "Tenant repaid by cheque.", null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    [Fact]
    public async Task A_card_dispute_is_kept_as_a_dispute_for_review_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        sandbox.HoldsCharge(Charge(h, payment, "009-card-dispute-charge.json", StripeReplay.CardDisputeRun));

        foreach (var file in new[] { "007-webhook-connect.json", "008-webhook-connect.json" })
        { (await h.Deliver(Disputed(StripeReplay.Delivered(file, StripeReplay.CardDisputeRun), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId, operation.ProcessedCount).ShouldBe(("NeedsReview", "return_requires_review", null, 2));
        var facts = await h.Facts(ct);
        facts.Select(x => x.Kind).ShouldBe(["Dispute", "Dispute"]);
        facts.ShouldAllBe(x => x.EvidenceId == "du_synthetic000001" && x.BankDate == new DateOnly(2026, 10, 9) && x.Gross == payment.ChargedAmount && !x.Complete);
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Effects(h, ct)).ShouldBe(0);
        await h.InOrg(async sp =>
        {
            var view = (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>().Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            (view.CanPostReturn, view.CanCloseReview).ShouldBe((false, true));
        }, ct);
    }

    [Theory]
    [InlineData("017-webhook-connect.json", true)]
    [InlineData("021-webhook-connect.json", false)]
    public async Task A_refund_is_kept_with_the_total_refunded_for_review_and_posts_nothing(string recorded, bool inFull)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        var before = sandbox.Replay.Requests.Count;
        var refund = Refunded(StripeReplay.Delivered(recorded, StripeReplay.RefundRun), h, payment, inFull ? payment.ChargedAmount : 50.00m);

        var delivered = await h.Deliver(refund, ct);

        delivered.Status.ShouldBe(HttpStatusCode.NoContent, delivered.Body);
        // The charge carries the payment's metadata: nothing was asked of Stripe to read it.
        sandbox.Replay.Requests.Count.ShouldBe(before);
        await h.Run(ct);
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId).ShouldBe(("NeedsReview", "return_requires_review", null));
        var fact = (await h.Facts(ct)).ShouldHaveSingleItem();
        (fact.Kind, fact.ProviderId, fact.Gross, fact.Fee, fact.Net, fact.EvidenceId, fact.PayoutId, fact.Complete).ShouldBe(
            ("Refund", Sandbox.IdFor(payment.Id), inFull ? payment.ChargedAmount : 50.00m, 0m, inFull ? payment.ChargedAmount : 50.00m, Sandbox.ChargeFor(payment.Id), "", false));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        (await Effects(h, ct)).ShouldBe(0);
    }

    // The account also holds payments of the probe and of older fixture generations, and they can be
    // disputed too. Authentic, asked about, and not this host's: acknowledged, and no organization entered.
    [Theory]
    [InlineData("another generation's")]
    [InlineData("the probe's")]
    [InlineData("one stripe does not hold")]
    public async Task A_dispute_about_a_payment_that_is_not_this_fixtures_is_acknowledged_and_nothing_is_kept(string whose)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        await h.Run(ct);
        var dispute = StripeReplay.Delivered(DisputeCreated);
        var other = StripeReplay.Intent("pi_synthetic000006", UuidV7.NewId(), UuidV7.NewId(), 20000);
        if (whose == "the probe's") { other["metadata"] = new JsonObject(); }
        if (whose != "one stripe does not hold") { sandbox.Holds(other); }
        var before = sandbox.Replay.Requests.Count;

        var delivered = await h.Deliver(dispute, ct);

        delivered.Status.ShouldBe(HttpStatusCode.NoContent, delivered.Body);
        delivered.Commands.ShouldBe(0);
        (await h.Facts(ct)).ShouldBeEmpty();
        sandbox.Replay.Requests.Skip(before).Select(x => x.Path).ShouldBe(["/v1/payment_intents/pi_synthetic000006"]);
        await h.Run(ct);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing");

        // The same event about this host's payment is kept, so the nothing above is the payment's doing.
        sandbox.HoldsCharge(Charge(h, payment, "052-ach-dispute-charge.json"));
        (await h.Deliver(Disputed(dispute, payment), ct)).Commands.ShouldBeGreaterThan(0);
        (await h.Facts(ct)).ShouldHaveSingleItem();
    }

    // Stripe could not be asked, so the delivery is not acknowledged and nothing is kept: Stripe sends
    // it again, and whether or not it does, the sweep finds the event and keeps it then.
    [Fact]
    public async Task A_dispute_whose_payment_cannot_be_asked_for_is_not_acknowledged_and_the_sweep_keeps_it_later()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        (await h.Deliver(Event(AchSucceeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        sandbox.HoldsCharge(Charge(h, payment, "052-ach-dispute-charge.json"));
        sandbox.Refused = Fault;

        var delivered = await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct);

        // An unexpected failure, with a reference and nothing about why. Not a 2xx, so Stripe does not count it delivered.
        delivered.Status.ShouldBe(HttpStatusCode.InternalServerError, delivered.Body);
        delivered.Commands.ShouldBe(0);
        (await h.Facts(ct)).ShouldHaveSingleItem().Kind.ShouldBe("Succeeded");

        // Still refused, the sweep passes over it and keeps nothing of it. Mended, it keeps the event.
        sandbox.Events = _ => ListedDispute(payment);
        await Sweep(h, ct);
        (await h.Facts(ct)).ShouldHaveSingleItem();
        sandbox.Refused = null;
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        await h.Run(ct);

        (await h.Facts(ct)).Where(x => x.Kind == "Return").Select(x => x.EventId).Order()
            .ShouldBe(["evt_synthetic000038", "evt_synthetic000039", "evt_synthetic000040"]);
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId).ShouldBe(("NeedsReview", "return_requires_review", null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    // Delivery 058 and the list's evt_synthetic000038 are one event, recorded both ways. The sweep
    // asks Stripe what the delivery asked, so it reads the same fact and not one that disagrees.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_dispute_event_delivered_and_also_swept_is_one_fact_and_no_conflict(bool deliveredFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct);
        await h.Run(ct);
        sandbox.HoldsCharge(Charge(h, payment, "052-ach-dispute-charge.json"));
        sandbox.Events = _ => ListedDispute(payment, "evt_synthetic000038");
        ((string)StripeReplay.Delivered(DisputeCreated)["id"]!).ShouldBe("evt_synthetic000038");

        if (deliveredFirst) { (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        await Sweep(h, ct);
        if (!deliveredFirst) { (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        sandbox.Sweeps.Count.ShouldBe(1);
        await h.Run(ct);

        var fact = (await h.Facts(ct)).ShouldHaveSingleItem();
        (fact.EventId, fact.Kind).ShouldBe(("evt_synthetic000038", "Return"));
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.ProcessedCount).ShouldBe(("NeedsReview", "return_requires_review", 1));
    }

    // A dispute or a refund arrives for a payment that stopped waiting long ago. The sweep finds it all the same.
    [Fact]
    public async Task A_dispute_never_delivered_for_a_payment_that_is_no_longer_waiting_is_found_by_the_sweep()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing", Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        (await h.Deliver(Event(AchSucceeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        await Sweep(h, ct);
        var paid = await h.Operation(payment.Id, ct);
        (paid.Status, paid.Reason).ShouldBe(("Processing", null));
        paid.PaidAt.ShouldNotBeNull();
        sandbox.HoldsCharge(Charge(h, payment, "052-ach-dispute-charge.json"));
        sandbox.Events = _ => ListedDispute(payment);

        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId).ShouldBe(("NeedsReview", "return_requires_review", null));
        (await h.Facts(ct)).Count(x => x.Kind == "Return").ShouldBe(3);
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    [Theory]
    [InlineData("the payment")]
    [InlineData("the charge")]
    public async Task A_dispute_whose_payment_or_charge_stripe_marks_live_is_refused_and_nothing_is_kept(string live)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing", Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct);
        await h.Run(ct);
        await Sweep(h, ct);
        var charge = Charge(h, payment, "052-ach-dispute-charge.json");
        var held = sandbox.Held(payment.Id);
        if (live == "the charge") { charge["livemode"] = true; } else { held["livemode"] = true; }
        sandbox.HoldsCharge(charge);
        sandbox.Holds(held);

        var delivered = await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct);

        delivered.Status.ShouldBe(HttpStatusCode.BadRequest, delivered.Body);
        ((string)JsonNode.Parse(delivered.Body)!["code"]!).ShouldBe("invalid_payment_callback");
        delivered.Commands.ShouldBe(0);
        (await h.Facts(ct)).ShouldBeEmpty();

        // Found by a sweep, it refuses the sweep: nothing is kept, and the time asked from does not move on.
        var good = h.Clock.GetUtcNow();
        sandbox.Events = _ => ListedDispute(payment);
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        (await h.Facts(ct)).ShouldBeEmpty();
        sandbox.Events = _ => StripeSandboxProcessorTests.Page(false);
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(good - SweepOverlap));
    }

    // One dispute Stripe will not answer about must not hold up everything else. What the sweep could
    // read is kept and the overdue payment is aged: only a dispute needs asking about, and a dispute
    // follows a success, so no payment still without an outcome is waiting on it. The sweep is not
    // counted as a good one, so the next asks from the same time and finds the dispute again.
    [Fact]
    public async Task A_dispute_the_sweep_cannot_ask_about_stops_nothing_else_and_is_found_by_the_next_sweep()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var failing = await h.Submit(200m, ct, PaymentMethods.Card);
        var disputed = await h.Submit(201m, ct, PaymentMethods.Card);
        var overdue = await h.Submit(202m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        var good = h.Clock.GetUtcNow();
        await Sweep(h, ct);
        sandbox.HoldsCharge(Charge(h, disputed, "052-ach-dispute-charge.json"));
        (int Status, string Body) Both()
        {
            var list = StripeReplay.RecordedBody("064-events-api.json");
            var data = list["data"]!.AsArray();
            foreach (var other in data.Where(x => (string)x!["id"]! is "evt_synthetic000039" or "evt_synthetic000040").ToArray()) { data.Remove(other); }
            Pointed(data.Single(x => (string)x!["id"]! == FailedEvent)!.AsObject(), h, failing);
            Disputed(data.Single(x => (string)x!["id"]! == "evt_synthetic000038")!.AsObject(), disputed);
            return (200, list.ToJsonString());
        }
        sandbox.Events = _ => Both();
        sandbox.ChargeRefused = (403, """{"error":{"type":"permission_error","message":"Refused."}}""");
        h.Clock.Advance(SweepInterval);
        await Created(h, overdue.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);

        await Sweep(h, ct);
        await h.Run(ct);

        // The failure beside it is kept and ends its payment; the dispute left no fact; the old payment went to a person.
        (await h.Facts(ct)).ShouldHaveSingleItem().EventId.ShouldBe(FailedEvent);
        (await h.Operation(failing.Id, ct)).Status.ShouldBe("Failed");
        (await h.Operation(disputed.Id, ct)).Status.ShouldBe("Processing");
        var aged = await h.Operation(overdue.Id, ct);
        (aged.Status, aged.Reason).ShouldBe(("NeedsReview", "outcome_overdue"));

        // Not a good sweep: the next asks from where the last good one left it. The charge answers now.
        sandbox.ChargeRefused = null;
        h.Clock.Advance(SweepInterval);
        var mended = h.Clock.GetUtcNow();
        await Sweep(h, ct);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(good - SweepOverlap));
        await h.Run(ct);
        (await h.Facts(ct)).Select(x => (x.EventId, x.Kind)).ShouldBe([(FailedEvent, "Failed"), ("evt_synthetic000038", "Return")], ignoreOrder: true);
        var operation = await h.Operation(disputed.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId).ShouldBe(("NeedsReview", "return_requires_review", null));

        // That one was good, and the sweep after it asks from it.
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(mended - SweepOverlap));
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    // Accepted, and pinned so that it is not assumed: Stripe has no charge under the id the first
    // time the event is read, and has it the second. The two readings of one event disagree.
    [Fact]
    public async Task One_dispute_event_read_with_two_different_answers_about_its_charge_is_kept_as_conflicting_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);

        (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        (await h.Facts(ct)).ShouldHaveSingleItem().Kind.ShouldBe("Dispute");
        (await h.Operation(payment.Id, ct)).Reason.ShouldBe("return_requires_review");

        sandbox.HoldsCharge(Charge(h, payment, "052-ach-dispute-charge.json"));
        (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);

        var facts = await h.Facts(ct);
        facts.Select(x => x.Kind).ShouldBe(["Dispute", "Conflict"]);
        facts[0].EventId.ShouldBe("evt_synthetic000038");
        facts[1].EventId.ShouldStartWith("conflict:");
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId, operation.ProcessedCount).ShouldBe(("NeedsReview", "conflicting_evidence", null, 2));
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // Read the second way again, it is the conflict already held.
        (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await h.Facts(ct)).Count.ShouldBe(2);
    }

    // Carried from step 5: retained for review, where it used to be refused and lost.
    [Fact]
    public async Task A_success_in_another_currency_is_kept_and_sends_the_payment_to_review_as_conflicting_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(500m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        var euros = Event(Succeeded, h, payment);
        euros["data"]!["object"]!["currency"] = "eur";

        var delivered = await h.Deliver(euros, ct);

        delivered.Status.ShouldBe(HttpStatusCode.NoContent, delivered.Body);
        await h.Run(ct);
        var fact = (await h.Facts(ct)).ShouldHaveSingleItem();
        (fact.EventId, fact.Kind, fact.Currency, fact.Gross, fact.Net, fact.ProviderId).ShouldBe(
            ("evt_synthetic000002", "Conflict", "EUR", payment.ChargedAmount, payment.ChargedAmount, Sandbox.IdFor(payment.Id)));
        var operation = await h.Operation(payment.Id, ct);
        // Not paid: the event that said so said it of other money.
        (operation.Status, operation.Reason, operation.PaidAt, operation.JournalId).ShouldBe(("NeedsReview", "conflicting_evidence", null, null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        await h.InOrg(async sp =>
        {
            var view = (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>().Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            (view.CanCloseReview, view.CanRetry, view.CanPostReturn).ShouldBe((true, false, false));
        }, ct);
    }

    // Carried from step 5: an event's amount is not compared with the operation's by the engine, and
    // need not be. No fact is judged before Stripe's own payment has been held to the operation: an
    // event only wakes the payment, and the worker's lookup retrieves it and compares.
    [Theory]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("generation")]
    public async Task An_event_wakes_the_payment_and_stripes_own_payment_differing_from_the_operation_sends_it_to_review(string difference)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(500m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing");
        var held = sandbox.Held(payment.Id);
        switch (difference)
        {
            case "amount": held["amount"] = (long)(payment.ChargedAmount * 100m) + 1; break;
            case "currency": held["currency"] = "eur"; break;
            default: held["metadata"]!["leasebook_generation"] = UuidV7.NewId().ToString("D"); break;
        }
        sandbox.Holds(held);
        var before = sandbox.Replay.Requests.Count;

        // The event itself says the operation's own amount: it is Stripe's payment that disagrees.
        (await h.Deliver(Event(Succeeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);

        sandbox.Replay.Requests.Skip(before).Select(x => (x.Method, x.Path)).ShouldBe([("GET", "/v1/payment_intents/" + Sandbox.IdFor(payment.Id))]);
        var operation = await h.Operation(payment.Id, ct);
        // The fact was never judged, so the success it reports marked nothing paid.
        (operation.Status, operation.Reason, operation.PaidAt, operation.JournalId, operation.ProcessedCount).ShouldBe(("NeedsReview", "conflicting_evidence", null, null, 0));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        // Held as it should be, the same event leaves a payment in processing and paid.
        var control = await h.Submit(501m, ct, PaymentMethods.Card);
        await h.Run(ct);
        var succeeded = Event(Succeeded, h, control);
        succeeded["id"] = "evt_control";
        (await h.Deliver(succeeded, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        var other = await h.Operation(control.Id, ct);
        (other.Status, other.Reason).ShouldBe(("Processing", null));
        other.PaidAt.ShouldNotBeNull();
    }

    // Carried from step 5. A payment cannot have both failed and succeeded: whichever came first, and
    // whether or not the worker judged the first alone, a person reads it.
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_payment_reported_both_failed_and_succeeded_goes_to_review_in_either_order(bool failedFirst, bool workerBetween)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        string[] order = failedFirst ? [Failed, Succeeded] : [Succeeded, Failed];

        (await h.Deliver(Event(order[0], h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        if (workerBetween)
        {
            await h.Run(ct);
            (await h.Operation(payment.Id, ct)).Status.ShouldBe(failedFirst ? "Failed" : "Processing");
        }
        (await h.Deliver(Event(order[1], h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId, operation.ProcessedCount).ShouldBe(("NeedsReview", "conflicting_evidence", null, 2));
        operation.PaidAt.ShouldBe(SucceededAt);
        (await h.JournalEntries(ct)).ShouldBe(entries);
        await h.InOrg(async sp =>
        {
            var view = (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>().Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            (view.CanCloseReview, view.CanRetry, view.CanPostReturn).ShouldBe((true, false, false));
        }, ct);
    }

    // Disputes and refunds arrive for payments that are not waiting, so the sweep no longer waits
    // for one: every interval, from a little before its last sweep that was kept whole.
    [Fact]
    public async Task The_first_sweep_asks_for_all_stripe_keeps_and_each_one_after_from_the_last_good_sweep_less_an_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var first = h.Clock.GetUtcNow();

        // No payment at all, and Stripe is asked: nothing in this process has asked before.
        await Sweep(h, ct);

        var asked = sandbox.Sweeps.ShouldHaveSingleItem();
        asked.Headers["Stripe-Account"].ShouldBe(Account);
        asked.Query["created[gte]"].ShouldBe(AskedFrom(first - EventsKept));
        asked.Query.Where(x => x.Key.StartsWith("types[", StringComparison.Ordinal)).Select(x => x.Value)
            .ShouldBe(StripeSandboxProcessorTests.TypesRead, ignoreOrder: true);

        // Not again inside the interval, nor a second short of it.
        await Sweep(h, ct);
        h.Clock.Advance(SweepInterval - TimeSpan.FromSeconds(1));
        await Sweep(h, ct);
        sandbox.Sweeps.Count.ShouldBe(1);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = h.Clock.GetUtcNow();
        await Sweep(h, ct);
        sandbox.Sweeps.Count.ShouldBe(2);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(first - SweepOverlap));

        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(second - SweepOverlap));
    }

    [Theory]
    [InlineData("stripe refuses the list")]
    [InlineData("what was found cannot be kept")]
    public async Task A_sweep_that_fails_does_not_move_the_time_the_next_one_asks_from(string failure)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Declined = true, Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        await h.Run(ct);
        var good = h.Clock.GetUtcNow();
        await Sweep(h, ct);

        if (failure == "stripe refuses the list") { sandbox.Events = _ => Fault; }
        else
        {
            sandbox.Events = _ => Listed(h, (FailedEvent, payment));
            h.Commands.FailingLocks["event:" + FailedEvent] = 0;
        }
        // Twice over, so that the time of a failed sweep is seen not to be taken either.
        for (var again = 0; again < 2; again++)
        {
            h.Clock.Advance(SweepInterval);
            await Sweep(h, ct);
            sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(good - SweepOverlap), $"failed sweep {again + 1}");
        }
        (await h.Facts(ct)).ShouldBeEmpty();

        // Mended, the sweep is kept whole and its own time is what the one after asks from.
        h.Commands.FailingLocks.Clear();
        sandbox.Events = _ => Listed(h, (FailedEvent, payment));
        h.Clock.Advance(SweepInterval);
        var mended = h.Clock.GetUtcNow();
        await Sweep(h, ct);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(good - SweepOverlap));
        (await h.Facts(ct)).ShouldHaveSingleItem().EventId.ShouldBe(FailedEvent);
        // Ended by what was found, the payment waits no longer and pulls nothing back.
        await h.Run(ct);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Failed");
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(mended - SweepOverlap));
        sandbox.Sweeps.Count.ShouldBe(5);
    }

    [Fact]
    public async Task The_oldest_waiting_payment_still_pulls_the_window_back_and_never_past_what_stripe_keeps()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        await Sweep(h, ct);
        var older = await h.Submit(200m, ct, PaymentMethods.Card);
        var newer = await h.Submit(300m, ct, PaymentMethods.Card);
        await h.Run(ct);

        // Older than the last good sweep: a payment waiting since before it may have an event from before it.
        h.Clock.Advance(SweepInterval);
        var now = h.Clock.GetUtcNow();
        await Created(h, older.Id, now.UtcDateTime.AddDays(-3), ct);
        await Sweep(h, ct);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(now.AddDays(-3)));

        // Stripe keeps events for thirty days. Each of these sweeps is refused, which is the only way
        // a payment this old is still waiting: one that Stripe answered would have sent it to a person.
        sandbox.Events = _ => Fault;
        foreach (var days in new[] { 29, 30, 400 })
        {
            h.Clock.Advance(SweepInterval);
            now = h.Clock.GetUtcNow();
            await Created(h, older.Id, now.UtcDateTime.AddDays(-days), ct);
            await Sweep(h, ct);
            sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(now - EventsKept), $"{days} days");
            (await h.Operation(older.Id, ct)).Status.ShouldBe("Processing");
        }

        // Newer than the last good sweep, it pulls nothing: the sweep's own time is the earlier.
        sandbox.Events = _ => StripeSandboxProcessorTests.Page(false);
        h.Clock.Advance(SweepInterval);
        var good = h.Clock.GetUtcNow();
        await Created(h, older.Id, good.UtcDateTime.AddDays(-1), ct);
        await Sweep(h, ct);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(good.AddDays(-1)));
        h.Clock.Advance(SweepInterval);
        now = h.Clock.GetUtcNow();
        await Created(h, older.Id, now.UtcDateTime.AddMinutes(-1), ct);
        await Created(h, newer.Id, now.UtcDateTime, ct);
        await Sweep(h, ct);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe(AskedFrom(good - SweepOverlap));
    }

    // What a sweep's "created[gte]" is for a time asked from: Stripe's seconds, less the adapter's allowance for a clock that is not Stripe's.
    private static string AskedFrom(DateTimeOffset since) => (since.ToUnixTimeSeconds() - 300).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<int> Effects(Harness h, CancellationToken ct)
    {
        var count = 0;
        await h.InOrg(async sp => count = await sp.GetRequiredService<AppDbContext>().Set<PaymentEffect>().CountAsync(ct), ct);
        return count;
    }

    // A recorded charge made this host's payment's: its id, the payment it belongs to, its amount and its metadata.
    private static JsonObject Charge(Harness h, PaymentView payment, string recorded, string run = StripeReplay.Run)
    {
        var charge = StripeReplay.RecordedBody(recorded, run);
        charge["id"] = Sandbox.ChargeFor(payment.Id);
        charge["payment_intent"] = Sandbox.IdFor(payment.Id);
        charge["amount"] = (long)(payment.ChargedAmount * 100m);
        charge["metadata"] = new JsonObject
        {
            ["leasebook_operation"] = payment.Id.ToString("D"),
            ["leasebook_generation"] = h.Binding.Generation.ToString("D"),
        };
        return charge;
    }

    // A recorded dispute event pointed at a payment this host made. Three things are substituted on
    // the dispute inside: the payment and the charge it names, and its amount. It carries nothing of
    // LeaseBook's to substitute; the event's own id, type, account and times are as Stripe sent them.
    private static JsonObject Disputed(JsonObject @event, PaymentView payment, decimal? amount = null)
    {
        ((string)@event["account"]!).ShouldBe(Account);
        var dispute = @event["data"]!["object"]!.AsObject();
        ((string)dispute["object"]!).ShouldBe("dispute");
        dispute["payment_intent"] = Sandbox.IdFor(payment.Id);
        dispute["charge"] = Sandbox.ChargeFor(payment.Id);
        dispute["amount"] = (long)((amount ?? payment.ChargedAmount) * 100m);
        return @event;
    }

    // A recorded charge.refunded event pointed at a payment this host made, with the total refunded so far.
    private static JsonObject Refunded(JsonObject @event, Harness h, PaymentView payment, decimal refunded)
    {
        ((string)@event["account"]!).ShouldBe(Account);
        var charge = @event["data"]!["object"]!.AsObject();
        ((string)charge["object"]!).ShouldBe("charge");
        charge["id"] = Sandbox.ChargeFor(payment.Id);
        charge["payment_intent"] = Sandbox.IdFor(payment.Id);
        charge["amount"] = (long)(payment.ChargedAmount * 100m);
        charge["amount_refunded"] = (long)(refunded * 100m);
        charge["metadata"] = new JsonObject
        {
            ["leasebook_operation"] = payment.Id.ToString("D"),
            ["leasebook_generation"] = h.Binding.Generation.ToString("D"),
        };
        return @event;
    }

    // The recorded list of the account's events with its dispute's events pointed at a payment this
    // host made: all three, or the ones named. The rest stay the probe's own.
    private static (int Status, string Body) ListedDispute(PaymentView payment, params string[] only)
    {
        var list = StripeReplay.RecordedBody("064-events-api.json");
        var data = list["data"]!.AsArray();
        string[] disputes = ["evt_synthetic000038", "evt_synthetic000039", "evt_synthetic000040"];
        foreach (var listed in data.Where(x => disputes.Contains((string)x!["id"]!)).ToArray())
        {
            if (only.Length > 0 && !only.Contains((string)listed!["id"]!)) { data.Remove(listed); continue; }
            Disputed(listed!.AsObject(), payment);
        }
        return (200, list.ToJsonString());
    }
}

// The engine's rule for a payment reported both ways is every processor's: the simulator's too.
public sealed partial class SimulatedPaymentTests
{
    // Only the sandbox adapter reports that kind. From the simulator it is no kind at all, as it was.
    [Fact]
    public async Task A_signed_simulator_notice_of_kind_conflict_is_ignored_and_stores_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Submit(client, 600m, ct);
        await h.Tick(ct);

        await h.Deliver(h.Observation(op, "Processing") with { Kind = "Conflict" }, ct);
        await h.Tick(ct);

        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<PaymentObservation>().CountAsync(ct)).ShouldBe(0), ct);
        var view = await Staff(admin, op.Id, ct);
        (view.Status, view.Reason).ShouldBe(("Processing", null));

        // The same notice of a kind the simulator does send is kept, so the nothing above is the kind's doing.
        await h.Emit(op, "Processing", ct);
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<PaymentObservation>().CountAsync(ct)).ShouldBe(1), ct);
    }

    [Theory]
    [InlineData("Failed", "Succeeded")]
    [InlineData("Succeeded", "Failed")]
    public async Task A_simulated_payment_reported_both_failed_and_succeeded_goes_to_review_in_either_order(string first, string second)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Submit(client, 600m, ct);

        await h.Emit(op, first, ct);
        (await Staff(admin, op.Id, ct)).Status.ShouldBe(first == "Failed" ? "Failed" : "Processing");
        await h.Emit(op, second, ct);

        var view = await Staff(admin, op.Id, ct);
        (view.Status, view.Reason, view.ReceiptRecorded, view.CanCloseReview, view.CanRetry).ShouldBe(("NeedsReview", "conflicting_evidence", false, true, false));
        view.PaidAt.ShouldNotBeNull();
        // Bank evidence that would have settled it posts nothing: a person holds it.
        await h.Emit(op, "BankCredit", ct);
        var after = await Staff(admin, op.Id, ct);
        (after.Status, after.Reason, after.ReceiptRecorded).ShouldBe(("NeedsReview", "conflicting_evidence", false));
        (await h.Balance(1, ct)).ShouldBe(1000m);
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<PaymentEffect>().CountAsync(ct)).ShouldBe(0), ct);
    }
}
