using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Stripe;

namespace LeaseBook.Tests.Integration;

// Asking Stripe again for the events of an account (ADR-054, #513): what the adapter sends, and what
// it makes of the answer. The list replayed is the one the probe recorded, of the same forty events
// it recorded as deliveries, so each event here was also seen the other way.
public sealed partial class StripeSandboxProcessorTests
{
    private const string RecordedEvents = "064-events-api.json";
    private static readonly DateTime Since = new(2026, 10, 8, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_sweep_lists_the_accounts_events_since_a_time_and_only_of_the_types_it_reads()
    {
        var h = new Adapter();
        h.Stripe.Answer = RecordedList;

        await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        var sent = h.Stripe.Requests[0];
        h.Stripe.Requests.Count(x => x.Path == "/v1/events").ShouldBe(1);
        (sent.Method, sent.Path).ShouldBe(("GET", "/v1/events"));
        // On the connected account, with the platform's key. A read: nothing to make idempotent.
        sent.Headers["Stripe-Account"].ShouldBe(h.Binding.Account);
        sent.Headers["Authorization"].ShouldBe("Bearer sk_test_abc");
        sent.Headers.ShouldNotContainKey("Idempotency-Key");
        // From five minutes before, for a clock that is not Stripe's, as a lookup's list is.
        sent.Query["created[gte]"].ShouldBe((new DateTimeOffset(Since).ToUnixTimeSeconds() - 300).ToString());
        sent.Query["limit"].ShouldBe("100");
        // Exactly the types the reader maps, each once. Stripe takes at most twenty in one filter.
        sent.Query.Where(x => x.Key.StartsWith("types[", StringComparison.Ordinal)).Select(x => x.Value).ShouldBe(TypesRead, ignoreOrder: true);
        sent.Query.Count.ShouldBe(2 + TypesRead.Length);
        TypesRead.Length.ShouldBe(7);
        sent.Query.Keys.Where(x => x.StartsWith("types[", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .ShouldBe(Enumerable.Range(0, TypesRead.Length).Select(i => $"types[{i}]"));

        // The request is written out by hand, because the answer is wanted as text. Stripe's own
        // library, asked for the same list, sends the same thing: the names are not this code's guess.
        var library = new StripeReplay { Answer = _ => StripeReplay.Recorded(RecordedEvents) };
        using var http = new HttpClient(library);
        var client = new StripeClient("sk_test_abc", httpClient: new SystemNetHttpClient(http, maxNetworkRetries: 0, enableTelemetry: false));
        // Only what it sends is wanted. Whether it can read the answer is the reason it is not used.
        await Record.ExceptionAsync(() => client.V1.Events.ListAsync(new EventListOptions
        {
            Created = new DateRangeOptions { GreaterThanOrEqual = Since.AddMinutes(-5) },
            Limit = 100,
            Types = [.. sent.Query.Where(x => x.Key.StartsWith("types[", StringComparison.Ordinal)).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value)],
        }, new RequestOptions { StripeAccount = h.Binding.Account }, Ct));
        var theirs = library.Requests.ShouldHaveSingleItem();
        (theirs.Method, theirs.Path).ShouldBe((sent.Method, sent.Path));
        sent.Query.ShouldBe(theirs.Query, ignoreOrder: true);
        theirs.Headers["Stripe-Account"].ShouldBe(sent.Headers["Stripe-Account"]);
    }

    // The proof that a swept event and a delivered one are read by the same code: every recorded
    // delivery that is read, a payment's progress or its dispute, read as the route reads it, is what
    // the sweep makes of the same event in the recorded list. Nothing in either recording is changed.
    // A dispute is read by asking Stripe, and the sweep asks what the delivery asked.
    [Fact]
    public async Task The_recorded_list_is_read_exactly_as_the_same_events_were_read_when_delivered()
    {
        var h = new Adapter();
        h.Stripe.Answer = RecordedList;
        var delivered = new List<ProcessorObservation>();
        foreach (var file in Progress.Select(x => x.File).Concat(AchDisputeEvents.Split(',')))
        { delivered.Add((await Read(h, StripeReplay.Delivered(file))).Value.ShouldNotBeNull()); }

        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        swept.Observations.Count.ShouldBe(12);
        swept.Observations.ShouldBe(delivered, ignoreOrder: true);
        swept.Observations.Count(x => x.Kind == "Return").ShouldBe(3);
        // Equal as stored, which is what makes the second of the two a duplicate and not a conflict.
        swept.Observations.Select(x => PaymentEngine.Hash(System.Text.Json.JsonSerializer.Serialize(x)))
            .ShouldBe(delivered.Select(x => PaymentEngine.Hash(System.Text.Json.JsonSerializer.Serialize(x))), ignoreOrder: true);
        // The probe asked for every type: the other twenty-eight are listed and are not read.
        (swept.Listed, swept.Unreadable).ShouldBe((40, 0));
        StripeReplay.RecordedBody(RecordedEvents)["data"]!.AsArray().Select(x => (string)x!["id"]!)
            .ShouldBe(StripeReplay.Deliveries().Select(x => (string)x.Event["id"]!), ignoreOrder: true);
    }

    [Fact]
    public async Task A_sweep_follows_the_list_to_its_end()
    {
        var h = new Adapter();
        // The first page ends on an event of the dispute, which is read by asking for its payment and charge.
        h.Stripe.Answer = sent => sent.Path != "/v1/events" ? AchDispute()(sent) : sent.Query.GetValueOrDefault("starting_after") switch
        {
            null => Page(true, Listed("evt_synthetic000035"), Listed("evt_synthetic000040")),
            "evt_synthetic000040" => Page(true, Listed("evt_synthetic000026")),
            "evt_synthetic000026" => Page(false, Listed("evt_synthetic000002")),
            var other => throw new InvalidOperationException("Unexpected page " + other),
        };

        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        swept.Observations.Select(x => x.EventId).ShouldBe(["evt_synthetic000035", "evt_synthetic000040", "evt_synthetic000026", "evt_synthetic000002"]);
        (swept.Listed, swept.Unreadable).ShouldBe((4, 0));
        var pages = h.Stripe.Requests.Where(x => x.Path == "/v1/events").ToArray();
        pages.Select(x => x.Query.GetValueOrDefault("starting_after")).ShouldBe([null, "evt_synthetic000040", "evt_synthetic000026"]);
        // Every page is the same question, but for where it starts.
        pages.ShouldAllBe(x => x.Query.Where(q => q.Key != "starting_after").OrderBy(q => q.Key)
            .SequenceEqual(pages[0].Query.OrderBy(q => q.Key)));
    }

    [Fact]
    public async Task A_list_of_events_that_never_ends_is_a_failure_and_not_a_shorter_answer()
    {
        var h = new Adapter();
        var page = 0;
        h.Stripe.Answer = _ =>
        {
            var listed = Listed("evt_synthetic000002");
            listed["id"] = "evt_page" + page++;
            return Page(true, listed);
        };

        // Not what was read so far: a caller takes an answer to mean that everything was asked for.
        await Should.ThrowAsync<IOException>(() => h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct));

        h.Stripe.Requests.Count.ShouldBe(100);
    }

    // As for a lookup's list: anywhere in it, whatever it is about. The key is reading a live account.
    [Theory]
    [InlineData("an event this step reads")]
    [InlineData("an event this step does not read")]
    [InlineData("an event for another account")]
    [InlineData("the payment inside an event")]
    [InlineData("on the second page")]
    public async Task Anything_marked_live_in_the_list_refuses_the_whole_sweep(string where)
    {
        var h = new Adapter();
        var live = Listed(where == "an event this step does not read" ? "evt_synthetic000001" : "evt_synthetic000002");
        if (where == "the payment inside an event") { live["data"]!["object"]!["livemode"] = true; } else { live["livemode"] = true; }
        if (where == "an event for another account") { live["account"] = "acct_synthetic000009"; }
        h.Stripe.Answer = sent => where == "on the second page" && !sent.Query.ContainsKey("starting_after")
            ? Page(true, Listed("evt_synthetic000035")) : Page(false, Listed("evt_synthetic000026"), live);

        // Nothing of it is kept, the events before the live one included.
        await Should.ThrowAsync<PaymentUnavailableException>(() => h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct));

        // The same list in test mode is read, so the refusal is the live flag's doing.
        live["livemode"] = false;
        live["data"]!["object"]!["livemode"] = false;
        (await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct)).Observations.ShouldNotBeEmpty();
    }

    // Two fixtures on one host. A list asked of one account that holds an event naming the other is
    // not evidence about the other: that account was not the one asked.
    [Fact]
    public async Task An_event_naming_another_account_is_not_taken_even_when_that_account_is_bound_here()
    {
        var otherGeneration = Guid.NewGuid();
        var h = new Adapter(("Payments:Fixtures:1:OrgId", Guid.NewGuid().ToString()), ("Payments:Fixtures:1:Generation", otherGeneration.ToString()),
            ("Payments:Fixtures:1:BankId", Guid.NewGuid().ToString()), ("Payments:Fixtures:1:Account", "acct_synthetic000009"));
        var other = h.Settings.Fixtures[1];
        var elsewhere = Listed("evt_synthetic000026");
        elsewhere["account"] = other.Account;
        elsewhere["data"]!["object"]!["metadata"]!["leasebook_generation"] = otherGeneration.ToString("D");
        h.Stripe.Answer = _ => Page(false, elsewhere, Listed("evt_synthetic000002"));

        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        swept.Observations.ShouldHaveSingleItem().EventId.ShouldBe("evt_synthetic000002");
        (swept.Listed, swept.Unreadable).ShouldBe((2, 0));

        // Asked of its own account, the same event is read: only the account asked made it not count.
        var theirs = await h.Processor.RecoverObservationsAsync(other, Since, Ct);
        var taken = theirs.Observations.ShouldHaveSingleItem();
        (taken.EventId, taken.Account, taken.Generation, taken.BankId).ShouldBe(("evt_synthetic000026", other.Account, otherGeneration, other.BankId));
        h.Stripe.Requests.Select(x => x.Headers["Stripe-Account"]).ShouldBe([h.Binding.Account, other.Account]);
    }

    // The account also holds the probe's payments and those of older fixture generations.
    [Fact]
    public async Task Events_of_the_probe_or_of_another_generation_are_listed_and_not_taken()
    {
        var h = new Adapter(("Payments:Fixtures:0:Generation", "0199e6a1-0000-7000-8000-000000000001"));
        var probes = Listed("evt_synthetic000011");
        probes["data"]!["object"]!["metadata"] = new JsonObject();
        h.Stripe.Answer = sent => sent.Path != "/v1/events" ? AchDispute()(sent)
            : Page(false, [.. StripeReplay.RecordedBody(RecordedEvents)["data"]!.AsArray().Select(x => x!.DeepClone()), probes]);

        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        swept.Observations.ShouldBeEmpty();
        // Not this host's, which is not the same as unreadable.
        (swept.Listed, swept.Unreadable).ShouldBe((41, 0));
        // The dispute's payment was asked for, to learn that: once for its three events, and its charge never.
        h.Stripe.Requests.Select(x => x.Path).ShouldBe(["/v1/events", DisputedPayment]);
    }

    [Fact]
    public async Task An_event_that_cannot_be_read_is_counted_and_does_not_stop_the_rest()
    {
        var h = new Adapter();
        var wrongAmount = Listed("evt_synthetic000035");
        wrongAmount["data"]!["object"]!["amount"] = "20000";
        var noFlag = Listed("evt_synthetic000026");
        noFlag.Remove("livemode");
        var twice = """{"id":"evt_twice","id":"evt_twice","livemode":false}""";
        h.Stripe.Answer = _ => (200, $$"""{"object":"list","has_more":false,"data":[{{wrongAmount.ToJsonString()}},7,{{noFlag.ToJsonString()}},{{twice}},null,{{Listed("evt_synthetic000002").ToJsonString()}}]}""");

        var swept = await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct);

        swept.Observations.ShouldHaveSingleItem().EventId.ShouldBe("evt_synthetic000002");
        (swept.Listed, swept.Unreadable).ShouldBe((6, 5));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("not an object")]
    [InlineData("no data")]
    [InlineData("data not a list")]
    [InlineData("no has_more")]
    [InlineData("more, after an event with no id")]
    [InlineData("more, and nothing listed")]
    public async Task An_answer_that_is_not_a_list_of_events_is_a_failure(string difference)
    {
        var h = new Adapter();
        var listed = Listed("evt_synthetic000002").ToJsonString();
        h.Stripe.Answer = _ => (200, difference switch
        {
            "not json" => "<html>",
            "not an object" => $"[{listed}]",
            "no data" => """{"object":"list","has_more":false}""",
            "data not a list" => $$"""{"object":"list","has_more":false,"data":{{listed}}}""",
            "no has_more" => $$"""{"object":"list","data":[{{listed}}]}""",
            // Not the end of the list, whatever a lookup makes of it: a sweep's answer is taken as complete.
            "more, and nothing listed" => """{"object":"list","has_more":true,"data":[]}""",
            _ => """{"object":"list","has_more":true,"data":[{"livemode":false}]}""",
        });

        await Should.ThrowAsync<IOException>(() => h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct));

        h.Stripe.Answer = _ => Page(false, Listed("evt_synthetic000002"));
        (await h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct)).Observations.ShouldHaveSingleItem();
    }

    // A read says nothing about any payment, so whatever Stripe answers but a list is left as thrown.
    [Theory]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(429, "invalid_request_error")]
    [InlineData(500, "api_error")]
    [InlineData(0, "no connection")]
    public async Task A_sweep_stripe_refuses_or_cannot_be_reached_for_is_thrown(int status, string type)
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => status == 0 ? throw new HttpRequestException("No route to the sandbox.") : (status, Error(type, null));

        var thrown = await Should.ThrowAsync<Exception>(() => h.Processor.RecoverObservationsAsync(h.Binding, Since, Ct));

        thrown.ShouldNotBeOfType<PaymentConflictException>();
        // Once, and not again by itself: a repeat is the worker's to make, on its own schedule.
        h.Stripe.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_sweep_for_a_binding_that_is_not_the_hosts_own_reaches_nothing()
    {
        var h = new Adapter();
        h.Stripe.Answer = _ => StripeReplay.Recorded(RecordedEvents);

        foreach (var binding in new[] { h.Binding with { Account = "acct_synthetic000009" }, h.Binding with { Generation = Guid.NewGuid() }, h.Binding with { OrgId = Guid.NewGuid() } })
        { await Should.ThrowAsync<PaymentUnavailableException>(() => h.Processor.RecoverObservationsAsync(binding, Since, Ct)); }

        h.Stripe.Requests.ShouldBeEmpty();
    }

    // The recorded list of events, and the recorded payment and charge its dispute's events name.
    private static (int Status, string Body) RecordedList(StripeReplay.Sent sent) =>
        sent.Path == "/v1/events" ? StripeReplay.Recorded(RecordedEvents) : AchDispute()(sent);

    /// <summary>One event of the recorded list, as Stripe listed it. A copy, for a test to change.</summary>
    internal static JsonObject Listed(string eventId) =>
        (JsonObject)StripeReplay.RecordedBody(RecordedEvents)["data"]!.AsArray().Single(x => (string)x!["id"]! == eventId)!.DeepClone();

    internal static (int Status, string Body) Page(bool hasMore, params JsonNode?[] events) =>
        (200, new JsonObject { ["object"] = "list", ["has_more"] = hasMore, ["url"] = "/v1/events", ["data"] = new JsonArray([.. events.Select(x => x?.DeepClone())]) }.ToJsonString());
}

