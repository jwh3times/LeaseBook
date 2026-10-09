using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Payments.Stripe;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// The adapter's callback steps and its lookup by a stored reference (ADR-054, #513 step 5). Every
// event here is one the probe recorded, signed when the test runs with a secret no sandbox issued:
// the recordings keep no signature.
public sealed partial class StripeSandboxProcessorTests
{
    private const string RecordedAccount = "acct_synthetic000002";

    // The nine recorded deliveries that say how a payment went, of forty. What each must be read as.
    private static readonly (string File, string EventId, string Kind, string ProviderId, decimal Amount, long Created)[] Progress =
    [
        ("005-webhook-connect.json", "evt_synthetic000002", "Succeeded", "pi_synthetic000001", 515.24m, 1791496584),
        ("011-webhook-connect.json", "evt_synthetic000006", "Succeeded", "pi_synthetic000002", 200.00m, 1791496588),
        ("018-webhook-connect.json", "evt_synthetic000011", "Failed", "pi_synthetic000003", 200.00m, 1791496592),
        ("020-webhook-connect.json", "evt_synthetic000012", "Processing", "pi_synthetic000004", 504.03m, 1791496593),
        ("026-webhook-connect.json", "evt_synthetic000018", "Succeeded", "pi_synthetic000004", 504.03m, 1791496614),
        ("031-webhook-connect.json", "evt_synthetic000020", "Processing", "pi_synthetic000005", 200.00m, 1791496628),
        ("037-webhook-connect.json", "evt_synthetic000026", "Failed", "pi_synthetic000005", 200.00m, 1791496649),
        ("043-webhook-connect.json", "evt_synthetic000029", "Processing", "pi_synthetic000006", 200.00m, 1791496659),
        ("049-webhook-connect.json", "evt_synthetic000035", "Succeeded", "pi_synthetic000006", 200.00m, 1791496681),
    ];

    [Fact]
    public void A_body_signed_with_the_hosts_secret_is_authentic_and_is_the_body_that_was_signed()
    {
        var h = new Adapter();
        var body = Bytes(StripeReplay.Delivered("005-webhook-connect.json"));

        var notice = h.Processor.Authenticate(body, StripeSigning.Header(body, h.Clock.GetUtcNow()));

        notice.ShouldNotBeNull().Body.ShouldBe(body);
        h.Stripe.Requests.ShouldBeEmpty();
    }

    // Five minutes either way, by the clock the host was given and not the machine's: the adapter's
    // clock here stands at noon on the day of the recordings, hours from any real time.
    [Theory]
    [InlineData(0, true)]
    [InlineData(-300, true)]
    [InlineData(300, true)]
    [InlineData(-301, false)]
    [InlineData(301, false)]
    [InlineData(-86400, false)]
    public void A_signature_is_good_for_five_minutes_either_side_of_the_hosts_clock(int secondsFromNow, bool authentic)
    {
        var h = new Adapter();
        var body = Bytes(StripeReplay.Delivered("005-webhook-connect.json"));
        var signature = StripeSigning.Header(body, h.Clock.GetUtcNow().AddSeconds(secondsFromNow));

        (h.Processor.Authenticate(body, signature) is not null).ShouldBe(authentic);

        // And by that clock alone: moved to the signature's own time, the same header is good.
        h.Clock.Advance(TimeSpan.FromSeconds(secondsFromNow));
        h.Processor.Authenticate(body, signature).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("another secret")]
    [InlineData("the fixture signing key, which is the simulator's")]
    [InlineData("a body changed after signing")]
    [InlineData("no header")]
    [InlineData("not a signature")]
    [InlineData("a timestamp and no signature")]
    [InlineData("a signature and no timestamp")]
    [InlineData("a scheme that is not v1")]
    [InlineData("a timestamp that is not a number")]
    [InlineData("two timestamps")]
    [InlineData("a signature that is not hex")]
    [InlineData("a signature cut short")]
    [InlineData("a signature over the body without its timestamp")]
    [InlineData("the simulator's format")]
    [InlineData("a part with no value")]
    [InlineData("a timestamp past the end of time")]
    [InlineData("a header longer than Stripe sends")]
    public void Anything_but_stripes_signature_over_this_body_is_refused(string difference)
    {
        var h = new Adapter();
        var body = Bytes(StripeReplay.Delivered("005-webhook-connect.json"));
        var t = h.Clock.GetUtcNow().ToUnixTimeSeconds();
        var mac = StripeSigning.Mac(body, t);
        var presented = body;
        var signature = difference switch
        {
            "another secret" => StripeSigning.Header(body, h.Clock.GetUtcNow(), "whsec_other"),
            "the fixture signing key, which is the simulator's" => StripeSigning.Header(body, h.Clock.GetUtcNow(), h.Settings.SigningKey),
            "a body changed after signing" => $"t={t},v1={mac}",
            "no header" => "",
            "not a signature" => "Stripe was here",
            "a timestamp and no signature" => $"t={t}",
            "a signature and no timestamp" => $"v1={mac}",
            "a scheme that is not v1" => $"t={t},v0={mac}",
            "a timestamp that is not a number" => $"t=now,v1={mac}",
            "two timestamps" => $"t={t},t={t},v1={mac}",
            "a signature that is not hex" => $"t={t},v1={new string('z', 64)}",
            "a signature cut short" => $"t={t},v1={mac[..62]}",
            "a signature over the body without its timestamp" =>
                $"t={t},v1={Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(StripeSigning.Secret), body))}",
            // What the simulator's callback routes take: "<seconds>.<hex>", here with the right secret.
            "the simulator's format" => $"{t}.{mac}",
            "a part with no value" => $"t={t},v1={mac},v1",
            // Correctly signed for the time it names, which no clock can be near. Refused, not thrown on.
            "a timestamp past the end of time" => $"t=99999999999999999,v1={StripeSigning.Mac(body, 99999999999999999)}",
            // Stripe's own header and then a scheme that is passed over, a thousand characters of it.
            "a header longer than Stripe sends" => $"t={t},v1={mac},v0={new string('a', 1024)}",
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };
        if (difference == "a body changed after signing") { presented = [.. body, (byte)' ']; }

        h.Processor.Authenticate(presented, signature).ShouldBeNull();

        // The same body under Stripe's own header is authentic, so each refusal is its difference's doing.
        h.Processor.Authenticate(body, $"t={t},v1={mac}").ShouldNotBeNull();
    }

    // The scheme itself, against an answer worked out apart from any code here, so that the check is
    // not only compared with a copy of itself. The key is the whole secret as text, prefix and all:
    //   printf '%s' '1791496587.{"id":"evt_vector","object":"event"}' | openssl dgst -sha256 -hmac 'whsec_test_vector_secret'
    [Fact]
    public void A_signature_worked_out_by_hand_is_the_one_accepted()
    {
        const string known = "62f168c739e46de5dd2a13a63690c3cac6c7df363866987235316438907f4b66";
        var h = new Adapter(("Payments:Stripe:WebhookSecret", "whsec_test_vector_secret"));
        h.Clock.Advance(DateTimeOffset.FromUnixTimeSeconds(1791496587) - h.Clock.GetUtcNow());
        var body = Encoding.ASCII.GetBytes("""{"id":"evt_vector","object":"event"}""");

        h.Processor.Authenticate(body, "t=1791496587,v1=" + known).ShouldNotBeNull();
        h.Processor.Authenticate(body, "t=1791496587,v1=" + known[..^1] + "7").ShouldBeNull();
        h.Processor.Authenticate(body, "t=1791496588,v1=" + known).ShouldBeNull();
    }

