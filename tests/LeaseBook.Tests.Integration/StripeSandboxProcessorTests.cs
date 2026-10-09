using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Payments.Stripe;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The Stripe sandbox adapter against what a sandbox really sent (ADR-054). Nothing here reaches the
/// network: every answer is a payload the probe recorded, or one built from it by changing one thing.
/// </summary>
public sealed class StripeSandboxProcessorTests
{
    // The ids the probe's recorded requests carried, so that what is sent can be compared with them whole.
    private static readonly Guid RecordedGeneration = Guid.Parse("68834ff9-f964-4044-9d3c-01c201889847");
    private static readonly Guid CardOperation = Guid.Parse("6319ed32-461a-45bd-8feb-b44e17b4639b");
    private static readonly Guid AchOperation = Guid.Parse("a55fc7d8-eb32-4df6-8022-19e534794476");
    private static readonly Guid DeclinedOperation = Guid.Parse("ce5cc8f3-7ebe-4b2d-b5c6-7574a521da56");
    private static readonly string[] IntentFields =
    [
        "amount", "currency", "payment_method", "confirm", "automatic_payment_methods[enabled]",
        "automatic_payment_methods[allow_redirects]", "metadata[leasebook_operation]", "metadata[leasebook_generation]",
    ];

    [Fact]
    public async Task A_card_payment_is_sent_as_the_probe_sent_it_and_nothing_more()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded("003-card-create.json");

        var result = await h.Processor.SubmitAsync(h.Request(CardOperation, 515.24m, PaymentMethods.Card), Ct);