// The sweep and the aging in a whole sandbox host: what the worker's recovery pass asks, keeps and
// sends to a person. The clock is the host's and stands still; a payment's age is set by moving its
// creation time, to the second, because that time is otherwise the machine's.
public sealed partial class StripeSandboxHostTests
{
    private const string FailedEvent = "evt_synthetic000011";
    private const string SucceededEvent = "evt_synthetic000002";
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task A_payment_whose_failure_was_never_delivered_is_ended_by_the_sweep()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Declined = true };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        var waiting = await h.Operation(payment.Id, ct);
        (waiting.Status, waiting.ProviderId).ShouldBe(("Processing", Sandbox.IdFor(payment.Id)));
        sandbox.Events = _ => Listed(h, (FailedEvent, payment));

        await Sweep(h, ct);

        // Asked of the fixture's account. The first sweep of a host asks for all that Stripe keeps.
        var asked = sandbox.Sweeps.ShouldHaveSingleItem();
        asked.Headers["Stripe-Account"].ShouldBe(Account);
        asked.Query["created[gte]"].ShouldBe(AskedFrom(h.Clock.GetUtcNow() - EventsKept));
        // Kept as a delivery would have been, and the payment woken: the worker's next pass judges it.
        var fact = (await h.Facts(ct)).ShouldHaveSingleItem();
        (fact.EventId, fact.Kind, fact.ProviderId).ShouldBe((FailedEvent, "Failed", Sandbox.IdFor(payment.Id)));
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing");
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.PaidAt, operation.JournalId).ShouldBe(("Failed", "collection_failed", null, null));
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    [Fact]
    public async Task A_payment_whose_success_was_never_delivered_is_marked_paid_by_the_sweep()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(500m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        sandbox.Events = _ => Listed(h, (SucceededEvent, payment));

        await Sweep(h, ct);
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.PaidAt, operation.JournalId, operation.ProcessedCount).ShouldBe(("Processing", null, SucceededAt, null, 1));
        (await h.Facts(ct)).ShouldHaveSingleItem().EventId.ShouldBe(SucceededEvent);
        (await h.JournalEntries(ct)).ShouldBe(entries);

        // Paid, it can still be disputed or refunded, so the next sweep is made all the same. It
        // finds the event it found before, which is the fact already held.
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        sandbox.Sweeps.Count.ShouldBe(2);
        await h.Run(ct);
        (await h.Facts(ct)).ShouldHaveSingleItem().Kind.ShouldBe("Succeeded");
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing");
    }

    // The recorded pair: delivery 018 and the list's evt_synthetic000011 are one event, recorded both
    // ways, and each is pointed at this host's payment by the same three substitutions. Whichever is
    // kept first, the other is the same fact again. Read differently, it would be a conflict.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_event_delivered_and_also_found_by_the_sweep_is_one_fact_and_no_conflict(bool deliveredFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Declined = true };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        await h.Run(ct);
        sandbox.Events = _ => Listed(h, (FailedEvent, payment));
        ((string)StripeReplay.Delivered(Failed)["id"]!).ShouldBe(FailedEvent);

        // Still waiting when the sweep runs either way: the worker has not judged the delivery yet.
        if (deliveredFirst) { (await h.Deliver(Event(Failed, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        await Sweep(h, ct);
        if (!deliveredFirst) { (await h.Deliver(Event(Failed, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        sandbox.Sweeps.Count.ShouldBe(1);
        await h.Run(ct);

        var fact = (await h.Facts(ct)).ShouldHaveSingleItem();
        (fact.EventId, fact.Kind).ShouldBe((FailedEvent, "Failed"));
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.ProcessedCount).ShouldBe(("Failed", "collection_failed", 1));
    }

    [Fact]
    public async Task A_sweep_that_fails_stops_no_due_payment_and_is_tried_again_at_the_next_interval_and_not_before()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Declined = true };
        await using var h = await Setup(sandbox.Replay, ct);
        var first = await h.Submit(200m, ct, PaymentMethods.Card);
        await h.Run(ct);
        sandbox.Events = _ => Fault;

        // Not thrown: the worker's pass goes on to whatever is due, now and next time.
        await Sweep(h, ct);
        sandbox.Sweeps.Count.ShouldBe(1);
        var second = await h.Submit(201m, ct, PaymentMethods.Card);
        await h.Run(ct);
        (await h.Operation(second.Id, ct)).ProviderId.ShouldBe(Sandbox.IdFor(second.Id));

        // Not again at once, nor a second short of the interval.
        await Sweep(h, ct);
        h.Clock.Advance(SweepInterval - TimeSpan.FromSeconds(1));
        await Sweep(h, ct);
        sandbox.Sweeps.Count.ShouldBe(1);

        // At the interval it is asked again, and what was missed is found then.
        sandbox.Events = _ => Listed(h, (FailedEvent, first));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await Sweep(h, ct);
        sandbox.Sweeps.Count.ShouldBe(2);
        await h.Run(ct);
        (await h.Operation(first.Id, ct)).Status.ShouldBe("Failed");

        // A sweep that worked waits the interval out too.
        await Sweep(h, ct);
        sandbox.Sweeps.Count.ShouldBe(2);
    }

    // The worker itself, left in the host: its loop does both, and a sweep Stripe refuses every time
    // does not end it or hold up a payment that is due. Nothing here counts on which of the worker's
    // passes came first: each step waits for one more sweep than there had been.
    [Fact]
    public async Task The_worker_sweeps_from_its_loop_and_goes_on_collecting_when_the_sweep_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => Fault };
        await using var h = await Setup(sandbox.Replay, ct, worker: true);

        var first = await h.Submit(200m, ct, PaymentMethods.Card);
        await Until(async () => (await h.Operation(first.Id, ct)).ProviderId is not null, "the worker to collect the first payment", ct);
        // Stripe refuses every time: any sweep seen has failed.
        await AnotherSweep(h, sandbox, ct);

        var second = await h.Submit(201m, ct, PaymentMethods.Card);
        await Until(async () => (await h.Operation(second.Id, ct)).ProviderId is not null, "the worker to collect a payment after its sweep failed", ct);

        // And it goes on sweeping, an interval at a time.
        await AnotherSweep(h, sandbox, ct);
        await AnotherSweep(h, sandbox, ct);
    }

    // A pass over what is due that throws every time must not keep the sweep from running. The
    // payment is at rest and waiting; it is made due with its lock failing, so that each pass fails
    // at the claim. Two more sweeps are waited for: one could come from a pass already under way.
    [Fact]
    public async Task The_worker_still_sweeps_when_its_pass_over_due_payments_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct, worker: true);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        await Until(async () => (await h.Operation(payment.Id, ct)).ProviderId is not null, "the worker to collect the payment", ct);

        h.Commands.FailingLocks[payment.Id.ToString()] = 0;
        await h.InOrg(sp => sp.GetRequiredService<AppDbContext>().Database
            .ExecuteSqlAsync($"UPDATE payment_operations SET due_at = {h.Clock.GetUtcNow().UtcDateTime} WHERE id = {payment.Id}", ct), ct);
        await Should.ThrowAsync<IOException>(() => h.Run(ct));

        await AnotherSweep(h, sandbox, ct);
        await AnotherSweep(h, sandbox, ct);
    }

    // Two fixtures on one host. The first's due payment cannot be claimed; the second's is collected all the same.
    [Fact]
    public async Task One_fixtures_failing_pass_does_not_stop_the_next_fixtures_due_payments()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct, secondAccount: "acct_synthetic000009");
        var other = h.Second.ShouldNotBeNull();
        var stuck = await h.Submit(200m, ct, PaymentMethods.Card);
        var theirs = await other.Submit(201m, ct, PaymentMethods.Card);
        h.Commands.FailingLocks[stuck.Id.ToString()] = 0;

        // Still thrown, for the worker to log and a foreground step to report. After the others have had their turn.
        await Should.ThrowAsync<IOException>(() => h.Run(ct));

        (await h.Operation(stuck.Id, ct)).ProviderId.ShouldBeNull();
        (await other.Operation(theirs.Id, ct)).ProviderId.ShouldBe(Sandbox.IdFor(theirs.Id));

        // Mended, the first is collected too: it was the failure and nothing else that held it.
        h.Commands.FailingLocks.Clear();
        await h.Run(ct);
        (await h.Operation(stuck.Id, ct)).ProviderId.ShouldBe(Sandbox.IdFor(stuck.Id));
    }

    [Fact]
    public async Task One_fixtures_failed_sweep_does_not_stop_the_next_fixtures_sweep_and_aging()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct, secondAccount: "acct_synthetic000009");
        var other = h.Second.ShouldNotBeNull();
        var ours = await h.Submit(200m, ct, PaymentMethods.Card);
        var theirs = await other.Submit(201m, ct, PaymentMethods.Card);
        await h.Run(ct);
        await Created(h, ours.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);
        await Created(other, theirs.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);
        // The first fixture bound is the one refused, so its failure comes before the other's turn.
        sandbox.Events = sent => sent.Headers["Stripe-Account"] == Account ? Fault : StripeSandboxProcessorTests.Page(false);

        await Sweep(h, ct);

        sandbox.Sweeps.Select(x => x.Headers["Stripe-Account"]).ShouldBe([Account, "acct_synthetic000009"]);
        (await h.Operation(ours.Id, ct)).Status.ShouldBe("Processing");
        var aged = await other.Operation(theirs.Id, ct);
        (aged.Status, aged.Reason).ShouldBe(("NeedsReview", "outcome_overdue"));
    }

    // What the sweep found could not all be kept, so the sweep has not done what aging rests on: the
    // old payment beside it stays where it is until a sweep is kept whole.
    [Fact]
    public async Task A_sweep_that_cannot_keep_what_it_found_ages_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Declined = true };
        await using var h = await Setup(sandbox.Replay, ct);
        var old = await h.Submit(200m, ct, PaymentMethods.Card);
        var found = await h.Submit(201m, ct, PaymentMethods.Card);
        await h.Run(ct);
        await Created(h, old.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);
        sandbox.Events = _ => Listed(h, (FailedEvent, found));
        h.Commands.FailingLocks["event:" + FailedEvent] = 0;

        await Sweep(h, ct);

        sandbox.Sweeps.Count.ShouldBe(1);
        (await h.Facts(ct)).ShouldBeEmpty();
        (await h.Operation(old.Id, ct)).Status.ShouldBe("Processing");

        h.Commands.FailingLocks.Clear();
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        (await h.Facts(ct)).ShouldHaveSingleItem().EventId.ShouldBe(FailedEvent);
        var operation = await h.Operation(old.Id, ct);
        (operation.Status, operation.Reason).ShouldBe(("NeedsReview", "outcome_overdue"));
    }

    [Fact]
    public async Task A_payment_that_cannot_be_aged_does_not_stop_the_rest_being_aged()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var older = await h.Submit(200m, ct, PaymentMethods.Card);
        var newer = await h.Submit(201m, ct, PaymentMethods.Card);
        await h.Run(ct);
        // The older is offered first, and it is the one that fails.
        await Created(h, older.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-9), ct);
        await Created(h, newer.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);
        h.Commands.FailingLocks[older.Id.ToString()] = 0;

        await Sweep(h, ct);

        (await h.Operation(older.Id, ct)).Status.ShouldBe("Processing");
        var aged = await h.Operation(newer.Id, ct);
        (aged.Status, aged.Reason).ShouldBe(("NeedsReview", "outcome_overdue"));

        // At the next sweep it is tried again.
        h.Commands.FailingLocks.Clear();
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        (await h.Operation(older.Id, ct)).Reason.ShouldBe("outcome_overdue");
    }

    // A row the binding does not answer for: here one whose account is no longer the fixture's. It is
    // older than anything waiting, and neither sets how far back Stripe is asked nor is offered for aging.
    [Fact]
    public async Task A_waiting_payment_that_is_not_the_bindings_neither_sets_the_window_nor_is_aged()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var elsewhere = await h.Submit(200m, ct, PaymentMethods.Card);
        var ours = await h.Submit(201m, ct, PaymentMethods.Card);
        await h.Run(ct);
        // A sweep kept whole first: the one after asks from it, unless a waiting payment is older.
        await Sweep(h, ct);
        h.Clock.Advance(SweepInterval);
        var now = h.Clock.GetUtcNow();
        await Created(h, elsewhere.Id, now.UtcDateTime.AddDays(-20), ct);
        await Created(h, ours.Id, now.UtcDateTime.AddDays(-8), ct);
        await h.InOrg(sp => sp.GetRequiredService<AppDbContext>().Database
            .ExecuteSqlAsync($"UPDATE payment_operations SET account = 'acct_synthetic000007' WHERE id = {elsewhere.Id}", ct), ct);

        await Sweep(h, ct);

        sandbox.Sweeps.Count.ShouldBe(2);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe((now.AddDays(-8).ToUnixTimeSeconds() - 300).ToString());
        (await h.Operation(elsewhere.Id, ct)).Status.ShouldBe("Processing");
        (await h.Operation(ours.Id, ct)).Reason.ShouldBe("outcome_overdue");
    }

    // The limit on how far back Stripe is asked, with a sweep Stripe answers: the payment is then old enough to go to a person.
    [Fact]
    public async Task A_sweep_that_succeeds_for_a_payment_older_than_stripe_keeps_events_asks_for_what_is_kept_and_ages_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        await h.Run(ct);
        // After a sweep kept whole, so that it is the payment's age and not a first sweep that reaches back.
        await Sweep(h, ct);
        h.Clock.Advance(SweepInterval);
        var now = h.Clock.GetUtcNow();
        await Created(h, payment.Id, now.UtcDateTime.AddDays(-40), ct);

        await Sweep(h, ct);

        sandbox.Sweeps.Count.ShouldBe(2);
        sandbox.Sweeps[^1].Query["created[gte]"].ShouldBe((now.AddDays(-29).ToUnixTimeSeconds() - 300).ToString());
        (await h.Operation(payment.Id, ct)).Reason.ShouldBe("outcome_overdue");
    }

    // A payment with a person stays with that person whatever fails while a late fact is judged. The
    // first try at judging it meets a fault at Stripe; before the fix that made it an ordinary
    // payment in processing, and the retry then took it out of review.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_success_whose_first_judging_fails_leaves_an_overdue_payment_with_a_person(bool closedFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        await Created(h, payment.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);
        await Sweep(h, ct);
        if (closedFirst)
        {
            await h.InOrg(sp => sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>()
                .Send(new ClosePaymentReview(h.Binding, payment.Id, UuidV7.NewId(), "Paid by cheque."), ct), ct);
        }
        var held = closedFirst ? "ReviewClosed" : "NeedsReview";
        (await h.Operation(payment.Id, ct)).Status.ShouldBe(held);

        (await h.Deliver(Event(Succeeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        sandbox.Refused = Fault;
        await h.Run(ct);

        // Where it was, under the reason it had, and due again on the usual schedule.
        var failed = await h.Operation(payment.Id, ct);
        (failed.Status, failed.Reason, failed.PaidAt, failed.Attempts, failed.ProcessedCount).ShouldBe((held, "outcome_overdue", null, 1, 0));
        failed.DueAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime.AddSeconds(PaymentEngine.RetrySeconds[0]), TimeSpan.FromMilliseconds(1));
        (failed.LeaseUntil, failed.LeaseClaimId).ShouldBe((null, null));

        sandbox.Refused = null;
        h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[0]));
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.PaidAt, operation.JournalId, operation.ProcessedCount, operation.Attempts).ShouldBe(
            ("NeedsReview", "outcome_overdue", SucceededAt, null, 1, 0));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        await h.InOrg(async sp =>
        {
            var view = (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>().Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            (view.CanRetry, view.CanCloseReview).ShouldBe((false, true));
        }, ct);
        // Judged, it rests: another pass asks nothing.
        var asked = sandbox.Replay.Requests.Count;
        await h.Run(ct);
        sandbox.Replay.Requests.Count.ShouldBe(asked);
    }

    [Fact]
    public async Task A_review_whose_late_fact_cannot_be_judged_is_retried_on_the_schedule_and_then_rests_in_review()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing", Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        await h.Run(ct);
        await Created(h, payment.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);
        await Sweep(h, ct);
        (await h.Deliver(Event(AchSucceeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        sandbox.Refused = Fault;
        var rest = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

        // One try and five more, each a little later than the last, and in review throughout.
        for (var attempt = 1; attempt <= PaymentEngine.RetrySeconds.Length + 1; attempt++)
        {
            var asked = sandbox.Replay.Requests.Count;
            await h.Run(ct);
            sandbox.Replay.Requests.Count.ShouldBe(asked + 1, $"attempt {attempt}");
            var failed = await h.Operation(payment.Id, ct);
            (failed.Status, failed.Reason, failed.PaidAt).ShouldBe(("NeedsReview", "outcome_overdue", null), $"attempt {attempt}");
            if (attempt > PaymentEngine.RetrySeconds.Length) { break; }
            failed.DueAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime.AddSeconds(PaymentEngine.RetrySeconds[attempt - 1]), TimeSpan.FromMilliseconds(1));
            h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[attempt - 1]));
        }

        // Out of tries, it rests where it was. There is no retry to offer: a person already has it.
        var resting = await h.Operation(payment.Id, ct);
        (resting.Status, resting.Reason, resting.DueAt, resting.ProcessedCount, resting.LeaseUntil).ShouldBe(("NeedsReview", "outcome_overdue", rest, 0, null));
        h.Clock.Advance(TimeSpan.FromHours(1));
        var quiet = sandbox.Replay.Requests.Count;
        await h.Run(ct);
        sandbox.Replay.Requests.Count.ShouldBe(quiet);
        await h.InOrg(async sp =>
        {
            var view = (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>().Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            (view.CanRetry, view.CanCloseReview).ShouldBe((false, true));
            await Should.ThrowAsync<PaymentConflictException>(() => sp.GetRequiredService<PaymentEngine>().RetryAsync(h.Binding, payment.Id, ct));
        }, ct);

        // The next fact wakes it with a whole schedule of its own, and both are judged together.
        sandbox.Refused = null;
        (await h.Deliver(Event(AchProcessing, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        sandbox.Refused = Fault;
        await h.Run(ct);
        (await h.Operation(payment.Id, ct)).DueAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime.AddSeconds(PaymentEngine.RetrySeconds[0]), TimeSpan.FromMilliseconds(1));
        sandbox.Refused = null;
        h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[0]));
        await h.Run(ct);
        var judged = await h.Operation(payment.Id, ct);
        (judged.Status, judged.Reason, judged.ProcessedCount, judged.DueAt).ShouldBe(("NeedsReview", "outcome_overdue", 2, rest));
        judged.PaidAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task At_seven_days_with_no_outcome_a_waiting_payment_goes_to_review_and_a_second_sooner_it_does_not()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        PaymentEngine.OutcomeOverdueAfter.ShouldBe(TimeSpan.FromDays(7));

        await Created(h, payment.Id, WholeSecond(h).AddDays(-7).AddSeconds(1), ct);
        await Sweep(h, ct);
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason).ShouldBe(("Processing", null));

        h.Clock.Advance(SweepInterval);
        await Created(h, payment.Id, WholeSecond(h).AddDays(-7), ct);
        await Sweep(h, ct);

        // Asked for first, both times, and then sent to a person. Nothing is posted, and nothing is due.
        sandbox.Sweeps.Count.ShouldBe(2);
        operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason, operation.PaidAt, operation.JournalId, operation.ProviderId).ShouldBe(
            ("NeedsReview", "outcome_overdue", null, null, Sandbox.IdFor(payment.Id)));
        (await h.JournalEntries(ct)).ShouldBe(entries);
        var asked = sandbox.Replay.Requests.Count;
        await h.Run(ct);
        sandbox.Replay.Requests.Count.ShouldBe(asked);
        // Staff can close it, and cannot retry it: there is nothing to send again.
        await h.InOrg(async sp =>
        {
            var view = (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>().Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            (view.Status, view.Reason, view.CanCloseReview, view.CanRetry, view.CanPostReturn, view.ReceiptRecorded).ShouldBe(("NeedsReview", "outcome_overdue", true, false, false, false));
        }, ct);

        // In review it waits for nothing, and Stripe is asked all the same: a sweep no longer needs a
        // payment waiting. It is not aged again, and nothing about it changes.
        h.Clock.Advance(SweepInterval);
        await Sweep(h, ct);
        sandbox.Sweeps.Count.ShouldBe(3);
        var later = await h.Operation(payment.Id, ct);
        (later.Status, later.Reason, later.ProcessedCount).ShouldBe(("NeedsReview", "outcome_overdue", operation.ProcessedCount));
    }

    // A payment must never go to a person while the events that could end it have not been asked for.
    [Fact]
    public async Task Nothing_is_aged_when_the_sweep_failed_and_it_is_once_a_sweep_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        await h.Run(ct);
        await Created(h, payment.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);
        var live = StripeSandboxProcessorTests.Listed("evt_synthetic000001");
        live["livemode"] = true;
        var refusals = new (string Name, (int Status, string Body) Answer)[]
        {
            ("a fault at Stripe", Fault),
            ("a list that does not end", StripeSandboxProcessorTests.Page(true, StripeSandboxProcessorTests.Listed("evt_synthetic000001"))),
            ("a live event", StripeSandboxProcessorTests.Page(false, live)),
        };

        foreach (var (name, answer) in refusals)
        {
            sandbox.Events = _ => answer;
            var asked = sandbox.Sweeps.Count;
            await Sweep(h, ct);
            sandbox.Sweeps.Count.ShouldBeGreaterThan(asked, name);
            (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing", name);
            h.Clock.Advance(SweepInterval);
        }

        sandbox.Events = _ => StripeSandboxProcessorTests.Page(false);
        await Sweep(h, ct);
        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason).ShouldBe(("NeedsReview", "outcome_overdue"));
    }

    // The sweep that finds an old payment's ending must not also send it to review: found, it has a
    // fact to be judged and is due, and the worker's next pass ends it.
    [Fact]
    public async Task An_old_payment_whose_outcome_the_sweep_has_just_found_is_judged_and_not_sent_to_review()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Declined = true };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        await h.Run(ct);
        await Created(h, payment.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);
        sandbox.Events = _ => Listed(h, (FailedEvent, payment));

        await Sweep(h, ct);

        var operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason).ShouldBe(("Processing", null));
        await h.Run(ct);
        operation = await h.Operation(payment.Id, ct);
        (operation.Status, operation.Reason).ShouldBe(("Failed", "collection_failed"));
    }

    // The engine's own conditions, each alone. Every payment here is seven days old and accepted.
    [Theory]
    [InlineData("the worker holds it")]
    [InlineData("due")]
    [InlineData("between attempts")]
    [InlineData("a fact not yet judged")]
    [InlineData("paid")]
    [InlineData("a second short of seven days")]
    [InlineData("already ended")]
    [InlineData("already with a person")]
    [InlineData("never accepted")]
    public async Task Only_an_idle_payment_still_waiting_with_every_fact_judged_is_overdue(string difference)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        var control = await h.Submit(505m, ct);
        await h.Run(ct);
        var now = WholeSecond(h);
        await Created(h, payment.Id, now.AddDays(-7), ct);
        await Created(h, control.Id, now.AddDays(-7), ct);
        if (difference == "a fact not yet judged") { (await h.Deliver(Event(AchProcessing, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent); }
        if (difference == "a second short of seven days") { await Created(h, payment.Id, now.AddDays(-7).AddSeconds(1), ct); }
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var operation = await db.Set<PaymentOperation>().SingleAsync(x => x.Id == payment.Id, ct);
            switch (difference)
            {
                // As a claim leaves it, and as a wake does.
                case "the worker holds it": operation.LeaseUntil = now.AddSeconds(30); operation.LeaseClaimId = UuidV7.NewId(); break;
                case "due": operation.DueAt = now; break;
                // As a lookup that failed leaves it: to be tried again in ten minutes, and to a person after that.
                case "between attempts": operation.Reason = "technical_failure"; operation.Attempts = 5; operation.DueAt = now.AddSeconds(600); break;
                // The delivery above woke it. Put back to rest, so that only the fact stands in the way.
                case "a fact not yet judged": operation.DueAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc); break;
                case "paid": operation.PaidAt = now.AddDays(-6); break;
                case "already ended": operation.Status = "Failed"; operation.Reason = "collection_failed"; break;
                case "already with a person": operation.Status = "NeedsReview"; operation.Reason = "conflicting_evidence"; break;
                case "never accepted": operation.ProviderId = null; break;
                default: break;
            }
            await db.SaveChangesAsync(ct);
        }, ct);
        var before = await h.Operation(payment.Id, ct);

        (await Overdue(h, payment.Id, ct)).ShouldBeFalse();

        var after = await h.Operation(payment.Id, ct);
        (after.Status, after.Reason).ShouldBe((before.Status, before.Reason));
        // The other payment, the same but for that one thing, is overdue.
        (await Overdue(h, control.Id, ct)).ShouldBeTrue();
        var marked = await h.Operation(control.Id, ct);
        (marked.Status, marked.Reason, marked.ProcessedCount, marked.JournalId).ShouldBe(("NeedsReview", "outcome_overdue", 0, null));
        // And once is all: it is in review, which is not waiting.
        (await Overdue(h, control.Id, ct)).ShouldBeFalse();
    }

    // The reference, then the operation, as a completion and a delivery take them. Stopped at the
    // operation's lock, the marking must already hold the reference.
    [Fact]
    public async Task Marking_a_payment_overdue_holds_the_reference_before_it_asks_for_the_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(504.03m, ct);
        await h.Run(ct);
        await Created(h, payment.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-8), ct);
        var reference = "reference:" + Sandbox.IdFor(payment.Id);
        var held = await Hold(h, sp => sp.GetRequiredService<PaymentEngine>().LockAsync(payment.Id.ToString(), ct), ct);
        Task<bool> marking;
        try
        {
            marking = Overdue(h, payment.Id, ct);
            await Until(async () => marking.IsCompleted || (await Advisory(h, payment.Id.ToString(), ct)).Waiting.Length == 1, "the marking to reach the operation lock", ct);
            marking.IsCompleted.ShouldBeFalse();
            var waiter = (await Advisory(h, payment.Id.ToString(), ct)).Waiting.ShouldHaveSingleItem();
            var onReference = await Advisory(h, reference, ct);
            onReference.Holding.ShouldBe([waiter]);
            onReference.Waiting.ShouldBeEmpty();
            (await h.Operation(payment.Id, ct)).Status.ShouldBe("Processing");
        }
        finally { await held.Release(); }

        (await marking).ShouldBeTrue();
    }

    // What follows for an overdue payment from the code that was already there: a person can close
    // it; a failure that arrives later ends it; a success that arrives later is recorded and leaves
    // it with a person, back in review if it had been closed. It never returns to processing.
    [Theory]
    [InlineData("Failed", false)]
    [InlineData("Failed", true)]
    [InlineData("Succeeded", false)]
    [InlineData("Succeeded", true)]
    public async Task An_overdue_payment_can_be_closed_and_a_late_outcome_is_still_recorded(string late, bool closedFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Events = _ => StripeSandboxProcessorTests.Page(false) };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await h.Submit(200m, ct, PaymentMethods.Card);
        var entries = await h.JournalEntries(ct);
        await h.Run(ct);
        await Created(h, payment.Id, h.Clock.GetUtcNow().UtcDateTime.AddDays(-7), ct);
        await Sweep(h, ct);
        (await h.Operation(payment.Id, ct)).Reason.ShouldBe("outcome_overdue");

        if (closedFirst)
        {
            await h.InOrg(async sp =>
            {
                var closed = await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>()
                    .Send(new ClosePaymentReview(h.Binding, payment.Id, UuidV7.NewId(), "Tenant paid by cheque instead."), ct);
                (closed.ShouldNotBeNull().Payment.Status, closed.Payment.ReviewNote).ShouldBe(("ReviewClosed", "Tenant paid by cheque instead."));
            }, ct);
        }

        (await h.Deliver(Event(late == "Failed" ? Failed : Succeeded, h, payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        await h.Run(ct);

        var operation = await h.Operation(payment.Id, ct);
        if (late == "Failed") { (operation.Status, operation.Reason, operation.PaidAt).ShouldBe(("Failed", "collection_failed", null)); }
        // Paid, and still a person's to settle: they may already have put the books right by hand.
        else { (operation.Status, operation.Reason, operation.PaidAt).ShouldBe(("NeedsReview", "outcome_overdue", SucceededAt)); }
        (operation.JournalId, operation.ProcessedCount).ShouldBe((null, 1));
        operation.ReviewNote.ShouldBe(closedFirst ? "Tenant paid by cheque instead." : null);
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }

    private static (int Status, string Body) Fault => (500, """{"error":{"type":"api_error","message":"Fault."}}""");

    // Moves the host's clock on an interval and waits for the worker to ask Stripe once more than it had.
    private static async Task AnotherSweep(Harness h, Sandbox sandbox, CancellationToken ct)
    {
        var before = sandbox.Sweeps.Count;
        h.Clock.Advance(SweepInterval);
        await Until(() => Task.FromResult(sandbox.Sweeps.Count > before), "the worker to sweep again", ct);
    }

    private static Task Sweep(Harness h, CancellationToken ct) => h.App.Services.GetRequiredService<PaymentRunner>().RecoverOnceAsync(ct);

    private static async Task<bool> Overdue(Harness h, Guid id, CancellationToken ct)
    {
        var marked = false;
        await h.InOrg(async sp => marked = await sp.GetRequiredService<PaymentEngine>().MarkOverdueAsync(h.Binding, id, ct), ct);
        return marked;
    }

    // A payment's age, set exactly. Its creation time is otherwise stamped by the database's clock.
    private static Task Created(Harness h, Guid id, DateTime at, CancellationToken ct) => h.InOrg(async sp =>
        (await sp.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync($"UPDATE payment_operations SET created_at = {at} WHERE id = {id}", ct)).ShouldBe(1), ct);

    // The host's time moved on to a whole second, and returned: a time stored to the microsecond then compares exactly.
    private static DateTime WholeSecond(Harness h)
    {
        h.Clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - (h.Clock.GetUtcNow().Ticks % TimeSpan.TicksPerSecond)));
        return h.Clock.GetUtcNow().UtcDateTime;
    }

    // The recorded list of the account's events, with some pointed at payments this host made, as
    // Event points a delivery. The rest stay the probe's own, of a generation that is not this host's.
    private static (int Status, string Body) Listed(Harness h, params (string EventId, PaymentView Payment)[] pointed)
    {
        var list = StripeReplay.RecordedBody("064-events-api.json");
        foreach (var (eventId, payment) in pointed)
        { Pointed(list["data"]!.AsArray().Single(x => (string)x!["id"]! == eventId)!.AsObject(), h, payment); }
        return (200, list.ToJsonString());
    }
}