    // Stripe sends one signature for each secret in force while a secret is being rolled.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void One_matching_signature_among_several_is_enough(bool matchingFirst)
    {
        var h = new Adapter();
        var body = Bytes(StripeReplay.Delivered("005-webhook-connect.json"));
        var t = h.Clock.GetUtcNow().ToUnixTimeSeconds();
        var (ours, other) = (StripeSigning.Mac(body, t), StripeSigning.Mac(body, t, "whsec_other"));

        h.Processor.Authenticate(body, matchingFirst ? $"t={t},v1={ours},v1={other}" : $"t={t},v1={other},v0={other},v1={ours}").ShouldNotBeNull();
        h.Processor.Authenticate(body, $"t={t},v1={other},v1={other}").ShouldBeNull();
    }

    [Fact]
    public void A_body_larger_than_a_stripe_callback_may_be_is_refused_however_it_is_signed()
    {
        var h = new Adapter();
        StripeSandboxProcessor.MaxNoticeBytes.ShouldBe(64 * 1024);
        // Larger than the simulator's notices are allowed to be: the largest recorded event is near 5 KB.
        var allowed = Encoding.UTF8.GetBytes(new string(' ', StripeSandboxProcessor.MaxNoticeBytes));
        var oversize = Encoding.UTF8.GetBytes(new string(' ', StripeSandboxProcessor.MaxNoticeBytes + 1));

        h.Processor.Authenticate(allowed, StripeSigning.Header(allowed, h.Clock.GetUtcNow())).ShouldNotBeNull();
        h.Processor.Authenticate(oversize, StripeSigning.Header(oversize, h.Clock.GetUtcNow())).ShouldBeNull();
    }

    [Fact]
    public void Nothing_is_authentic_once_the_host_is_not_a_sandbox_host_or_holds_no_secret()
    {
        var body = Bytes(StripeReplay.Delivered("005-webhook-connect.json"));

        var h = new Adapter();
        h.Settings.Mode = PaymentModes.Simulation;
        h.Processor.Authenticate(body, StripeSigning.Header(body, h.Clock.GetUtcNow())).ShouldBeNull();

        // Signed with the empty secret, which is what a host with none would otherwise be checking against.
        h = new Adapter();
        h.Settings.Stripe!.WebhookSecret = "";
        h.Processor.Authenticate(body, StripeSigning.Header(body, h.Clock.GetUtcNow(), "")).ShouldBeNull();
    }