        result.ShouldBe(new ProcessorResult("Accepted", "pi_synthetic000001"));
        var sent = h.Stripe.Requests.ShouldHaveSingleItem();
        (sent.Method, sent.Path).ShouldBe(("POST", "/v1/payment_intents"));
        // The whole set of fields, so that one added later (a customer, a description, the payment
        // method types Stripe refuses) fails here and not in a sandbox.
        sent.Form.Keys.Order().ShouldBe(IntentFields.Order());
        sent.Form.ShouldBe(StripeReplay.RecordedForm("003-card-create.json"), ignoreOrder: true);
        sent.Form["amount"].ShouldBe("51524");
        sent.Form["payment_method"].ShouldBe("pm_card_visa");
        sent.Headers["Stripe-Account"].ShouldBe(h.Binding.Account);
        sent.Headers["Idempotency-Key"].ShouldBe(CardOperation.ToString("D"));
        // The version the payloads were recorded at. A library that moved it would be read against stale shapes.
        sent.Headers["Stripe-Version"].ShouldBe("2026-09-30.endive");
    }

    [Fact]
    public async Task An_ach_payment_uses_the_ach_test_method_and_an_offline_mandate()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded("019-ach-create.json");

        var result = await h.Processor.SubmitAsync(h.Request(AchOperation, 504.03m, PaymentMethods.Ach), Ct);

        // Still processing at Stripe. Accepted says the processor holds the collection, not how it ended.
        result.ShouldBe(new ProcessorResult("Accepted", "pi_synthetic000004"));
        var sent = h.Stripe.Requests.ShouldHaveSingleItem();
        sent.Form.Keys.Order().ShouldBe(IntentFields.Append("mandate_data[customer_acceptance][type]").Order());
        sent.Form.ShouldBe(StripeReplay.RecordedForm("019-ach-create.json"), ignoreOrder: true);
        sent.Form["payment_method"].ShouldBe("pm_usBankAccount_success");
        sent.Form["mandate_data[customer_acceptance][type]"].ShouldBe("offline");
    }

    [Fact]
    public async Task The_test_payment_methods_are_the_bindings_own()
    {
        var h = new Adapter(("Payments:Fixtures:0:CardPaymentMethod", "pm_card_visa_chargeDeclined"),
            ("Payments:Fixtures:0:AchPaymentMethod", "pm_usBankAccount_noAccount"));
        h.Stripe.Answer = _ => StripeReplay.Recorded("003-card-create.json");

        await h.Processor.SubmitAsync(h.Request(UuidV7.NewId(), 10m, PaymentMethods.Card), Ct);
        await h.Processor.SubmitAsync(h.Request(UuidV7.NewId(), 10m, PaymentMethods.Ach), Ct);

        h.Stripe.Requests.Select(x => x.Form["payment_method"]).ShouldBe(["pm_card_visa_chargeDeclined", "pm_usBankAccount_noAccount"]);
    }

    [Fact]
    public async Task A_declined_card_is_still_a_collection_the_processor_holds()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded("015-card-declined-create.json");

        var result = await h.Processor.SubmitAsync(h.Request(DeclinedOperation, 200m, PaymentMethods.Card), Ct);

        // HTTP 402, with the payment inside the error. That it failed arrives later as an event.
        StripeReplay.Recorded("015-card-declined-create.json").Status.ShouldBe(402);
        result.ShouldBe(new ProcessorResult("Accepted", "pi_synthetic000003"));
    }

    [Fact]
    public async Task The_same_key_with_other_parameters_is_a_conflict()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded("054-idempotent-changed.json");

        await Should.ThrowAsync<PaymentConflictException>(() => h.Processor.SubmitAsync(h.Request(CardOperation, 516.24m, PaymentMethods.Card), Ct));
    }

    [Theory]
    // Stripe will not take the request however often it is sent: unavailable, and a person looks.
    [InlineData(400, "invalid_request_error", "parameter_unknown", "PaymentUnavailableException")]
    [InlineData(404, "invalid_request_error", "resource_missing", "PaymentUnavailableException")]
    // A key Stripe would not take charged nothing, and mending it makes the same request valid. Left
    // as it is, which the worker counts as a technical failure: retried, and then staff's to retry.
    [InlineData(401, "invalid_request_error", null, "StripeException")]
    [InlineData(401, "authentication_error", null, "StripeException")]
    [InlineData(403, "permission_error", null, "StripeException")]
    [InlineData(400, "authentication_error", null, "StripeException")]
    [InlineData(400, "permission_error", null, "StripeException")]
    // And so is whatever asking again may change.
    [InlineData(429, "invalid_request_error", "rate_limit", "StripeException")]
    [InlineData(409, "invalid_request_error", "idempotency_key_in_use", "StripeException")]
    [InlineData(500, "api_error", null, "StripeException")]
    [InlineData(503, "api_error", null, "StripeException")]
    public async Task A_refusal_to_charge_is_final_or_retried_by_what_it_says(int status, string type, string? code, string expected)
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => (status, Error(type, code));

        var thrown = await Should.ThrowAsync<Exception>(() => h.Processor.SubmitAsync(h.Request(UuidV7.NewId(), 10m, PaymentMethods.Card), Ct));

        thrown.GetType().Name.ShouldBe(expected);
    }

    [Theory]
    [InlineData(400, "invalid_request_error", "parameter_unknown")]
    [InlineData(400, "idempotency_error", null)]
    [InlineData(401, "authentication_error", null)]
    [InlineData(403, "permission_error", null)]
    [InlineData(404, "invalid_request_error", "resource_missing")]
    [InlineData(429, "invalid_request_error", "rate_limit")]
    [InlineData(500, "api_error", null)]
    public async Task No_refusal_of_a_lookup_is_a_verdict_on_the_payment(int status, string type, string? code)
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => (status, Error(type, code));

        // A read cannot have charged. Parked for review it could only be closed; left as a technical
        // failure it is retried, and found or made once the key or the account is mended.
        var thrown = await Should.ThrowAsync<Exception>(() => h.Processor.LookupAsync(h.Request(UuidV7.NewId(), 10m, PaymentMethods.Card), Ct));

        thrown.GetType().Name.ShouldBe("StripeException");
    }

    // Accepted is for a collection that can still end with nobody here to act: collected, on its
    // way, or refused, which arrives as an event. Any other state would wait for ever.
    [Theory]
    [InlineData("succeeded", true)]
    [InlineData("processing", true)]
    [InlineData("requires_payment_method", true)]
    [InlineData("requires_action", false)]
    [InlineData("requires_confirmation", false)]
    [InlineData("requires_capture", false)]
    [InlineData("canceled", false)]
    [InlineData("a_state_nobody_named", false)]
    public async Task A_payment_nothing_here_can_complete_goes_to_a_person_wherever_it_is_met(string status, bool accepted)
    {
        var operation = UuidV7.NewId();
        var intent = StripeReplay.Intent("pi_met", operation, RecordedGeneration, 1000);
        intent["status"] = status;
        var answers = new Dictionary<string, (int, string)>
        {
            ["created"] = (200, intent.ToJsonString()),
            ["carried by a refusal"] = (402, new JsonObject { ["error"] = new JsonObject { ["type"] = "card_error", ["payment_intent"] = intent.DeepClone() } }.ToJsonString()),
            ["listed"] = StripeReplay.List(hasMore: false, (JsonObject)intent.DeepClone()),
        };
        foreach (var (met, answer) in answers)
        {
            var h = new Adapter();
            h.Stripe.Answer = _ => answer;
            var request = h.Request(operation, 10m, PaymentMethods.Card);
            Task<ProcessorResult> Ask() => met == "listed" ? h.Processor.LookupAsync(request, Ct) : h.Processor.SubmitAsync(request, Ct);

            if (accepted) { (await Ask()).ShouldBe(new ProcessorResult("Accepted", "pi_met"), met); }
            else { (await Should.ThrowAsync<Exception>(Ask)).ShouldBeOfType<PaymentConflictException>(met); }
        }
    }

    [Fact]
    public async Task A_connection_that_fails_is_left_to_be_retried()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => throw new HttpRequestException("No route to the sandbox.");
        var request = h.Request(UuidV7.NewId(), 10m, PaymentMethods.Card);

        var submit = await Should.ThrowAsync<Exception>(() => h.Processor.SubmitAsync(request, Ct));
        var lookup = await Should.ThrowAsync<Exception>(() => h.Processor.LookupAsync(request, Ct));
        new[] { submit, lookup }.ShouldAllBe(x => !(x is PaymentConflictException) && !(x is PaymentUnavailableException));
    }

    [Fact]
    public async Task A_lookup_lists_the_creation_window_and_finds_the_payment_by_its_operation()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded("055-recovery-list.json");
        var request = h.Request(CardOperation, 515.24m, PaymentMethods.Card);

        // Six payments on the account, and the one that carries this operation's id.
        (await h.Processor.LookupAsync(request, Ct)).ShouldBe(new ProcessorResult("Accepted", "pi_synthetic000001"));

        var sent = h.Stripe.Requests.ShouldHaveSingleItem();
        (sent.Method, sent.Path).ShouldBe(("GET", "/v1/payment_intents"));
        sent.Path.ShouldNotContain("search");
        sent.Headers["Stripe-Account"].ShouldBe(h.Binding.Account);
        sent.Headers.ShouldNotContainKey("Idempotency-Key");
        // From five minutes before the operation was created, for a clock that is not Stripe's.
        var since = new DateTimeOffset(request.CreatedAt).ToUnixTimeSeconds() - 300;
        sent.Query.ShouldBe(new Dictionary<string, string> { ["created[gte]"] = since.ToString(), ["limit"] = "100" }, ignoreOrder: true);
    }

    [Fact]
    public async Task A_lookup_follows_the_list_to_its_end()
    {
        var h = new Adapter();
        var operation = UuidV7.NewId();
        h.Stripe.Answer = sent => sent.Query.GetValueOrDefault("starting_after") switch
        {
            null => StripeReplay.List(hasMore: true, StripeReplay.Intent("pi_page1a", UuidV7.NewId(), RecordedGeneration, 1000), StripeReplay.Intent("pi_page1b", UuidV7.NewId(), RecordedGeneration, 1000)),
            "pi_page1b" => StripeReplay.List(hasMore: true, StripeReplay.Intent("pi_page2", UuidV7.NewId(), RecordedGeneration, 1000)),
            "pi_page2" => StripeReplay.List(hasMore: false, StripeReplay.Intent("pi_wanted", operation, RecordedGeneration, 1000)),
            var other => throw new InvalidOperationException("Unexpected page " + other),
        };

        (await h.Processor.LookupAsync(h.Request(operation, 10m, PaymentMethods.Card), Ct)).ShouldBe(new ProcessorResult("Accepted", "pi_wanted"));

        h.Stripe.Requests.Select(x => x.Query.GetValueOrDefault("starting_after")).ShouldBe([null, "pi_page1b", "pi_page2"]);
        h.Stripe.Requests.ShouldAllBe(x => x.Query["limit"] == "100" && x.Query["created[gte]"] == h.Stripe.Requests[0].Query["created[gte]"]);
    }

    [Fact]
    public async Task A_list_that_never_ends_is_a_failure_and_not_an_answer()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.List(hasMore: true, StripeReplay.Intent("pi_" + Guid.NewGuid().ToString("N"), UuidV7.NewId(), RecordedGeneration, 1000));

        // Neither "absent", which would let the charge be made again, nor a verdict on the payment.
        var thrown = await Should.ThrowAsync<IOException>(() => h.Processor.LookupAsync(h.Request(UuidV7.NewId(), 10m, PaymentMethods.Card), Ct));
        thrown.ShouldNotBeNull();
        h.Stripe.Requests.Count.ShouldBe(100);
    }

    [Fact]
    public async Task A_lookup_that_finds_nothing_says_absent()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded("055-recovery-list.json");
        (await h.Processor.LookupAsync(h.Request(UuidV7.NewId(), 515.24m, PaymentMethods.Card), Ct)).ShouldBe(new ProcessorResult("Absent", null));

        h.Stripe.Answer = _ => StripeReplay.List(hasMore: false);
        (await h.Processor.LookupAsync(h.Request(UuidV7.NewId(), 515.24m, PaymentMethods.Card), Ct)).ShouldBe(new ProcessorResult("Absent", null));
    }

    // Each differs from the payment that is found, above, by one thing.
    [Theory]
    [InlineData("generation")]
    [InlineData("no generation")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("two")]
    public async Task A_lookup_that_finds_something_else_under_the_operation_is_a_conflict(string difference)
    {
        var h = new Adapter();
        var operation = UuidV7.NewId();
        var found = StripeReplay.Intent("pi_found", operation, difference == "generation" ? Guid.NewGuid() : RecordedGeneration,
            difference == "amount" ? 1001 : 1000, difference == "currency" ? "eur" : "usd");
        if (difference == "no generation") { found["metadata"]!.AsObject().Remove("leasebook_generation"); }
        var others = difference == "two" ? new[] { StripeReplay.Intent("pi_again", operation, RecordedGeneration, 1000) } : [];
        h.Stripe.Answer = _ => StripeReplay.List(hasMore: false, [found, .. others]);

        await Should.ThrowAsync<PaymentConflictException>(() => h.Processor.LookupAsync(h.Request(operation, 10m, PaymentMethods.Card), Ct));

        // And with nothing different it is found, so each refusal above is its difference's doing.
        h.Stripe.Answer = _ => StripeReplay.List(hasMore: false, StripeReplay.Intent("pi_found", operation, RecordedGeneration, 1000));
        (await h.Processor.LookupAsync(h.Request(operation, 10m, PaymentMethods.Card), Ct)).ProviderId.ShouldBe("pi_found");
    }

    [Fact]
    public async Task An_operation_older_than_the_idempotency_key_is_never_submitted_again()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded("003-card-create.json");
        var request = h.Request(CardOperation, 515.24m, PaymentMethods.Card);

        // Within the key's life the repeat is safe: Stripe answers it with the first payment.
        h.Clock.Advance(TimeSpan.FromHours(23));
        (await h.Processor.SubmitAsync(request, Ct)).ProviderId.ShouldBe("pi_synthetic000001");
        h.Stripe.Requests.Count.ShouldBe(1);

        // Past it Stripe may have forgotten the key and would charge again, so it is not asked.
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await Should.ThrowAsync<PaymentConflictException>(() => h.Processor.SubmitAsync(request, Ct));
        h.Stripe.Requests.Count.ShouldBe(1);

        // It can still be found: looking does not charge.
        h.Stripe.Answer = _ => StripeReplay.Recorded("055-recovery-list.json");
        (await h.Processor.LookupAsync(request, Ct)).ProviderId.ShouldBe("pi_synthetic000001");
    }

    [Fact]
    public async Task A_live_object_is_refused_and_its_id_is_not_kept()
    {
        var h = new Adapter();
        var operation = UuidV7.NewId();
        var request = h.Request(operation, 10m, PaymentMethods.Card);
        var live = StripeReplay.Intent("pi_live", operation, RecordedGeneration, 1000);
        live["livemode"] = true;

        h.Stripe.Answer = _ => (200, live.ToJsonString());
        await Should.ThrowAsync<PaymentUnavailableException>(() => h.Processor.SubmitAsync(request, Ct));

        h.Stripe.Answer = _ => (402, new JsonObject { ["error"] = new JsonObject { ["type"] = "card_error", ["payment_intent"] = live.DeepClone() } }.ToJsonString());
        await Should.ThrowAsync<PaymentUnavailableException>(() => h.Processor.SubmitAsync(request, Ct));

        // Anywhere in a list, not only on the payment looked for: the key is reading a live account.
        var liveOther = StripeReplay.Intent("pi_other", UuidV7.NewId(), RecordedGeneration, 1000);
        liveOther["livemode"] = true;
        foreach (var listed in new[] { live, liveOther })
        {
            h.Stripe.Answer = _ => StripeReplay.List(hasMore: false, (JsonObject)listed.DeepClone());
            await Should.ThrowAsync<PaymentUnavailableException>(() => h.Processor.LookupAsync(request, Ct));
        }

        // The same payment in test mode is accepted, so the refusals above are the live flag's doing.
        live["livemode"] = false;
        h.Stripe.Answer = _ => (200, live.ToJsonString());
        (await h.Processor.SubmitAsync(request, Ct)).ProviderId.ShouldBe("pi_live");
    }

    [Theory]
    [InlineData("10.001", "USD", "card")]
    [InlineData("0", "USD", "card")]
    [InlineData("-5", "USD", "ach")]
    [InlineData("10", "EUR", "card")]
    [InlineData("10", "usd", "card")]
    [InlineData("10", "USD", "wire")]
    public async Task An_amount_that_is_not_whole_cents_of_dollars_is_refused_before_anything_is_sent(string amount, string currency, string method)
    {
        var h = new Adapter();
        var request = h.Request(UuidV7.NewId(), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), method) with { Currency = currency };

        await Should.ThrowAsync<PaymentConflictException>(() => h.Processor.SubmitAsync(request, Ct));
        if (method != "wire") { await Should.ThrowAsync<PaymentConflictException>(() => h.Processor.LookupAsync(request, Ct)); }

        h.Stripe.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0.01", "1")]
    [InlineData("515.24", "51524")]
    [InlineData("1000.00", "100000")]
    [InlineData("999999999999.99", "99999999999999")]
    public async Task An_amount_is_sent_in_exact_cents(string amount, string cents)
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded("003-card-create.json");

        await h.Processor.SubmitAsync(h.Request(UuidV7.NewId(), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), PaymentMethods.Card), Ct);

        h.Stripe.Requests.ShouldHaveSingleItem().Form["amount"].ShouldBe(cents);
    }

    [Fact]
    public async Task A_binding_that_is_not_the_hosts_own_reaches_nothing()
    {
        var h = new Adapter();
        var request = h.Request(UuidV7.NewId(), 10m, PaymentMethods.Card);

        foreach (var foreign in new[]
        {
            request with { Binding = h.Binding with { Generation = Guid.NewGuid() } },
            request with { Binding = h.Binding with { Account = "acct_other" } },
            request with { Binding = h.Binding with { Mode = PaymentModes.Simulation } },
            request with { Binding = h.Binding with { CardPaymentMethod = "pm_1RealSavedMethod" } },
            request with { Binding = h.Binding with { OrgId = Guid.NewGuid() } },
        })
        {
            await Should.ThrowAsync<PaymentUnavailableException>(() => h.Processor.SubmitAsync(foreign, Ct));
            await Should.ThrowAsync<PaymentUnavailableException>(() => h.Processor.LookupAsync(foreign, Ct));
        }

        h.Stripe.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_request_names_a_payer_or_the_payment_method_types()
    {
        var h = new Adapter();
        h.Stripe.Answer = sent => sent.Method == "GET" ? StripeReplay.Recorded("055-recovery-list.json")
            : StripeReplay.Recorded(sent.Form.ContainsKey("mandate_data[customer_acceptance][type]") ? "019-ach-create.json" : "003-card-create.json");
        foreach (var method in PaymentMethods.All)
        {
            var request = h.Request(UuidV7.NewId(), 515.24m, method);
            await h.Processor.LookupAsync(request, Ct);
            await h.Processor.SubmitAsync(request, Ct);
        }

        h.Stripe.Requests.Count.ShouldBe(4);
        var forbidden = new[] { "payment_method_types", "customer", "email", "description", "statement_descriptor", "shipping", "billing", "name", "phone", "address", "on_behalf_of", "application_fee" };
        // The one field that says "customer" is the offline mandate's fixed word for who accepted it.
        foreach (var name in h.Stripe.Requests.SelectMany(x => x.Form.Keys.Concat(x.Query.Keys)).Where(x => x != "mandate_data[customer_acceptance][type]"))
        { forbidden.ShouldAllBe(word => !name.Contains(word, StringComparison.OrdinalIgnoreCase), name); }
        // Nor in a value: what is sent is an amount, a currency, a test token, flags and two ids of LeaseBook's own.
        h.Stripe.Requests.SelectMany(x => x.Form.Values).ShouldAllBe(value => !value.Contains('@') && !value.Contains(' '));
    }

    [Fact]
    public async Task Callbacks_are_not_read_yet()
    {
        var h = new Adapter();
        var body = Encoding.UTF8.GetBytes("{}");

        h.Processor.Authenticate(body, "t=1,v1=00").ShouldBeNull();
        (await h.Processor.ReadObservationAsync(new ProcessorNotice(body), Ct)).ShouldBe(ProcessorRead<ProcessorObservation>.NotOurs);
        (await h.Processor.ReadSettlementAsync(new ProcessorNotice(body), Ct)).ShouldBe(ProcessorRead<ProcessorSettlement>.NotOurs);
        h.Stripe.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void The_adapter_exists_only_in_a_sandbox_host()
    {
        var simulation = SimulationSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Mode"] = "Simulation",
            ["Payments:SigningKey"] = new string('x', 64),
            ["Payments:Fixtures:0:OrgId"] = Guid.NewGuid().ToString(),
            ["Payments:Fixtures:0:Generation"] = Guid.NewGuid().ToString(),
            ["Payments:Fixtures:0:BankId"] = Guid.NewGuid().ToString(),
            ["Payments:Fixtures:0:Account"] = "sim_fixture",
        }).Build(), new DevelopmentEnvironment());

        Should.Throw<InvalidOperationException>(() => new StripeSandboxProcessor(simulation, TimeProvider.System, new StripeTransport(new StripeReplay())));
    }

    // The startup barrier (ADR-054): the key is the platform's, and each fixture's account is a test
    // account of that platform which pays its own Stripe fees. Admitted with what the probe recorded;
    // each refusal differs from that by one thing.
    [Fact]
    public async Task The_platforms_test_key_and_a_connected_account_that_pays_its_own_fees_are_admitted()
    {
        var h = new Adapter();
        h.Stripe.Answer = Accounts();

        await h.Processor.RequirePlatformKeyAsync(Ct);

        h.Stripe.Requests.Select(x => (x.Method, x.Path)).ShouldBe([("GET", "/v1/account"), ("GET", "/v1/accounts/acct_synthetic000002")]);
        // Asked as the platform: on behalf of nobody, and changing nothing.
        h.Stripe.Requests.ShouldAllBe(x => !x.Headers.ContainsKey("Stripe-Account") && !x.Headers.ContainsKey("Idempotency-Key"));
    }

    // Two of these are not proof of a live barrier. An account carries no "livemode" member, so no
    // real answer can trip them: they pin a defensive read of a shape Stripe does not send today.
    // What keeps a live key out is its prefix, refused in SimulationSettings before a client exists.
    [Theory]
    [InlineData("own key")]
    [InlineData("platform marked live, which Stripe does not send")]
    [InlineData("platform unreadable")]
    [InlineData("account marked live, which Stripe does not send")]
    [InlineData("application pays fees")]
    [InlineData("no fee payer")]
    [InlineData("another account")]
    [InlineData("account unreadable")]
    [InlineData("account unreachable")]
    public async Task A_key_or_an_account_that_is_not_the_platforms_sandbox_is_refused_at_startup(string difference)
    {
        const string key = "sk_test_abc";
        var h = new Adapter();
        var refused = (403, """{"error":{"type":"invalid_request_error","message":"The provided key 'sk_test_***abc' does not have access to this account."}}""");
        h.Stripe.Answer = Accounts(
            platform: body =>
            {
                // A connected account's own key reads that account as its own.
                if (difference == "own key") { body["id"] = "acct_synthetic000002"; }
                if (difference.StartsWith("platform marked live")) { body["livemode"] = true; }
            },
            connected: body =>
            {
                if (difference.StartsWith("account marked live")) { body["livemode"] = true; }
                if (difference == "application pays fees") { body["controller"]!["fees"]!["payer"] = "application"; }
                if (difference == "no fee payer") { body["controller"]!["fees"] = null; }
                if (difference == "another account") { body["id"] = "acct_synthetic000009"; }
            },
            platformAnswer: difference == "platform unreadable" ? refused : null,
            connectedAnswer: difference == "account unreadable" ? refused : null,
            connectedFault: difference == "account unreachable");

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => h.Processor.RequirePlatformKeyAsync(Ct));

        // Never the key, in whole or as Stripe abbreviates it; a fixture is named by its organization.
        refusal.Message.ShouldNotContain(key);
        refusal.Message.ShouldNotContain("sk_test");
        refusal.Message.ShouldNotContain("abc");
        refusal.InnerException.ShouldBeNull();
        if (difference != "own key" && !difference.StartsWith("platform"))
        { refusal.Message.ShouldContain(h.Binding.OrgId.ToString()); }
        // A key that is not the platform's is refused before any fixture's account is read with it.
        else { h.Stripe.Requests.ShouldHaveSingleItem().Path.ShouldBe("/v1/account"); }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Error(string type, string? code) =>
        new JsonObject { ["error"] = new JsonObject { ["type"] = type, ["code"] = code, ["message"] = "Refused." } }.ToJsonString();

    /// <summary>Answers the two account reads from the recordings, each changed as a test asks.</summary>
    internal static Func<StripeReplay.Sent, (int Status, string Body)> Accounts(Action<JsonObject>? platform = null,
        Action<JsonObject>? connected = null, (int, string)? platformAnswer = null, (int, string)? connectedAnswer = null,
        bool connectedFault = false, string account = "acct_synthetic000002") => sent =>
    {
        if (sent.Path == "/v1/account")
        {
            if (platformAnswer is { } answer) { return answer; }
            var body = StripeReplay.RecordedBody("001-platform-account.json");
            platform?.Invoke(body);
            return (200, body.ToJsonString());
        }
        if (sent.Path == "/v1/accounts/" + account)
        {
            if (connectedFault) { throw new HttpRequestException("No route to the sandbox."); }
            if (connectedAnswer is { } answer) { return answer; }
            var body = StripeReplay.RecordedBody("002-connected-account.json");
            body["id"] = account;
            connected?.Invoke(body);
            return (200, body.ToJsonString());
        }
        throw new InvalidOperationException("Unexpected request " + sent.Path);
    };

    internal sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    internal sealed class Clock(DateTimeOffset? start = null) : TimeProvider
    {
        private DateTimeOffset _now = start ?? new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    // The adapter alone: a sandbox host's settings, a clock the test moves, and the replaying transport.
    private sealed class Adapter
    {
        private readonly DateTime _created;

        public Adapter(params (string Key, string Value)[] changes)
        {
            var values = new Dictionary<string, string?>
            {
                ["Payments:Mode"] = "StripeSandbox",
                ["Payments:SigningKey"] = new string('x', 64),
                ["Payments:Stripe:SecretKey"] = "sk_test_abc",
                ["Payments:Fixtures:0:OrgId"] = Guid.NewGuid().ToString(),
                ["Payments:Fixtures:0:Generation"] = RecordedGeneration.ToString(),
                ["Payments:Fixtures:0:BankId"] = Guid.NewGuid().ToString(),
                ["Payments:Fixtures:0:Account"] = "acct_synthetic000002",
            };
            foreach (var (key, value) in changes) { values[key] = value; }
            var settings = SimulationSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new DevelopmentEnvironment());
            Binding = settings.Fixtures.Single();
            _created = Clock.GetUtcNow().UtcDateTime;
            Processor = new StripeSandboxProcessor(settings, Clock, new StripeTransport(Stripe));
        }

        public StripeReplay Stripe { get; } = new();
        public Clock Clock { get; } = new();
        public FixtureBinding Binding { get; }
        public StripeSandboxProcessor Processor { get; }

        public ProcessorRequest Request(Guid operation, decimal amount, string method) =>
            new(Binding, operation, "fingerprint", amount, "USD", method, _created);
    }
}

