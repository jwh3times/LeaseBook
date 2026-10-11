using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// What the independent review of step 8 found (#513): which failures of reading a delivered event
// are answered 503, what the line logged for one says, and that storing what was read is not among them.
public sealed partial class StripeSandboxHostTests
{
    private const string BalanceLines = "/v1/balance_transactions";
    // What a library's or a transport's own text could carry, and the log must never repeat.
    private const string LibraryText = "sk_test_repeated_by_the_library";

    // The sandbox double as it is, except that asking for a payout's lines throws.
    private static void LinesThrow(Sandbox sandbox, Func<Exception> thrown)
    {
        var answer = sandbox.Replay.Answer;
        sandbox.Replay.Answer = sent => sent.Path == BalanceLines ? throw thrown() : answer(sent);
    }

    private static (LogLevel Level, string Category, string Message, EventId EventId) Deferred(Observability.CapturingLoggerProvider logs)
    {
        var deferred = logs.Entries.Where(x => x.EventId.Id == 4608).ShouldHaveSingleItem();
        (deferred.Level, deferred.EventId.Name).ShouldBe((LogLevel.Warning, "PaymentCallbackDeferred"));
        logs.Entries.ShouldNotContain(x => x.EventId.Id == 1000);
        return deferred;
    }

    private static void ShouldBeUnhandled((HttpStatusCode Status, int Commands, string Body) delivered, Observability.CapturingLoggerProvider logs)
    {
        delivered.Status.ShouldBe(HttpStatusCode.InternalServerError, delivered.Body);
        ((string?)JsonNode.Parse(delivered.Body)!["code"]).ShouldBe("internal_error");
        logs.Entries.ShouldNotContain(x => x.EventId.Id == 4608);
        logs.Entries.Where(x => x.EventId.Id == 1000).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Error);
    }

    // A fault in the reading itself is not Stripe failing to answer. It is not called that, Stripe is
    // not told to send the event again as though it were, and it reaches the handler of faults nobody
    // meant, which logs the whole exception.
    [Fact]
    public async Task A_fault_in_reading_an_event_is_an_unhandled_error_and_not_a_deferral()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        using var logs = new Observability.CapturingLoggerProvider();
        await using var h = await Setup(sandbox.Replay, ct, logs: logs);
        await Paid(h, 200m, ct);
        LinesThrow(sandbox, () => new InvalidOperationException("A fault in the reader."));

        var delivered = await h.Deliver(PaidPayout(Payout, 198.40m), ct);

        ShouldBeUnhandled(delivered, logs);
        (await Payouts(h, ct)).ShouldBeEmpty();
    }

    // Stripe could not be reached, or did not answer in time. Deferred, and named by its type alone:
    // what a library or a socket says of a request is not LeaseBook's text to vouch for.
    [Theory]
    [InlineData("no connection", "HttpRequestException")]
    [InlineData("timed out", "TaskCanceledException")]
    [InlineData("stream broken", "IOException")]
    public async Task An_event_stripe_could_not_be_reached_about_is_deferred_and_logged_by_type_only(string failure, string type)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        using var logs = new Observability.CapturingLoggerProvider();
        await using var h = await Setup(sandbox.Replay, ct, logs: logs);
        await Paid(h, 200m, ct);
        LinesThrow(sandbox, () => failure switch
        {
            "no connection" => new HttpRequestException(LibraryText),
            "timed out" => new TaskCanceledException(LibraryText, new TimeoutException(LibraryText)),
            _ => new IOException(LibraryText),
        });

        var delivered = await h.Deliver(PaidPayout(Payout, 198.40m), ct);

        delivered.Status.ShouldBe(HttpStatusCode.ServiceUnavailable, delivered.Body);
        ((string?)JsonNode.Parse(delivered.Body)!["code"]).ShouldBe("payment_callback_deferred");
        var deferred = Deferred(logs);
        deferred.Message.ShouldContain($"({type}; adapter condition: none)");
        logs.Entries.ShouldNotContain(x => x.Message.Contains(LibraryText, StringComparison.Ordinal));
        (await Payouts(h, ct)).ShouldBeEmpty();
    }

    // Both are deferred, and they are not the same thing: a payout Stripe is still reconciling is
    // early and will be readable, and one Stripe does not hold never will be. The adapter's own
    // sentence for each is in the line, so that whoever reads it can tell which.
    [Theory]
    [InlineData("still reconciling", "The Stripe sandbox has not finished reconciling the payout; its lines are not read yet.")]
    [InlineData("not held", "The Stripe sandbox does not hold the payout its event is about.")]
    public async Task A_condition_the_adapter_names_is_deferred_and_logged_with_what_it_says(string condition, string says)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        using var logs = new Observability.CapturingLoggerProvider();
        await using var h = await Setup(sandbox.Replay, ct, logs: logs);
        await Paid(h, 200m, ct);
        var paid = PaidPayout(Payout, 198.40m);
        var payout = paid["data"]!["object"]!.AsObject();
        payout["reconciliation_status"] = "in_progress";
        sandbox.Payouts[Payout] = condition == "not held"
            ? (404, """{"error":{"type":"invalid_request_error","code":"resource_missing","message":"No such payout."}}""")
            : (200, payout.ToJsonString());

        var delivered = await h.Deliver(paid, ct);

        delivered.Status.ShouldBe(HttpStatusCode.ServiceUnavailable, delivered.Body);
        ((string?)JsonNode.Parse(delivered.Body)!["code"]).ShouldBe("payment_callback_deferred");
        // Never in the answer: the sender is told to send it again and nothing of why.
        delivered.Body.ShouldNotContain("Stripe");
        Deferred(logs).Message.ShouldContain($"(ProcessorUnansweredException; adapter condition: {says})");
        (await Payouts(h, ct)).ShouldBeEmpty();
    }

    // The event was read; keeping it failed. That is this host's fault and is answered as one, with
    // the same kind of exception a failed read is deferred for: only where it was thrown tells them apart.
    [Fact]
    public async Task A_payment_event_that_was_read_and_could_not_be_stored_is_an_unhandled_error_and_not_a_deferral()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        using var logs = new Observability.CapturingLoggerProvider();
        await using var h = await Setup(sandbox.Replay, ct, logs: logs);
        var payment = await h.Submit(200m, ct);
        await h.Run(ct);
        var succeeded = Event(AchSucceeded, h, payment);
        h.Commands.FailingLocks["event:" + (string)succeeded["id"]!] = 0;

        var delivered = await h.Deliver(succeeded, ct);

        ShouldBeUnhandled(delivered, logs);
        delivered.Commands.ShouldBeGreaterThan(0);
        (await h.Facts(ct)).ShouldBeEmpty();

        // The same event is kept once storing works, so the failure above was the store's and nothing else's.
        h.Commands.FailingLocks.Clear();
        (await h.Deliver(succeeded, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await h.Facts(ct)).ShouldHaveSingleItem().Kind.ShouldBe("Succeeded");
    }

    [Fact]
    public async Task A_payout_that_was_read_and_could_not_be_stored_is_an_unhandled_error_and_not_a_deferral()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        using var logs = new Observability.CapturingLoggerProvider();
        await using var h = await Setup(sandbox.Replay, ct, logs: logs);
        var payment = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        var paid = PaidPayout(Payout, 198.40m);
        h.Commands.FailingLocks["settlement:" + Payout] = 0;

        var delivered = await h.Deliver(paid, ct);

        ShouldBeUnhandled(delivered, logs);
        // Stripe was asked and answered: the read was whole before the store was tried.
        sandbox.LineRequests.ShouldHaveSingleItem();
        delivered.Commands.ShouldBeGreaterThan(0);
        (await Payouts(h, ct)).ShouldBeEmpty();

        h.Commands.FailingLocks.Clear();
        (await h.Deliver(paid, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await Payouts(h, ct)).ShouldHaveSingleItem().Status.ShouldBe("AwaitingBank");
    }
}