    [Fact]
    public async Task Of_the_recorded_deliveries_the_nine_about_a_payments_progress_are_read_and_the_rest_are_not_this_steps()
    {
        var h = new Adapter();
        var deliveries = StripeReplay.Deliveries();
        deliveries.Count.ShouldBe(40);
        Progress.Select(x => x.File).ShouldBeSubsetOf(deliveries.Select(x => x.Name));

        var read = 0;
        foreach (var (name, recorded) in deliveries)
        {
            var result = await Read(h, recorded);
            if (Progress.SingleOrDefault(x => x.File == name) is not { File: not null } expected)
            {
                result.ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs, name + " " + recorded["type"]);
                continue;
            }
            read++;
            result.Ignored.ShouldBeFalse(name);
            var at = DateTimeOffset.FromUnixTimeSeconds(expected.Created).UtcDateTime;
            // The generation and the bank are the binding's. Nothing is evidence of money at a bank:
            // no evidence id, no payout, not complete.
            result.Value.ShouldBe(new ProcessorObservation(expected.EventId, expected.ProviderId, RecordedAccount, PaymentModes.StripeSandbox,
                h.Binding.Generation, expected.Kind, expected.Amount, 0m, expected.Amount, "USD", h.Binding.BankId,
                new DateOnly(2026, 10, 8), "", "", false, at), name);
            result.Value!.ObservedAt.Kind.ShouldBe(DateTimeKind.Utc);
            ((string)recorded["type"]!).ShouldBe("payment_intent." + expected.Kind switch { "Failed" => "payment_failed", var kind => kind.ToLowerInvariant() });
        }
        read.ShouldBe(9);
        // The first of them in full, against literals: 21:56:24 UTC on the day of the run.
        (await Read(h, StripeReplay.Delivered("005-webhook-connect.json"))).Value!.ObservedAt.ShouldBe(new DateTime(2026, 10, 8, 21, 56, 24, DateTimeKind.Utc));
        // A snapshot event carries the payment: reading one asks Stripe nothing.
        h.Stripe.Requests.ShouldBeEmpty();
    }

    // The same connected account holds the probe's payments and those of older fixture generations.
    // They are authentic, and not this host's.
    [Theory]
    [InlineData("Payments:Fixtures:0:Generation", "0199e6a1-0000-7000-8000-000000000001")]
    [InlineData("Payments:Fixtures:0:Account", "acct_synthetic000009")]
    public async Task For_another_generation_or_an_account_nobody_bound_no_recorded_delivery_is_this_hosts(string setting, string value)
    {
        var h = new Adapter((setting, value));

        foreach (var (name, recorded) in StripeReplay.Deliveries())
        { (await Read(h, recorded)).ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs, name); }

        h.Stripe.Requests.ShouldBeEmpty();
    }

    // Each differs from a delivery that is read, 005, by one thing.
    [Theory]
    [InlineData("marked live")]
    [InlineData("no livemode")]
    [InlineData("livemode null")]
    [InlineData("livemode as text")]
    [InlineData("payment marked live")]
    [InlineData("no created")]
    [InlineData("created as text")]
    [InlineData("created before time")]
    [InlineData("no id")]
    [InlineData("id not text")]
    [InlineData("id longer than is kept")]
    [InlineData("no type")]
    [InlineData("euros")]
    [InlineData("no currency")]
    [InlineData("fractional amount")]
    [InlineData("zero amount")]
    [InlineData("negative amount")]
    [InlineData("amount as text")]
    [InlineData("amount larger than is kept")]
    [InlineData("no amount")]
    [InlineData("no data")]
    [InlineData("not a payment")]
    [InlineData("payment with no id")]
    public async Task An_event_that_cannot_be_taken_as_it_stands_is_unreadable(string difference)
    {
        var h = new Adapter();
        var changed = StripeReplay.Delivered("005-webhook-connect.json");
        var payment = changed["data"]!["object"]!.AsObject();
        switch (difference)
        {
            case "marked live": changed["livemode"] = true; break;
            case "no livemode": changed.Remove("livemode"); break;
            case "livemode null": changed["livemode"] = null; break;
            case "livemode as text": changed["livemode"] = "false"; break;
            case "payment marked live": payment["livemode"] = true; break;
            case "no created": changed.Remove("created"); break;
            case "created as text": changed["created"] = "1791496584"; break;
            case "created before time": changed["created"] = 0; break;
            case "no id": changed.Remove("id"); break;
            case "id not text": changed["id"] = 7; break;
            case "id longer than is kept": changed["id"] = "evt_" + new string('a', 97); break;
            case "no type": changed.Remove("type"); break;
            case "euros": payment["currency"] = "eur"; break;
            case "no currency": payment.Remove("currency"); break;
            case "fractional amount": payment["amount"] = 51524.5m; break;
            case "zero amount": payment["amount"] = 0; break;
            case "negative amount": payment["amount"] = -51524; break;
            case "amount as text": payment["amount"] = "51524"; break;
            case "amount larger than is kept": payment["amount"] = 100_000_000_000_000; break;
            case "no amount": payment.Remove("amount"); break;
            case "no data": changed.Remove("data"); break;
            case "not a payment": payment["object"] = "charge"; break;
            case "payment with no id": payment.Remove("id"); break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }

        // Unreadable, which the route refuses, and not "not ours", which it would acknowledge and drop.
        (await Read(h, changed)).ShouldBe(ProcessorRead<ProcessorObservation>.Of(null));

        (await Read(h, StripeReplay.Delivered("005-webhook-connect.json"))).Value.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"evt_synthetic000002\"")]
    [InlineData("null")]
    [InlineData("7")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"id\":\"evt_1\",\"id\":\"evt_2\"}")]
    public async Task A_body_that_is_not_one_json_object_is_unreadable(string text)
    {
        var h = new Adapter();
        var body = Encoding.UTF8.GetBytes(text);
        var notice = h.Processor.Authenticate(body, StripeSigning.Header(body, h.Clock.GetUtcNow())).ShouldNotBeNull();

        (await h.Processor.ReadObservationAsync(notice, Ct)).ShouldBe(ProcessorRead<ProcessorObservation>.Of(null));
    }

    [Theory]
    [InlineData("no account")]
    [InlineData("account not text")]
    [InlineData("another kind of event")]
    [InlineData("no metadata")]
    [InlineData("no generation")]
    [InlineData("generation not an id")]
    [InlineData("no operation")]
    [InlineData("operation not an id")]
    // Not this host's before it is anything else: what is wrong with another fixture's payment is not ours to refuse.
    [InlineData("another generation, in euros")]
    [InlineData("another kind of event, with no data")]
    public async Task An_event_for_no_payment_of_this_fixtures_is_not_ours(string difference)
    {
        var h = new Adapter();
        var changed = StripeReplay.Delivered("005-webhook-connect.json");
        var payment = changed["data"]!["object"]!.AsObject();
        var metadata = payment["metadata"]!.AsObject();
        switch (difference)
        {
            case "no account": changed.Remove("account"); break;
            case "account not text": changed["account"] = 2; break;
            case "another kind of event": changed["type"] = "payment_intent.canceled"; break;
            case "no metadata": payment.Remove("metadata"); break;
            case "no generation": metadata.Remove("leasebook_generation"); break;
            case "generation not an id": metadata["leasebook_generation"] = "the first"; break;
            case "no operation": metadata.Remove("leasebook_operation"); break;
            case "operation not an id": metadata["leasebook_operation"] = "rent"; break;
            case "another generation, in euros": metadata["leasebook_generation"] = Guid.NewGuid().ToString(); payment["currency"] = "eur"; break;
            case "another kind of event, with no data": changed["type"] = "payout.paid"; changed.Remove("data"); break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }

        (await Read(h, changed)).ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs);

        (await Read(h, StripeReplay.Delivered("005-webhook-connect.json"))).Value.ShouldNotBeNull();
    }

    // ADR-054: an event marked live is rejected. Before the account is looked at, so that it is
    // refused whoever it is for and never acknowledged as merely someone else's.
    [Fact]
    public async Task A_live_event_is_refused_whoever_it_is_for()
    {
        var h = new Adapter(("Payments:Fixtures:0:Account", "acct_synthetic000009"));
        var live = StripeReplay.Delivered("005-webhook-connect.json");
        live["livemode"] = true;

        (await Read(h, live)).ShouldBe(ProcessorRead<ProcessorObservation>.Of(null));
    }

    [Fact]
    public async Task No_delivery_is_read_as_payout_evidence_yet()
    {
        var h = new Adapter();
        foreach (var (name, recorded) in StripeReplay.Deliveries())
        {
            (await h.Processor.ReadSettlementAsync(Authentic(h, Bytes(recorded)), Ct)).ShouldBe(ProcessorRead<ProcessorSettlement>.NotOurs, name);
        }
        h.Stripe.Requests.ShouldBeEmpty();
    }

    // A reference LeaseBook stored is asked for by name. No list, whenever the operation was created.
    [Fact]
    public async Task A_lookup_with_a_stored_reference_asks_for_that_payment_and_lists_nothing()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded("028-ach-final.json");
        var request = h.Request(AchOperation, 504.03m, PaymentMethods.Ach) with { ProviderId = "pi_synthetic000004" };

        (await h.Processor.LookupAsync(request, Ct)).ShouldBe(new ProcessorResult("Accepted", "pi_synthetic000004"));

        var sent = h.Stripe.Requests.ShouldHaveSingleItem();
        (sent.Method, sent.Path).ShouldBe(("GET", "/v1/payment_intents/pi_synthetic000004"));
        sent.Query.ShouldBeEmpty();
        sent.Headers["Stripe-Account"].ShouldBe(h.Binding.Account);
        sent.Headers.ShouldNotContainKey("Idempotency-Key");

        // Without one it is the list scan it was, so the single request above is the reference's doing.
        h.Stripe.Answer = _ => StripeReplay.Recorded("055-recovery-list.json");
        await h.Processor.LookupAsync(request with { ProviderId = null }, Ct);
        h.Stripe.Requests[1].Path.ShouldBe("/v1/payment_intents");
    }

    // The same checks a payment found by the scan must pass. Each differs from one that passes by one thing.
    [Theory]
    [InlineData("another operation", "PaymentConflictException")]
    [InlineData("no operation", "PaymentConflictException")]
    [InlineData("generation", "PaymentConflictException")]
    [InlineData("no generation", "PaymentConflictException")]
    [InlineData("no metadata", "PaymentConflictException")]
    [InlineData("amount", "PaymentConflictException")]
    [InlineData("currency", "PaymentConflictException")]
    [InlineData("another payment answered", "PaymentConflictException")]
    [InlineData("a state nothing here can complete", "PaymentConflictException")]
    // Stripe has no payment under a reference LeaseBook stored: evidence that disagrees with what it holds.
    [InlineData("not found", "PaymentConflictException")]
    [InlineData("live", "PaymentUnavailableException")]
    // Live before it is anything else, as anywhere in a list: the key is reading a live account.
    [InlineData("live, and another operation's", "PaymentUnavailableException")]
    // Not a verdict on the payment: left to be retried, as for the scan.
    [InlineData("key refused", "StripeException")]
    [InlineData("permission refused", "StripeException")]
    [InlineData("rate limited", "StripeException")]
    [InlineData("fault at Stripe", "StripeException")]
    public async Task A_stored_reference_that_stripe_answers_differently_is_never_taken_as_found(string difference, string expected)
    {
        var h = new Adapter();
        var operation = UuidV7.NewId();
        var intent = StripeReplay.Intent("pi_stored", difference.Contains("another operation", StringComparison.Ordinal) ? UuidV7.NewId() : operation,
            difference == "generation" ? Guid.NewGuid() : RecordedGeneration, difference == "amount" ? 1001 : 1000, difference == "currency" ? "eur" : "usd");
        intent["status"] = difference == "a state nothing here can complete" ? "requires_action" : "succeeded";
        if (difference == "no operation") { intent["metadata"]!.AsObject().Remove("leasebook_operation"); }
        if (difference == "no generation") { intent["metadata"]!.AsObject().Remove("leasebook_generation"); }
        if (difference == "no metadata") { intent["metadata"] = new JsonObject(); }
        if (difference == "another payment answered") { intent["id"] = "pi_other"; }
        if (difference.StartsWith("live", StringComparison.Ordinal)) { intent["livemode"] = true; }
        h.Stripe.Answer = _ => difference switch
        {
            "not found" => (404, Error("invalid_request_error", "resource_missing")),
            "key refused" => (401, Error("authentication_error", null)),
            "permission refused" => (403, Error("permission_error", null)),
            "rate limited" => (429, Error("invalid_request_error", "rate_limit")),
            "fault at Stripe" => (500, Error("api_error", null)),
            _ => (200, intent.ToJsonString()),
        };
        var request = h.Request(operation, 10m, PaymentMethods.Card) with { ProviderId = "pi_stored" };

        var thrown = await Should.ThrowAsync<Exception>(() => h.Processor.LookupAsync(request, Ct));

        thrown.GetType().Name.ShouldBe(expected);
        // One question, and never a search or a charge on the strength of a bad answer.
        h.Stripe.Requests.ShouldHaveSingleItem().Path.ShouldBe("/v1/payment_intents/pi_stored");

        // And with nothing different it is found.
        h.Stripe.Answer = _ => (200, StripeReplay.Intent("pi_stored", operation, RecordedGeneration, 1000).ToJsonString());
        (await h.Processor.LookupAsync(request, Ct)).ShouldBe(new ProcessorResult("Accepted", "pi_stored"));
    }

    [Fact]
    public void A_worker_passes_the_reference_an_operation_holds()
    {
        var h = new Adapter();
        var operation = new PaymentOperation { Id = UuidV7.NewId(), Fingerprint = "f", Amount = 10m, Currency = "USD", Method = PaymentMethods.Card };

        ProcessorRequest.For(h.Binding, operation).ProviderId.ShouldBeNull();
        operation.ProviderId = "pi_stored";
        ProcessorRequest.For(h.Binding, operation).ProviderId.ShouldBe("pi_stored");
    }

    private static byte[] Bytes(JsonNode @event) => Encoding.UTF8.GetBytes(@event.ToJsonString());

    private static ProcessorNotice Authentic(Adapter h, byte[] body) =>
        h.Processor.Authenticate(body, StripeSigning.Header(body, h.Clock.GetUtcNow())).ShouldNotBeNull();

    // Only through Authenticate, as the route does: nothing reads a body that did not pass it.
    private static Task<ProcessorRead<ProcessorObservation>> Read(Adapter h, JsonNode @event) =>
        h.Processor.ReadObservationAsync(Authentic(h, Bytes(@event)), Ct);
}