/// <summary>
/// Stands where the network would be. Keeps every request it was sent and answers from a payload
/// the probe recorded, or from what a test built. Anything a test did not expect throws.
/// </summary>
internal sealed class StripeReplay : HttpMessageHandler
{
    private const string Run = "2026-10-08T21-56-17-265Z";
    private static readonly Lazy<string> Directory = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LeaseBook.slnx"))) { directory = directory.Parent; }
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("LeaseBook.slnx not found above the test base directory."),
            "tests", "fixtures", "stripe", Run);
    });
    private readonly Lock _gate = new();
    private readonly List<Sent> _requests = [];

    public sealed record Sent(string Method, string Path, IReadOnlyDictionary<string, string> Query,
        IReadOnlyDictionary<string, string> Form, IReadOnlyDictionary<string, string> Headers);

    public IReadOnlyList<Sent> Requests { get { lock (_gate) { return [.. _requests]; } } }
    public Func<Sent, (int Status, string Body)> Answer { get; set; } = sent => throw new InvalidOperationException("Unexpected request " + sent.Path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri!.Host.ShouldBe("api.stripe.com");
        var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new Sent(request.Method.Method, request.RequestUri.AbsolutePath, Pairs(request.RequestUri.Query), Pairs(form),
            request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase));
        lock (_gate) { _requests.Add(sent); }
        var (status, body) = Answer(sent);
        return new HttpResponseMessage((HttpStatusCode)status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static Dictionary<string, string> Pairs(string encoded) =>
        QueryHelpers.ParseQuery(encoded).ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.Ordinal);

    private static JsonObject File_(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Directory.Value, name)))!.AsObject();

    /// <summary>A recorded exchange's answer: its status and its body, as the sandbox sent them.</summary>
    public static (int Status, string Body) Recorded(string name)
    {
        var file = File_(name);
        return (file["status"]!.GetValue<int>(), file["body"]!.ToJsonString());
    }

    public static JsonObject RecordedBody(string name) => File_(name)["body"]!.AsObject();

    /// <summary>What a recorded request sent, flattened the way a form names nested fields.</summary>
    public static Dictionary<string, string> RecordedForm(string name)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, JsonNode? node)
        {
            if (node is JsonObject nested) { foreach (var (child, value) in nested) { Add($"{key}[{child}]", value); } }
            else { form[key] = node!.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? node.ToJsonString() : node.ToString(); }
        }
        foreach (var (key, value) in File_(name)["request"]!["params"]!.AsObject()) { Add(key, value); }
        return form;
    }

    /// <summary>One payment as the recorded list holds them, with the fields a lookup reads set.</summary>
    public static JsonObject Intent(string id, Guid operation, Guid generation, long amount, string currency = "usd")
    {
        var intent = (JsonObject)RecordedBody("055-recovery-list.json")["data"]![0]!.DeepClone();
        intent["id"] = id;
        intent["amount"] = amount;
        intent["currency"] = currency;
        intent["metadata"] = new JsonObject { ["leasebook_operation"] = operation.ToString("D"), ["leasebook_generation"] = generation.ToString("D") };
        return intent;
    }

    public static (int Status, string Body) List(bool hasMore, params JsonObject[] intents) =>
        (200, new JsonObject { ["object"] = "list", ["has_more"] = hasMore, ["url"] = "/v1/payment_intents", ["data"] = new JsonArray([.. intents]) }.ToJsonString());
}

/// <summary>A whole host in StripeSandbox mode, with the recorded sandbox behind it.</summary>
[Collection(nameof(DatabaseCollection))]
public sealed class StripeSandboxHostTests(PostgresFixture fixture)
{
    private const string Account = "acct_synthetic000002";

    [Fact]
    public async Task A_sandbox_host_collects_a_payment_through_stripe_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var stripe = new StripeReplay();
        var accounts = StripeSandboxProcessorTests.Accounts();
        stripe.Answer = sent => sent.Path.StartsWith("/v1/account", StringComparison.Ordinal) ? accounts(sent)
            : sent.Method == "GET" ? StripeReplay.List(hasMore: false) : StripeReplay.Recorded("019-ach-create.json");
        await using var h = await Setup(stripe, ct);

        // The host started, having asked whose key it holds and whether the account is the fixture's.
        h.App.Services.GetRequiredService<IPaymentProcessor>().ShouldBeOfType<StripeSandboxProcessor>();
        h.App.Services.GetService<SimulatedProcessor>().ShouldBeNull();
        stripe.Requests.Select(x => x.Path).ShouldBe(["/v1/account", "/v1/accounts/" + Account]);

        using var tenant = h.App.CreateClient();
        await tenant.PrimeCsrfAsync(ct);
        (await tenant.PostAsJsonAsync("/api/auth/login", new { email = $"tenant-{h.Suffix}1@payments.test", password = PaymentFixtureBootstrap.Password }, ct)).EnsureSuccessStatusCode();
        await tenant.PrimeCsrfAsync(ct);
        var entries = await h.JournalEntries(ct);
        var response = await tenant.PostAsJsonAsync("/api/portal/tenant/payments", new SubmitPaymentBody(UuidV7.NewId(), 504.03m, "USD"), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(ct));
        var payment = (await response.Content.ReadFromJsonAsync<PaymentView>(ct))!;