// The callback route in a whole sandbox host: what a delivery stores, wakes and leaves alone.
public sealed partial class StripeSandboxHostTests
{
    private const string Succeeded = "005-webhook-connect.json";
    private const string Failed = "018-webhook-connect.json";
    private const string AchProcessing = "020-webhook-connect.json";
    private const string AchSucceeded = "026-webhook-connect.json";
    private static readonly DateTime SucceededAt = DateTimeOffset.FromUnixTimeSeconds(1791496584).UtcDateTime;

    [Fact]
    public async Task A_card_payment_that_succeeds_is_marked_paid_stays_in_processing_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(500m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        (await h.Operation(payment.Id, ct)).ProviderId.ShouldBe(Sandbox.IdFor(payment.Id));
        var before = sandbox.Replay.Requests.Count;

        var delivered = await h.Deliver(Event(Succeeded, h, payment), ct);

        delivered.Status.ShouldBe(HttpStatusCode.NoContent, delivered.Body);
        // Stored under the organization the account is bound to: this delivery did enter one.
        delivered.Commands.ShouldBeGreaterThan(0);
        // The route stores and acknowledges. It asks Stripe nothing and decides nothing.
        sandbox.Replay.Requests.Count.ShouldBe(before);
        (await h.Operation(payment.Id, ct)).PaidAt.ShouldBeNull();

        await h.Run(ct);

        // Woken with its reference known: that one payment is asked for. No list, and no charge.
        sandbox.Replay.Requests.Skip(before).Select(x => (x.Method, x.Path)).ShouldBe([("GET", "/v1/payment_intents/" + Sandbox.IdFor(payment.Id))]);
        var operation = await h.Operation(payment.Id, ct);
        // Paid at Stripe is not money at the bank. Nothing posts until bank evidence says so.
        (operation.Status, operation.Reason, operation.PaidAt, operation.JournalId, operation.ProcessedCount).ShouldBe(("Processing", null, SucceededAt, null, 1));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        var fact = (await h.Facts(ct)).ShouldHaveSingleItem();
        (fact.EventId, fact.ProviderId, fact.Kind, fact.Mode, fact.Generation, fact.BankId, fact.Account).ShouldBe(
            ("evt_synthetic000002", Sandbox.IdFor(payment.Id), "Succeeded", PaymentModes.StripeSandbox, h.Binding.Generation, h.Binding.BankId, Account));
        (fact.Gross, fact.Fee, fact.Net, fact.EvidenceId, fact.PayoutId, fact.Complete).ShouldBe((payment.ChargedAmount, 0m, payment.ChargedAmount, "", "", false));

        // Shown to staff and to the tenant as paid and in progress, with no bank evidence to cite.
        await h.InOrg(async sp =>
        {
            var view = (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>().Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            (view.Status, view.ReceiptRecorded, view.EvidenceReference, view.PaidAt).ShouldBe(("Processing", false, null, SucceededAt));
        }, ct);

        // And it rests: another pass finds nothing due and asks nothing.
        await h.Run(ct);
        sandbox.Replay.Requests.Count.ShouldBe(before + 1);
    }

    [Fact]
    public async Task A_declined_card_fails_when_its_event_is_read_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Declined = true };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        // Accepted, and waiting: that the card was refused is the event's to say.
        var waiting = await h.Operation(payment.Id, ct);
        (waiting.Status, waiting.ProviderId).ShouldBe(("Processing", Sandbox.IdFor(payment.Id)));