        await h.App.Services.GetRequiredService<PaymentRunner>().RunOnceAsync(ct);

        // Looked for, not found, and so made: once, for this operation, on the fixture's account.
        var sent = stripe.Requests.Skip(2).ToArray();
        sent.Select(x => (x.Method, x.Path)).ShouldBe([("GET", "/v1/payment_intents"), ("POST", "/v1/payment_intents")]);
        sent.ShouldAllBe(x => x.Headers["Stripe-Account"] == Account);
        sent[1].Headers["Idempotency-Key"].ShouldBe(payment.Id.ToString("D"));
        sent[1].Form["amount"].ShouldBe("50403");
        sent[1].Form["metadata[leasebook_operation]"].ShouldBe(payment.Id.ToString("D"));
        sent[1].Form["metadata[leasebook_generation]"].ShouldBe(h.Binding.Generation.ToString("D"));
        await h.InOrg(async sp =>
        {
            var operation = await sp.GetRequiredService<AppDbContext>().Set<PaymentOperation>().SingleAsync(x => x.Id == payment.Id, ct);
            (operation.ProviderId, operation.Status, operation.JournalId).ShouldBe(("pi_synthetic000004", "Processing", null));
        }, ct);
        // Stripe holding the collection is not money at the bank: nothing is posted for it.
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // The simulator's drivers have no simulator to drive here, and say so instead of failing on it.
        (await Cli(h.App.Services, ct, "emit", h.Binding.OrgId.ToString(), payment.Id.ToString(), "BankCredit", "2026-10-08")).ShouldBe(1);
        (await Cli(h.App.Services, ct, "payout", h.Binding.OrgId.ToString(), "2026-10-08", "po_1", "pay:" + payment.Id)).ShouldBe(1);
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // A foreground step proves the key again before it collects, then finds nothing due.
        var before = stripe.Requests.Count;
        (await Cli(h.App.Services, ct, "step")).ShouldBe(0);
        stripe.Requests.Skip(before).Select(x => x.Path).ShouldBe(["/v1/account", "/v1/accounts/" + Account]);
    }

    [Fact]
    public async Task A_sandbox_host_refuses_to_start_with_a_connected_accounts_own_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var stripe = new StripeReplay { Answer = StripeSandboxProcessorTests.Accounts(platform: body => body["id"] = Account) };

        // The whole host, not the check alone: a start that skipped it would serve with that key.
        var refusal = await Should.ThrowAsync<InvalidOperationException>(async () => { await using var h = await Setup(stripe, ct); });

        refusal.Message.ShouldContain("platform's test-mode key");
        stripe.Requests.ShouldHaveSingleItem().Path.ShouldBe("/v1/account");
    }

    // ADR-054, "An unknown result is found, never repeated", through the worker and not the adapter
    // alone: Stripe may have forgotten the idempotency key, and the list scan finds nothing.
    [Fact]
    public async Task After_the_key_may_be_forgotten_an_empty_scan_sends_the_payment_to_review_and_charges_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var stripe = new StripeReplay();
        var accounts = StripeSandboxProcessorTests.Accounts();
        stripe.Answer = sent => sent.Path.StartsWith("/v1/account", StringComparison.Ordinal) ? accounts(sent)
            : sent.Method == "GET" ? StripeReplay.List(hasMore: false) : StripeReplay.Recorded("019-ach-create.json");
        await using var h = await Setup(stripe, ct);
        var payment = await h.Submit(504.03m, ct);
        var entries = await h.JournalEntries(ct);
        var before = stripe.Requests.Count;

        // The worker did not reach it for a day: a host that was down, or a queue that was stuck.
        // A whole day, not a second past the limit: the operation's creation time is stamped from the
        // machine's clock while this host reads a stopped one, so the time the test has already
        // taken comes off the margin. On a slow run a one-second margin was gone and the charge was made.
        h.Clock.Advance(TimeSpan.FromHours(24));
        await h.App.Services.GetRequiredService<PaymentRunner>().RunOnceAsync(ct);

        // Looked for, and not made. A person reads it; nothing was charged and nothing posted.
        stripe.Requests.Skip(before).Select(x => (x.Method, x.Path)).ShouldBe([("GET", "/v1/payment_intents")]);
        stripe.Requests.ShouldAllBe(x => x.Method != "POST");
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.ProviderId, operation.JournalId).ShouldBe(("NeedsReview", "conflicting_evidence", null, null));
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // And it stays there: another pass asks Stripe nothing.
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.App.Services.GetRequiredService<PaymentRunner>().RunOnceAsync(ct);
        stripe.Requests.Count.ShouldBe(before + 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(48)]
    public async Task A_payment_the_scan_finds_takes_its_reference_and_is_not_charged_again(int hoursLater)
    {
        var ct = TestContext.Current.CancellationToken;
        var stripe = new StripeReplay();
        var accounts = StripeSandboxProcessorTests.Accounts();
        Guid? made = null;
        Guid generation = default;
        // The charge was made and its answer lost: Stripe lists it under this operation's id.
        stripe.Answer = sent => sent.Path.StartsWith("/v1/account", StringComparison.Ordinal) ? accounts(sent)
            : sent.Method == "GET" ? StripeReplay.List(hasMore: false, StripeReplay.Intent("pi_found_by_scan", made!.Value, generation, 50403))
            : throw new InvalidOperationException("A payment that exists must not be made again.");
        await using var h = await Setup(stripe, ct);
        generation = h.Binding.Generation;
        var payment = await h.Submit(504.03m, ct);
        made = payment.Id;
        var entries = await h.JournalEntries(ct);

        // Found whether or not Stripe still remembers the key: looking does not charge.
        h.Clock.Advance(TimeSpan.FromHours(hoursLater));
        await h.App.Services.GetRequiredService<PaymentRunner>().RunOnceAsync(ct);

        stripe.Requests.ShouldAllBe(x => x.Method != "POST");
        var operation = await h.Operation(payment.Id, ct);
        (operation.ProviderId, operation.Status, operation.Reason, operation.JournalId).ShouldBe(("pi_found_by_scan", "Processing", null, null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    private static Task<int> Cli(IServiceProvider services, CancellationToken ct, params string[] args)
    {
        new PaymentSimulationVerb().TryCreateInvocation(["payment-simulation", .. args], out var invocation, out var error).ShouldBeTrue(error);
        return invocation.RunAsync(services, ct);
    }

    private async Task<Harness> Setup(StripeReplay stripe, CancellationToken ct)
    {
        var binding = new FixtureBinding(UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId(), Account) { Mode = PaymentModes.StripeSandbox };
        var suffix = UuidV7.NewId().ToString("N");
        await PaymentFixtureBootstrap.SeedAsync(fixture.Api.Services, binding, suffix, ct);
        var clock = new StripeSandboxProcessorTests.Clock(DateTimeOffset.UtcNow);
        var factory = new ApiFactory(fixture.AppConnectionString, new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Payments:Mode"] = PaymentModes.StripeSandbox,
            ["Payments:SigningKey"] = new string('x', 64),
            ["Payments:Stripe:SecretKey"] = "sk_test_abc",
            ["Payments:Fixtures:0:OrgId"] = binding.OrgId.ToString(),
            ["Payments:Fixtures:0:Generation"] = binding.Generation.ToString(),
            ["Payments:Fixtures:0:BankId"] = binding.BankId.ToString(),
            ["Payments:Fixtures:0:Account"] = binding.Account,
        });
        var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService)
                && x.ImplementationType?.Name == "PaymentWorker").ToArray()) { services.Remove(service); }
            services.RemoveAll<StripeTransport>(); services.AddSingleton(new StripeTransport(stripe));
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        }));
        try { _ = app.Services; }
        catch { await app.DisposeAsync(); await factory.DisposeAsync(); throw; }
        return new Harness(factory, app, binding, suffix, clock);
    }

    private sealed class Harness(ApiFactory factory, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, FixtureBinding binding, string suffix, StripeSandboxProcessorTests.Clock clock) : IAsyncDisposable
    {
        public StripeSandboxProcessorTests.Clock Clock => clock;
        // As a tenant of the fixture would: signed in, through the portal's own route.
        public async Task<PaymentView> Submit(decimal amount, CancellationToken ct)
        {
            using var tenant = app.CreateClient();
            await tenant.PrimeCsrfAsync(ct);
            (await tenant.PostAsJsonAsync("/api/auth/login", new { email = $"tenant-{suffix}1@payments.test", password = PaymentFixtureBootstrap.Password }, ct)).EnsureSuccessStatusCode();
            await tenant.PrimeCsrfAsync(ct);
            var response = await tenant.PostAsJsonAsync("/api/portal/tenant/payments", new SubmitPaymentBody(UuidV7.NewId(), amount, "USD"), ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(ct));
            return (await response.Content.ReadFromJsonAsync<PaymentView>(ct))!;
        }
        public async Task<PaymentOperation> Operation(Guid id, CancellationToken ct)
        {
            PaymentOperation? operation = null;
            await InOrg(async sp => operation = await sp.GetRequiredService<AppDbContext>().Set<PaymentOperation>().AsNoTracking().SingleAsync(x => x.Id == id, ct), ct);
            return operation!;
        }
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App => app;
        public FixtureBinding Binding => binding;
        public string Suffix => suffix;
        public async Task InOrg(Func<IServiceProvider, Task> work, CancellationToken ct)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(binding.OrgId, "test:payments", () => work(scope.ServiceProvider), ct);
        }
        public async Task<int> JournalEntries(CancellationToken ct)
        {
            var count = 0;
            await InOrg(async sp => count = await sp.GetRequiredService<AppDbContext>().Set<JournalEntry>().CountAsync(ct), ct);
            return count;
        }
        public async ValueTask DisposeAsync() { await app.DisposeAsync(); await factory.DisposeAsync(); }
    }
}