        (await h.Deliver(Event(Failed, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.PaidAt, operation.JournalId).ShouldBe(("Failed", "collection_failed", null, null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    // Stripe promises no order. Whichever way round, and whether or not the worker ran between them.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_ach_payments_two_events_end_the_same_in_either_order(bool successFirst, bool workerBetween)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        string[] order = successFirst ? [AchSucceeded, AchProcessing] : [AchProcessing, AchSucceeded];

        (await h.Deliver(Event(order[0], h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        if (workerBetween) { await h.Run(ct); }
        (await h.Deliver(Event(order[1], h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId, operation.ProcessedCount).ShouldBe(("Processing", null, null, 2));
        // Paid when Stripe said it succeeded, whichever event came last.
        operation.PaidAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1791496614).UtcDateTime);
        (await h.Facts(ct)).Select(x => x.Kind).Order().ShouldBe(["Processing", "Succeeded"]);
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    [Fact]
    public async Task A_delivery_repeated_is_one_fact_and_the_same_event_with_other_content_goes_to_review()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        var succeeded = Event(Succeeded, h, payment);

        (await h.Deliver(succeeded, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);
        var asked = sandbox.Replay.Requests.Count;
        // Stripe delivers at least once. The repeat is acknowledged, signed afresh as Stripe's retries are.
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        (await h.Deliver(succeeded, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);

        // One fact, and nothing woken by the repeat.
        (await h.Facts(ct)).ShouldHaveSingleItem().Kind.ShouldBe("Succeeded");
        sandbox.Replay.Requests.Count.ShouldBe(asked);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing");

        // The same event id saying something else, and authentic: evidence that disagrees with itself.
        var altered = (JsonObject)succeeded.DeepClone();
        altered["data"]!["object"]!["amount"] = (long)(payment.ChargedAmount * 100m) + 1;
        (await h.Deliver(altered, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.JournalId).ShouldBe(("NeedsReview", "conflicting_evidence", null));
        (await h.Facts(ct)).Select(x => x.Kind).Order().ShouldBe(["Conflict", "Succeeded"]);
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    // Authentic, and for a connected account no fixture here is bound to: the platform's other
    // accounts, or its own. Acknowledged so that Stripe stops sending it, and nothing else at all.
    [Theory]
    [InlineData("acct_synthetic000009")]
    [InlineData(null)]
    public async Task An_event_for_an_account_nobody_bound_is_acknowledged_and_enters_no_organization(string? account)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        await h.Run(ct);
        var asked = sandbox.Replay.Requests.Count;
        var elsewhere = Event(Succeeded, h, payment);
        if (account is null) { elsewhere.Remove("account"); } else { elsewhere["account"] = account; }

        var delivered = await h.Deliver(elsewhere, ct);

        delivered.Status.ShouldBe(HttpStatusCode.NoContent);
        // Not one database command: no organization scope was opened, and no organization was read.
        delivered.Commands.ShouldBe(0);
        (await h.Facts(ct)).ShouldBeEmpty();
        sandbox.Replay.Requests.Count.ShouldBe(asked);
        await h.Run(ct);
        (await h.Operation(payment.Id, ct)).PaidAt.ShouldBeNull();

        // The same event for the bound account is stored, so the nothing above is the account's doing.
        var ours = await h.Deliver(Event(Succeeded, h, payment), ct);
        ours.Commands.ShouldBeGreaterThan(0);
        (await h.Facts(ct)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("signed with another secret")]
    [InlineData("signed five minutes and a second ago")]
    [InlineData("signed as the simulator signs")]
    [InlineData("signed for a time no clock can hold")]
    [InlineData("a body changed after signing")]
    [InlineData("marked live")]
    [InlineData("not json")]
    [InlineData("larger than a callback may be")]
    public async Task A_callback_that_is_not_authentic_or_cannot_be_read_is_refused_and_nothing_is_kept(string difference)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        await h.Run(ct);
        var succeeded = Event(Succeeded, h, payment);
        var body = Encoding.UTF8.GetBytes(succeeded.ToJsonString());
        JsonNode sent = succeeded;
        string? signature = null;
        switch (difference)
        {
            case "unsigned": signature = ""; break;
            case "signed with another secret": signature = StripeSigning.Header(body, h.Clock.GetUtcNow(), "whsec_other"); break;
            case "signed five minutes and a second ago": signature = StripeSigning.Header(body, h.Clock.GetUtcNow().AddSeconds(-301)); break;
            case "signed as the simulator signs":
                signature = $"{h.Clock.GetUtcNow().ToUnixTimeSeconds()}.{StripeSigning.Mac(body, h.Clock.GetUtcNow().ToUnixTimeSeconds())}"; break;
            // A refusal like any other, and not a fault: nothing about the header may bring the route down.
            case "signed for a time no clock can hold": signature = $"t=99999999999999999,v1={StripeSigning.Mac(body, 99999999999999999)}"; break;
            case "a body changed after signing":
                signature = StripeSigning.Header(body, h.Clock.GetUtcNow());
                sent = succeeded.DeepClone(); sent["data"]!["object"]!["amount"] = 1; break;
            // Correctly signed, both of these: refused for what they say.
            case "marked live": succeeded["livemode"] = true; break;
            case "not json": sent = JsonValue.Create("payment_intent.succeeded"); break;
            // Correctly signed too, and one byte over. The padding is a member no reader looks at.
            case "larger than a callback may be":
                succeeded["padding"] = new string('x', StripeSandboxProcessor.MaxNoticeBytes + 1 - body.Length - ",\"padding\":\"\"".Length); break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }
        if (difference == "larger than a callback may be") { Encoding.UTF8.GetByteCount(sent.ToJsonString()).ShouldBe(StripeSandboxProcessor.MaxNoticeBytes + 1); }

        var delivered = await h.Deliver(sent, ct, signature);

        delivered.Status.ShouldBe(HttpStatusCode.BadRequest);
        // The one generic refusal, with a reference and nothing about why.
        var problem = JsonNode.Parse(delivered.Body)!;
        ((string)problem["code"]!).ShouldBe("invalid_payment_callback");
        ((string?)problem["correlationId"]).ShouldNotBeNullOrEmpty();
        delivered.Commands.ShouldBe(0);
        (await h.Facts(ct)).ShouldBeEmpty();
        await h.Run(ct);
        (await h.Operation(payment.Id, ct)).PaidAt.ShouldBeNull();

        // The event as Stripe sent it is taken, so each refusal above is its difference's doing.
        (await h.Deliver(Event(Succeeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await h.Facts(ct)).ShouldHaveSingleItem();
    }

    // The largest body the route reads is read whole and judged: here acknowledged as not ours.
    [Fact]
    public async Task A_callback_of_exactly_the_largest_size_is_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var large = StripeReplay.Delivered("004-webhook-connect.json");
        large["padding"] = new string('x', StripeSandboxProcessor.MaxNoticeBytes - Encoding.UTF8.GetByteCount(large.ToJsonString()) - ",\"padding\":\"\"".Length);
        Encoding.UTF8.GetByteCount(large.ToJsonString()).ShouldBe(StripeSandboxProcessor.MaxNoticeBytes);

        (await h.Deliver(large, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
    }

    // Stripe can deliver an event before the worker has stored the reference its own request made.
    [Fact]
    public async Task An_event_that_arrives_before_the_reference_is_stored_counts_once_it_is()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        var entries = await h.JournalEntries(ct);
        (await h.Operation(payment.Id, ct)).ProviderId.ShouldBeNull();

        // Kept under the organization, with no operation yet to wake.
        (await h.Deliver(Event(Succeeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await h.Facts(ct)).ShouldHaveSingleItem().ProviderId.ShouldBe(Sandbox.IdFor(payment.Id));
        (await h.Operation(payment.Id, ct)).PaidAt.ShouldBeNull();

        await h.Run(ct);

        // The worker's own pass: looked for by the scan, made, and judged with the fact already there.
        sandbox.Replay.Requests.Skip(2).Select(x => (x.Method, x.Path)).ShouldBe([("GET", "/v1/payment_intents"), ("POST", "/v1/payment_intents")]);
        var operation = await h.Operation(payment.Id, ct);
        (operation.ProviderId, operation.Status, operation.PaidAt, operation.ProcessedCount, operation.JournalId).ShouldBe(
            (Sandbox.IdFor(payment.Id), "Processing", SucceededAt, 1, null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    [Fact]
    public async Task The_simulators_callback_routes_do_not_exist_in_a_sandbox_host()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var body = StripeReplay.Delivered(Succeeded);

        foreach (var route in new[] { "/callbacks/payments/simulation", "/callbacks/payments/simulation/payout" })
        {
            // Not found, whatever is presented: Stripe's header, the simulator's, or none.
            (await h.Deliver(body, ct, route: route)).Status.ShouldBe(HttpStatusCode.NotFound, route);
            (await h.Deliver(body, ct, route: route, header: "X-Simulation-Signature")).Status.ShouldBe(HttpStatusCode.NotFound, route);
            (await h.Deliver(body, ct, signature: "", route: route)).Status.ShouldBe(HttpStatusCode.NotFound, route);
        }
        // Stripe's route is there: the same unsigned request is refused by it, not missing.
        (await h.Deliver(body, ct, signature: "")).Status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(PaymentModes.Simulation)]
    [InlineData(PaymentModes.Disabled)]
    public async Task Stripes_callback_route_exists_only_in_a_sandbox_host(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Warning" };
        if (mode == PaymentModes.Simulation)
        {
            var binding = new FixtureBinding(UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId(), "sim_" + UuidV7.NewId().ToString("N"));
            await PaymentFixtureBootstrap.SeedAsync(fixture.Api.Services, binding, UuidV7.NewId().ToString("N"), ct);
            settings["Payments:Mode"] = mode;
            settings["Payments:SigningKey"] = new string('x', 64);
            settings["Payments:Fixtures:0:OrgId"] = binding.OrgId.ToString();
            settings["Payments:Fixtures:0:Generation"] = binding.Generation.ToString();
            settings["Payments:Fixtures:0:BankId"] = binding.BankId.ToString();
            settings["Payments:Fixtures:0:Account"] = binding.Account;
        }
        await using var factory = new ApiFactory(fixture.AppConnectionString, settings);
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService)
                && x.ImplementationType?.Name == "PaymentWorker").ToArray()) { services.Remove(service); }
        }));
        using var client = app.CreateClient();
        var body = Encoding.UTF8.GetBytes(StripeReplay.Delivered(Succeeded).ToJsonString());

        async Task<HttpStatusCode> Post(string route, string header, string signature)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new ByteArrayContent(body) };
            request.Headers.TryAddWithoutValidation(header, signature).ShouldBeTrue();
            using var response = await client.SendAsync(request, ct);
            return response.StatusCode;
        }

        (await Post("/callbacks/payments/stripe", "Stripe-Signature", StripeSigning.Header(body, DateTimeOffset.UtcNow))).ShouldBe(HttpStatusCode.NotFound);
        // The simulator's two are there in a simulation host, refusing what they cannot verify, and
        // nowhere else. That they accept the simulator's own notices is SimulatedPaymentTests' to show.
        var simulators = mode == PaymentModes.Simulation ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound;
        (await Post("/callbacks/payments/simulation", "X-Simulation-Signature", "bad")).ShouldBe(simulators);
        (await Post("/callbacks/payments/simulation/payout", "X-Simulation-Signature", "bad")).ShouldBe(simulators);
    }

    // Carried from step 4: a key Stripe refuses, met by the worker and not by the adapter alone. It
    // charged nothing and mending the key makes the request valid, so it is retried, not parked.
    [Fact]
    public async Task A_key_stripe_refuses_at_lookup_is_a_technical_failure_the_worker_retries()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        var before = sandbox.Replay.Requests.Count;
        sandbox.Refused = (401, """{"error":{"type":"invalid_request_error","message":"Invalid API Key provided: sk_test_***abc"}}""");

        await h.Run(ct);

        sandbox.Replay.Requests.Skip(before).Select(x => (x.Method, x.Path)).ShouldBe([("GET", "/v1/payment_intents")]);
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.Attempts, operation.ProviderId, operation.JournalId).ShouldBe(("Processing", "technical_failure", 1, null, null));
        // Scheduled, a second on by the host's clock, and not due before then.
        operation.DueAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime.AddSeconds(PaymentEngine.RetrySeconds[0]), TimeSpan.FromMilliseconds(1));
        await h.Run(ct);
        sandbox.Replay.Requests.Count.ShouldBe(before + 1);

        // The key mended, the same operation is found absent and charged once.
        sandbox.Refused = null;
        h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[0]));
        await h.Run(ct);
        operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.ProviderId).ShouldBe(("Processing", null, Sandbox.IdFor(payment.Id)));
        sandbox.Replay.Requests.Count(x => x.Method == "POST").ShouldBe(1);
    }

    // The same refusal once the reference is known: the event's wake fails the same way, and the fact waits.
    [Fact]
    public async Task A_key_stripe_refuses_when_an_event_wakes_a_payment_leaves_the_fact_to_be_judged_on_the_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        await h.Run(ct);
        (await h.Deliver(Event(Succeeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        sandbox.Refused = (403, """{"error":{"type":"permission_error","message":"Refused."}}""");

        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.PaidAt).ShouldBe(("Processing", "technical_failure", null));

        sandbox.Refused = null;
        h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[0]));
        await h.Run(ct);
        operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.PaidAt).ShouldBe(("Processing", null, SucceededAt));
        sandbox.Replay.Requests.Count(x => x.Method == "POST").ShouldBe(1);
    }

    // Carried from step 3: a sandbox host bound to an organization that is not the fixture it names
    // does not start, and Stripe is not asked anything on its behalf.
    [Theory]
    [InlineData("an organization nobody seeded")]
    [InlineData("another generation")]
    [InlineData("another bank")]
    public async Task A_sandbox_host_refuses_to_start_for_an_organization_that_is_not_its_fixture(string difference)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();

        await Should.ThrowAsync<PaymentUnavailableException>(async () =>
        {
            await using var h = await Setup(sandbox.Replay, ct, seeded => difference switch
            {
                "an organization nobody seeded" => seeded with { OrgId = UuidV7.NewId() },
                "another generation" => seeded with { Generation = UuidV7.NewId() },
                _ => seeded with { BankId = UuidV7.NewId() },
            });
        });

        sandbox.Replay.Requests.ShouldBeEmpty();
    }

    // Carried from step 3: the runner's own comparisons of mode, account and generation, in a sandbox
    // host. The adapter takes all three from the binding, so only a caller that did not could trip them.
    [Fact]
    public async Task The_runner_stores_nothing_said_in_another_mode_generation_or_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var runner = h.App.Services.GetRequiredService<PaymentRunner>();
        var ours = new ProcessorObservation("evt_runner", "pi_runner", Account, PaymentModes.StripeSandbox, h.Binding.Generation, "Succeeded",
            10m, 0m, 10m, "USD", h.Binding.BankId, new DateOnly(2026, 10, 8), "", "", false, SucceededAt);

        await runner.ReceiveAsync(ours with { Mode = PaymentModes.Simulation }, ct);
        await runner.ReceiveAsync(ours with { Generation = UuidV7.NewId() }, ct);
        await runner.ReceiveAsync(ours with { Account = "acct_synthetic000009" }, ct);
        await runner.ReceiveAsync(ours with { Kind = "payment_intent.succeeded" }, ct);
        (await h.Facts(ct)).ShouldBeEmpty();
        var payout = new ProcessorSettlement("po_runner", Account, PaymentModes.Simulation, h.Binding.Generation, "standard", 10m, "USD",
            h.Binding.BankId, new DateOnly(2026, 10, 8), "bank-po_runner", SucceededAt, []);
        (await runner.ReceiveSettlementAsync(payout, ct)).ShouldBe(new PayoutDelivery(null, false));

        await runner.ReceiveAsync(ours, ct);
        (await h.Facts(ct)).ShouldHaveSingleItem().Mode.ShouldBe(PaymentModes.StripeSandbox);
    }

    // An event and the worker's completion share one lock on the provider's reference, so that they
    // cannot pass each other. Each is shown to wait on it: the lock is held from another organization
    // transaction, opened the way the product opens one, and taken by the engine's own LockAsync.
    // Nothing here sleeps to let something happen: what a backend holds and waits for is read from
    // pg_locks, and a test goes on when the database says so.
    [Fact]
    public async Task A_completion_waits_for_whoever_holds_the_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        var reference = "reference:" + Sandbox.IdFor(payment.Id);
        var held = await Hold(h, sp => sp.GetRequiredService<PaymentEngine>().LockAsync(reference, ct), ct);
        Task run;
        try
        {
            run = h.Run(ct);
            await Until(async () => run.IsCompleted || (await Advisory(h, reference, ct)).Waiting.Length == 1, "the completion to reach the reference lock", ct);
            run.IsCompleted.ShouldBeFalse();
            // Charged, and its result not yet kept: the completion is what waits.
            sandbox.Replay.Requests.Count(x => x.Method == "POST").ShouldBe(1);
            (await h.Operation(payment.Id, ct)).ProviderId.ShouldBeNull();
        }
        finally { await held.Release(); }
        await run;

        (await h.Operation(payment.Id, ct)).ProviderId.ShouldBe(Sandbox.IdFor(payment.Id));
    }

    [Fact]
    public async Task A_delivery_waits_for_whoever_holds_the_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        var reference = "reference:" + Sandbox.IdFor(payment.Id);
        // What the argument for the lock rests on: a statement after it sees what committed before it.
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"SELECT current_setting('transaction_isolation') AS \"Value\"").SingleAsync(ct)).ShouldBe("read committed"), ct);
        var held = await Hold(h, sp => sp.GetRequiredService<PaymentEngine>().LockAsync(reference, ct), ct);
        Task<(HttpStatusCode Status, int Commands, string Body)> delivery;
        try
        {
            delivery = h.Deliver(Event(Succeeded, h, payment), ct);
            await Until(async () => delivery.IsCompleted || (await Advisory(h, reference, ct)).Waiting.Length == 1, "the delivery to reach the reference lock", ct);
            delivery.IsCompleted.ShouldBeFalse();
            // Not stored before the lock is had: the fact and the search for its operation are one step behind it.
            (await h.Facts(ct)).ShouldBeEmpty();
        }
        finally { await held.Release(); }

        (await delivery).Status.ShouldBe(HttpStatusCode.NoContent);
        (await h.Facts(ct)).ShouldHaveSingleItem();

        // Another reference's lock holds nothing up.
        var other = await Hold(h, sp => sp.GetRequiredService<PaymentEngine>().LockAsync("reference:pi_another", ct), ct);
        try { await h.Run(ct); }
        finally { await other.Release(); }
        (await h.Operation(payment.Id, ct)).PaidAt.ShouldBe(SucceededAt);
    }

    // The order the completion takes its two locks in: the reference, then the operation. A delivery
    // takes them in that order too, so the other way round the two could each hold what the other
    // wants. Stopped at the operation's lock, the completion must already hold the reference.
    [Fact]
    public async Task A_completion_holds_the_reference_before_it_asks_for_the_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var charged = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandbox = new Sandbox { OnCharge = () => { arrived.TrySetResult(); charged.Wait(TimeSpan.FromSeconds(15)).ShouldBeTrue(); } };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        var reference = "reference:" + Sandbox.IdFor(payment.Id);

        // The worker has claimed the operation, which took and gave back its lock, and is at Stripe.
        var run = h.Run(ct);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        var held = await Hold(h, sp => sp.GetRequiredService<PaymentEngine>().LockAsync(payment.Id.ToString(), ct), ct);
        try
        {
            charged.Set();
            await Until(async () => run.IsCompleted || (await Advisory(h, payment.Id.ToString(), ct)).Waiting.Length == 1, "the completion to reach the operation lock", ct);
            run.IsCompleted.ShouldBeFalse();
            var completion = (await Advisory(h, payment.Id.ToString(), ct)).Waiting.ShouldHaveSingleItem();
            var onReference = await Advisory(h, reference, ct);
            onReference.Holding.ShouldBe([completion]);
            onReference.Waiting.ShouldBeEmpty();
        }
        finally { await held.Release(); }
        await run;

        (await h.Operation(payment.Id, ct)).ProviderId.ShouldBe(Sandbox.IdFor(payment.Id));
    }

    // A declined card's failure is delivered as the refusal comes back, so the two meet as a matter
    // of course. Both wait behind a held reference, the second started only once the first is seen
    // waiting, and waiters are let through in the order they arrived. Either way the failure is judged.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failure_delivered_as_the_worker_completes_is_judged_whichever_goes_first(bool deliveryFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Declined = true };
        await using var h = await Setup(sandbox.Replay, ct);
        for (var round = 0; round < 3; round++)
        {
            var payment = await h.Submit(200m + round, ct, PaymentMethods.Card);
            var reference = "reference:" + Sandbox.IdFor(payment.Id);
            // Its own event: the recorded id again would be the first round's event saying something else.
            var failed = Event(Failed, h, payment);
            failed["id"] = "evt_round" + round;
            var held = await Hold(h, sp => sp.GetRequiredService<PaymentEngine>().LockAsync(reference, ct), ct);
            Task run, delivery;
            try
            {
                Task Start(bool theDelivery) => theDelivery ? h.Deliver(failed, ct) : h.Run(ct);
                var first = Start(deliveryFirst);
                await Until(async () => first.IsCompleted || (await Advisory(h, reference, ct)).Waiting.Length == 1, "the first to reach the reference lock", ct);
                var second = Start(!deliveryFirst);
                await Until(async () => first.IsCompleted || second.IsCompleted || (await Advisory(h, reference, ct)).Waiting.Length == 2, "the second to reach the reference lock", ct);
                (first.IsCompleted, second.IsCompleted).ShouldBe((false, false), $"round {round}");
                (delivery, run) = deliveryFirst ? (first, second) : (second, first);
            }
            finally { await held.Release(); }
            await Task.WhenAll(run, delivery);

            // Which went first shows in what the completion found. After the delivery, the fact: judged
            // there and then. Before it, nothing: parked, and then woken by the delivery, so due.
            var between = await h.Operation(payment.Id, ct);
            if (deliveryFirst) { (between.Status, between.ProcessedCount).ShouldBe(("Failed", 1), $"round {round}"); }
            else
            {
                (between.Status, between.ProcessedCount).ShouldBe(("Processing", 0), $"round {round}");
                between.DueAt.ShouldBeLessThanOrEqualTo(h.Clock.GetUtcNow().UtcDateTime);
            }
            await h.Run(ct);
            var operation = await h.Operation(payment.Id, ct);
            (operation.Status, operation.Reason, operation.ProcessedCount).ShouldBe(("Failed", "collection_failed", 1), $"round {round}");
        }
    }

    // The interleaving that lost the failure before the lock existed, held open so that it happens
    // every time: the completion has read the facts, found none, and not yet committed, while the
    // delivery stores its fact and looks for an operation that does not hold the reference yet.
    // The completion is stopped at its last write by a lock on the operation's row.
    [Fact]
    public async Task A_failure_delivered_while_the_completion_is_uncommitted_still_wakes_the_payment()
    {
        var ct = TestContext.Current.CancellationToken;
        using var charged = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandbox = new Sandbox { Declined = true, OnCharge = () => { arrived.TrySetResult(); charged.Wait(TimeSpan.FromSeconds(15)).ShouldBeTrue(); } };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        var reference = "reference:" + Sandbox.IdFor(payment.Id);

        // The worker has claimed the operation and is at Stripe. Its row is locked before the answer returns.
        var run = h.Run(ct);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        var row = await Hold(h, sp => sp.GetRequiredService<AppDbContext>().Database
            .ExecuteSqlAsync($"SELECT 1 FROM payment_operations WHERE id = {payment.Id} FOR UPDATE", ct), ct);
        Task<(HttpStatusCode Status, int Commands, string Body)> delivery;
        bool deliveryWaited;
        try
        {
            charged.Set();
            // Until the completion is stopped at its write: the backend that holds the operation's
            // lock is waiting for the transaction that holds the row. Its reads are behind it by then.
            await Until(async () => run.IsCompleted || (await Advisory(h, payment.Id.ToString(), ct)).Holding.Intersect(await WaitingOnARow(ct)).Any(),
                "the completion to stop at its write", ct);
            run.IsCompleted.ShouldBeFalse();
            delivery = h.Deliver(Event(Failed, h, payment), ct);
            // The delivery waits for the completion. Without the shared lock it finishes instead,
            // having found no operation to wake. Asserted last, so that what is lost without it shows first.
            await Until(async () => delivery.IsCompleted || (await Advisory(h, reference, ct)).Waiting.Length == 1, "the delivery to finish or to wait", ct);
            deliveryWaited = !delivery.IsCompleted;
        }
        finally { await row.Release(); }
        await run;
        (await delivery).Status.ShouldBe(HttpStatusCode.NoContent);

        // The completion saw no fact, and the delivery that followed it found and woke the operation.
        var waiting = await h.Operation(payment.Id, ct);
        (waiting.Status, waiting.ProviderId, waiting.ProcessedCount).ShouldBe(("Processing", Sandbox.IdFor(payment.Id), 0));
        waiting.DueAt.ShouldBeLessThanOrEqualTo(h.Clock.GetUtcNow().UtcDateTime);
        await h.Run(ct);
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.ProcessedCount).ShouldBe(("Failed", "collection_failed", 1));
        deliveryWaited.ShouldBeTrue();
    }

    // Stripe sends every kind of event it has, in bursts and from one address, and a delivery turned
    // away may never be sent again. Its route has a limit of its own, far above the one tenants and
    // staff share, and neither uses up the other's.
    [Fact]
    public async Task Stripes_deliveries_are_not_counted_against_the_limit_tenants_and_staff_share()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        // More than twice the shared limit of 60 a minute, so that whenever a window turns over during
        // the test, more than 60 of each fall inside one.
        const int many = 130;
        var ignorable = StripeReplay.Delivered("004-webhook-connect.json");

        var deliveries = new List<HttpStatusCode>();
        for (var i = 0; i < many; i++) { deliveries.Add((await h.Deliver(ignorable, ct)).Status); }
        deliveries.ShouldAllBe(x => x == HttpStatusCode.NoContent);

        // The shared limit is still there for its own routes, and those deliveries used none of it:
        // the first 60 quotes are answered, and it is quotes that run out.
        using var tenant = await h.Tenant(ct);
        var quotes = new List<HttpStatusCode>();
        for (var i = 0; i < many; i++)
        {
            using var response = await tenant.GetAsync("/api/portal/tenant/payments/quote?amount=10&method=ach", ct);
            quotes.Add(response.StatusCode);
        }
        quotes.Take(50).ShouldAllBe(x => x == HttpStatusCode.OK);
        quotes.ShouldContain(HttpStatusCode.TooManyRequests);

        // And with that one spent, Stripe is still heard.
        (await h.Deliver(ignorable, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
    }

    // What holds and what waits for the advisory lock PaymentEngine.LockAsync takes for a key in this
    // fixture's organization, by backend. The key is hashed here as it is there.
    private async Task<(int[] Holding, int[] Waiting)> Advisory(Harness h, string key, CancellationToken ct)
    {
        await using var connection = await fixture.OpenAppConnectionAsync(ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT pid, granted FROM pg_locks
            WHERE locktype = 'advisory' AND objsubid = 1
              AND ((classid::bigint << 32) | objid::bigint) = hashtextextended(@name, 0)
            ORDER BY pid
            """, connection);
        command.Parameters.AddWithValue("name", h.Binding.OrgId + ":payment:" + key);
        var rows = new List<(int Pid, bool Granted)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) { rows.Add((reader.GetInt32(0), reader.GetBoolean(1))); }
        return ([.. rows.Where(x => x.Granted).Select(x => x.Pid)], [.. rows.Where(x => !x.Granted).Select(x => x.Pid)]);
    }

    // The backends waiting for another transaction to end, which is how a wait for a locked row shows.
    private async Task<int[]> WaitingOnARow(CancellationToken ct)
    {
        await using var connection = await fixture.OpenAppConnectionAsync(ct);
        await using var command = new Npgsql.NpgsqlCommand("SELECT pid FROM pg_locks WHERE locktype = 'transactionid' AND NOT granted", connection);
        var pids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) { pids.Add(reader.GetInt32(0)); }
        return [.. pids];
    }

    // Goes on when the condition holds, and fails when it has not in half a minute. Never a wait
    // whose running out is taken to mean that something happened.
    private static async Task Until(Func<Task<bool>> condition, string what, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) { throw new TimeoutException("Waited half a minute for " + what + "."); }
            await Task.Delay(20, ct);
        }
    }

    // Holds a lock in an organization transaction of its own until released.
    private static async Task<Held> Hold(Harness h, Func<IServiceProvider, Task> take, CancellationToken ct)
    {
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = h.InOrg(async sp => { await take(sp); taken.SetResult(); await release.Task; }, ct);
        await await Task.WhenAny(taken.Task, holder);
        return new Held(holder, release);
    }

    private sealed class Held(Task holder, TaskCompletionSource release)
    {
        public async Task Release() { release.TrySetResult(); await holder; }
    }

    // A recorded event pointed at a payment this host made. Three things are substituted, all on the
    // payment inside the event: its id, for the reference the sandbox double gave; its amount, for
    // what was charged; and its metadata, for this operation and this fixture's generation. The event's
    // own id, type, account, version, livemode and created are as Stripe sent them.
    private static JsonObject Event(string recorded, Harness h, PaymentView payment)
    {
        var @event = StripeReplay.Delivered(recorded);
        ((string)@event["account"]!).ShouldBe(Account);
        var intent = @event["data"]!["object"]!.AsObject();
        intent["id"] = Sandbox.IdFor(payment.Id);
        intent["amount"] = (long)(payment.ChargedAmount * 100m);
        intent["metadata"] = new JsonObject
        {
            ["leasebook_operation"] = payment.Id.ToString("D"),
            ["leasebook_generation"] = h.Binding.Generation.ToString("D"),
        };
        return @event;
    }

    /// <summary>
    /// Answers a host as a sandbox would: the two account reads from the recordings, and payments it
    /// makes, lists and returns by reference, each shaped like a recorded one. Anything else throws.
    /// </summary>
    private sealed class Sandbox
    {
        private readonly Func<StripeReplay.Sent, (int Status, string Body)> _accounts = StripeSandboxProcessorTests.Accounts();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _payments = new();

        public Sandbox() => Replay = new StripeReplay { Answer = Answer };

        public StripeReplay Replay { get; }
        /// <summary>The state a new payment is in: a card's is settled at once, an ACH debit's is not.</summary>
        public string Status { get; init; } = "succeeded";
        /// <summary>The card is refused: HTTP 402, with the payment inside the error.</summary>
        public bool Declined { get; init; }
        /// <summary>Called when a charge arrives, before it is answered: a test can hold the worker at Stripe.</summary>
        public Action? OnCharge { get; init; }
        /// <summary>When set, what every request about a payment is answered with.</summary>
        public (int Status, string Body)? Refused { get; set; }

        public static string IdFor(Guid operation) => "pi_host" + operation.ToString("N");

        private (int Status, string Body) Answer(StripeReplay.Sent sent)
        {
            if (sent.Path.StartsWith("/v1/account", StringComparison.Ordinal)) { return _accounts(sent); }
            if (Refused is { } refused) { return refused; }
            const string payments = "/v1/payment_intents";
            if (sent.Method == "POST" && sent.Path == payments)
            {
                OnCharge?.Invoke();
                var operation = Guid.Parse(sent.Form["metadata[leasebook_operation]"]);
                var intent = StripeReplay.Intent(IdFor(operation), operation, Guid.Parse(sent.Form["metadata[leasebook_generation]"]),
                    long.Parse(sent.Form["amount"], System.Globalization.CultureInfo.InvariantCulture));
                intent["status"] = Declined ? "requires_payment_method" : Status;
                _payments[IdFor(operation)] = intent.ToJsonString();
                return Declined
                    ? (402, new JsonObject { ["error"] = new JsonObject { ["type"] = "card_error", ["code"] = "card_declined", ["payment_intent"] = intent } }.ToJsonString())
                    : (200, intent.ToJsonString());
            }
            if (sent.Method == "GET" && sent.Path == payments)
            { return StripeReplay.List(hasMore: false, [.. _payments.Values.Select(x => JsonNode.Parse(x)!.AsObject())]); }
            if (sent.Method == "GET" && sent.Path.StartsWith(payments + "/", StringComparison.Ordinal))
            {
                return _payments.TryGetValue(sent.Path[(payments.Length + 1)..], out var found) ? (200, found)
                    : (404, """{"error":{"type":"invalid_request_error","code":"resource_missing","message":"No such payment_intent."}}""");
            }
            throw new InvalidOperationException($"Unexpected request {sent.Method} {sent.Path}");
        }
    }
}
