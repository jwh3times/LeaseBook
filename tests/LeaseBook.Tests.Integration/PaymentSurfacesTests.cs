using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Observability;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// What the two payment modes show of themselves (ADR-054, #513 step 8): the mode a screen words
// itself by, the memo a posting carries, what the logs call things, and a staff read that says
// only what is stored. Nothing here is a new money path: the lines a posting makes are held to
// the same figures in both modes.

/// <summary>What a receipt posts whichever mode collected it, so that both modes are held to one answer.</summary>
internal static class PaymentReceiptShape
{
    public const string SimulationMemo = "Simulated tenant payment";
    public const string SandboxMemo = "Stripe sandbox tenant payment";
    public const string SimulationReturnNote = "Bank returned this simulated tenant payment.";
    public const string SandboxReturnNote = "Bank returned this Stripe sandbox tenant payment.";

    /// <summary>The lines of a receipt of 200.00 by ACH from the fixture's first tenant, in either mode.</summary>
    public static readonly string[] TwoHundredByAch =
    [
        "OwnerEquity Cash - 200.00",
        "TenantReceivable Accrual - 200.00",
        "TrustBank Both 200.00 -",
    ];

    public static async Task<string[]> LinesAsync(AppDbContext db, Guid entryId, CancellationToken ct) =>
        [.. (await db.Set<JournalLine>().AsNoTracking().Where(x => x.EntryId == entryId).ToListAsync(ct))
            .Select(x => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{x.AccountClass} {x.Basis} {Amount(x.Debit)} {Amount(x.Credit)}"))
            .Order(StringComparer.Ordinal)];

    private static string Amount(Money? money) =>
        money is { } value ? value.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "-";
}

public sealed partial class StripeSandboxHostTests
{
    [Fact]
    public async Task A_sandbox_host_tells_staff_and_the_tenant_its_mode_and_the_tenant_nothing_else_new()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox();
        await using var h = await Setup(sandbox.Replay, ct);
        using var tenant = await h.Tenant(ct);
        using var admin = await h.Admin(ct);

        using var mine = JsonDocument.Parse(await tenant.GetStringAsync("/api/portal/tenant/payments", ct));
        using var staff = JsonDocument.Parse(await admin.GetStringAsync("/api/payments", ct));

        mine.RootElement.GetProperty("mode").GetString().ShouldBe("StripeSandbox");
        staff.RootElement.GetProperty("mode").GetString().ShouldBe("StripeSandbox");
        // The mode and nothing beside it: no account, no generation, no bank.
        mine.RootElement.EnumerateObject().Select(x => x.Name).ShouldBe(
            ["enabled", "items", "unmatchedObservations", "fundsInTransit", "mode"], ignoreOrder: true);
        var body = mine.RootElement.GetRawText();
        body.ShouldNotContain(Account);
        body.ShouldNotContain(h.Binding.Generation.ToString());
        body.ShouldNotContain(h.Binding.BankId.ToString());
    }

    // A payout is the one way a sandbox payment reaches the ledger. Its receipt says which mode
    // collected it, and is otherwise the receipt the simulation posts for the same payment.
    [Fact]
    public async Task A_sandbox_payout_posts_its_receipt_under_the_sandbox_memo_and_the_same_lines_as_the_simulation()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);

        var (payout, items) = await Stored(h, Payout, ct);
        (payout.Status, payout.Mode).ShouldBe(("Posted", PaymentModes.StripeSandbox));
        var operation = await h.Operation(payment.Id, ct);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var receipt = await db.Set<JournalEntry>().AsNoTracking().SingleAsync(x => x.Id == operation.JournalId, ct);
            receipt.Description.ShouldBe(PaymentReceiptShape.SandboxMemo);
            // Everything but the memo is what it was: the event, the date, the reference and the lines.
            (receipt.EventType, receipt.EntryDate, receipt.SourceRef).ShouldBe(
                ("PaymentReceived", Today(h), $"payout:{Payout}:{items.Single().Item}"));
            (await PaymentReceiptShape.LinesAsync(db, receipt.Id, ct)).ShouldBe(PaymentReceiptShape.TwoHundredByAch);
        }, ct);
    }

    // Stripe never sends single-payment bank evidence or a complete return, so neither posts in a
    // sandbox host today. The engine is shared all the same: evidence held under the sandbox's mode
    // posts under the sandbox's memo, with the references the simulation gives its own.
    [Fact]
    public async Task Bank_evidence_and_a_complete_return_held_under_the_sandbox_mode_post_under_the_sandbox_memo()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        var payment = await Paid(h, 200m, ct);
        var at = h.Clock.GetUtcNow().UtcDateTime;
        ProcessorObservation Evidence(string kind) => new("evt_bank_" + kind, Sandbox.IdFor(payment.Id), Account, PaymentModes.StripeSandbox,
            h.Binding.Generation, kind, 200m, 0m, 200m, "USD", h.Binding.BankId, Today(h), "bank-" + kind, "po_bank", true, at);

        await h.InOrg(sp => sp.GetRequiredService<PaymentEngine>().ReceiveAsync(h.Binding, Evidence("BankCredit"), ct), ct);
        await h.Run(ct);
        var settled = await h.Operation(payment.Id, ct);
        settled.Status.ShouldBe("Settled");
        await h.InOrg(sp => sp.GetRequiredService<PaymentEngine>().ReceiveAsync(h.Binding, Evidence("Return"), ct), ct);
        await h.Run(ct);
        await h.InOrg(async sp => (await sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>()
            .Send(new PostPaymentReturn(h.Binding, payment.Id), ct)).ShouldNotBeNull().Refusal.ShouldBeNull(), ct);

        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Returned");
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var receipt = await db.Set<JournalEntry>().AsNoTracking().SingleAsync(x => x.Id == settled.JournalId, ct);
            (receipt.Description, receipt.SourceRef).ShouldBe((PaymentReceiptShape.SandboxMemo, $"sim-payment:{payment.Id:N}:receipt"));
            (await PaymentReceiptShape.LinesAsync(db, receipt.Id, ct)).ShouldBe(PaymentReceiptShape.TwoHundredByAch);
            var reversal = await db.Set<JournalEntry>().AsNoTracking().SingleAsync(x => x.ReversesEntryId == receipt.Id, ct);
            (reversal.InternalNote, reversal.SourceRef, reversal.EntryDate).ShouldBe(
                (PaymentReceiptShape.SandboxReturnNote, $"sim-payment:{payment.Id:N}:return", Today(h)));
        }, ct);
    }

    // Stripe's CLI forwards every kind of event an account has, and most are not this host's to act
    // on. Each is acknowledged and dropped, and said so quietly. Nothing a sandbox host logs calls a
    // payment or a payout simulated; the ids and names an alert is keyed on are what they were.
    [Fact]
    public async Task A_sandbox_host_logs_what_it_drops_at_debug_and_calls_nothing_simulated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var logs = new CapturingLoggerProvider();
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct, logs: logs);
        await HoldFees(h, 10m, ct);

        (await h.Deliver(StripeReplay.Delivered("004-webhook-connect.json"), ct)).Status.ShouldBe(HttpStatusCode.NoContent);

        var dropped = Payments(logs).ShouldHaveSingleItem();
        (dropped.Level, dropped.EventId.Id, dropped.EventId.Name).ShouldBe((LogLevel.Debug, 4602, "PaymentCallbackIgnored"));

        var payment = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);

        var said = Payments(logs);
        said.ShouldAllBe(x => !x.Message.Contains("imulated", StringComparison.Ordinal));
        said.Where(x => x.EventId.Id == 4600).ShouldNotBeEmpty();
        said.Where(x => x.EventId.Id == 4600).ShouldAllBe(x => x.EventId.Name == "PaymentProcessed" && x.Level == LogLevel.Information
            && x.Message.StartsWith("Online payment " + payment.Id + " processed in ", StringComparison.Ordinal));
        var checkedPayouts = said.Where(x => x.EventId.Id == 4604).ToArray();
        checkedPayouts.Length.ShouldBe(2);
        checkedPayouts.ShouldAllBe(x => x.EventId.Name == "PaymentSettlementEvaluated" && x.Message.StartsWith("Payout ", StringComparison.Ordinal));
        checkedPayouts[^1].Message.ShouldEndWith(" is Posted (none)");
    }

    // What the payment worker and the callback routes log, in the order they logged it.
    private static (LogLevel Level, string Category, string Message, EventId EventId)[] Payments(CapturingLoggerProvider logs) =>
        [.. logs.Entries.Where(x => x.EventId.Id is >= 4600 and < 4700)];

    // A held payout has Stripe's figure and no bank's. Read by staff it says so: no bank amount and
    // no bank date, where it used to say 0.00 on the first day of the calendar.
    [Fact]
    public async Task A_held_payout_is_read_by_staff_with_no_bank_amount_and_no_bank_date_until_a_bank_has_spoken()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        var payment = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        using var admin = await h.Admin(ct);

        using (var held = JsonDocument.Parse(await admin.GetStringAsync("/api/payments/settlements", ct)))
        {
            var payout = held.RootElement.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
            (payout.GetProperty("status").GetString(), payout.GetProperty("bankAmount").ValueKind, payout.GetProperty("bankDate").ValueKind,
                payout.GetProperty("postedAt").ValueKind).ShouldBe(("AwaitingBank", JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null));
            payout.GetProperty("processorAmount").GetDecimal().ShouldBe(198.40m);
        }

        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);

        var posted = (await admin.GetFromJsonAsync<SettlementsResponse>("/api/payments/settlements", ct))!.Items.ShouldHaveSingleItem();
        (posted.Status, posted.BankAmount, posted.BankDate, posted.ProcessorAmount).ShouldBe(("Posted", 198.40m, Today(h), 198.40m));
        posted.PostedAt.ShouldNotBeNull();
    }

    // "Post return" is offered from this flag. The engine refuses each of these however often it is
    // asked, so the flag must say so first: the same rule read twice, never two rules.
    [Theory]
    [InlineData("an ACH dispute before its payout line", "return_partial_unsupported")]
    [InlineData("a card dispute", "return_kind_unsupported")]
    [InlineData("a refund", "return_kind_unsupported")]
    public async Task Staff_are_not_offered_a_return_the_engine_will_refuse(string what, string refusal)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new Sandbox { Status = "processing" };
        await using var h = await Setup(sandbox.Replay, ct);
        await HoldFees(h, 10m, ct);
        // Receipted, as a payout leaves it: without a receipt there is nothing to return and nothing to offer.
        var payment = await Paid(h, 200m, ct);
        sandbox.Lines[Payout] = LinesOf(PayoutLine(Payout, 198.40m), PaymentLine(h, payment, 1.60m));
        (await h.Deliver(PaidPayout(Payout, 198.40m), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await Bank(h, Payout, 198.40m, ct)).ShouldBe(0);
        (await h.Operation(payment.Id, ct)).Status.ShouldBe("Settled");
        switch (what)
        {
            case "a refund":
                (await h.Deliver(Refunded(StripeReplay.Delivered("017-webhook-connect.json", StripeReplay.RefundRun), h, payment, payment.ChargedAmount), ct))
                    .Status.ShouldBe(HttpStatusCode.NoContent);
                break;
            case "a card dispute":
                sandbox.HoldsCharge(Charge(h, payment, "009-card-dispute-charge.json", StripeReplay.CardDisputeRun));
                (await h.Deliver(Disputed(StripeReplay.Delivered("007-webhook-connect.json", StripeReplay.CardDisputeRun), payment), ct))
                    .Status.ShouldBe(HttpStatusCode.NoContent);
                break;
            default:
                sandbox.HoldsCharge(Charge(h, payment, "052-ach-dispute-charge.json"));
                (await h.Deliver(Disputed(StripeReplay.Delivered(DisputeCreated), payment), ct)).Status.ShouldBe(HttpStatusCode.NoContent);
                break;
        }
        await h.Run(ct);
        var entries = await h.JournalEntries(ct);

        await h.InOrg(async sp =>
        {
            var sender = sp.GetRequiredService<LeaseBook.SharedKernel.Cqrs.ISender>();
            var view = (await sender.Query(new GetPayments(null, payment.Id), ct)).ShouldHaveSingleItem();
            (view.Status, view.Reason, view.ReceiptRecorded, view.CanCloseReview).ShouldBe(("NeedsReview", "return_requires_review", true, true));
            view.CanPostReturn.ShouldBeFalse();

            var result = (await sender.Send(new PostPaymentReturn(h.Binding, payment.Id), ct)).ShouldNotBeNull();

            // The engine and the flag agree, before the attempt and after it.
            result.Refusal.ShouldBe(refusal);
            (result.Refusal is null).ShouldBe(view.CanPostReturn);
            result.Payment.CanPostReturn.ShouldBeFalse();
        }, ct);
        (await h.JournalEntries(ct)).ShouldBe(entries);
    }
}

public sealed partial class SimulatedPaymentTests
{
    [Fact]
    public async Task A_simulation_host_tells_staff_and_the_tenant_its_mode_and_a_host_without_payments_tells_nobody_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var tenant = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);

        var mine = (await tenant.GetFromJsonAsync<PaymentsResponse>(Path, ct))!;
        var staff = (await admin.GetFromJsonAsync<PaymentsResponse>("/api/payments", ct))!;

        (mine.Enabled, mine.Mode).ShouldBe((true, "Simulation"));
        (staff.Enabled, staff.Mode).ShouldBe((true, "Simulation"));

        // The same organization on a host with payments off: not enabled, and no mode to word anything by.
        using var elsewhere = fixture.Api.CreateClient();
        await elsewhere.PrimeCsrfAsync(ct);
        (await elsewhere.PostAsJsonAsync("/api/auth/login", new { email = h.AdminEmail, password = PaymentFixtureBootstrap.Password }, ct)).EnsureSuccessStatusCode();
        using var off = JsonDocument.Parse(await elsewhere.GetStringAsync("/api/payments", ct));
        (off.RootElement.GetProperty("enabled").GetBoolean(), off.RootElement.GetProperty("mode").ValueKind).ShouldBe((false, JsonValueKind.Null));
    }

    // The simulation's memos are what they were before a second mode had one of its own, to the byte.
    [Fact]
    public async Task The_simulations_receipt_payout_receipt_and_return_keep_their_memos_and_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var tenant = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var single = await Submit(tenant, 200m, ct);
        await h.Emit(single, "BankCredit", ct);
        var batched = await Submit(tenant, 200m, ct);
        await h.Tick(ct);
        await h.Settle(h.Payout("po_memo", 200m, h.Line("1", "Payment", batched, 200m, 0m, 200m)), ct);
        await h.Emit(single, "Return", ct);
        (await admin.PostAsync($"/api/payments/{single.Id}/return", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var first = await Staff(admin, single.Id, ct);
        var second = await Staff(admin, batched.Id, ct);
        (first.Status, second.Status).ShouldBe(("Returned", "Settled"));
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            foreach (var (id, sourceRef) in new[] { (first.ReceiptEntryId, $"sim-payment:{single.Id:N}:receipt"), (second.ReceiptEntryId, "payout:po_memo:1") })
            {
                var receipt = await db.Set<JournalEntry>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
                (receipt.Description, receipt.SourceRef, receipt.EventType).ShouldBe((PaymentReceiptShape.SimulationMemo, sourceRef, "PaymentReceived"));
                (await PaymentReceiptShape.LinesAsync(db, receipt.Id, ct)).ShouldBe(PaymentReceiptShape.TwoHundredByAch);
            }
            var reversal = await db.Set<JournalEntry>().AsNoTracking().SingleAsync(x => x.Id == first.ReturnEntryId, ct);
            (reversal.InternalNote, reversal.SourceRef).ShouldBe((PaymentReceiptShape.SimulationReturnNote, $"sim-payment:{single.Id:N}:return"));
        }, ct);
    }

    // The simulator's driver sends only what a fixture asked for, so a notice it drops is worth a
    // line at the level it always had. Its messages no longer call anything simulated either.
    [Fact]
    public async Task The_simulator_still_logs_a_notice_that_is_not_ours_and_an_unmapped_one_at_information()
    {
        var ct = TestContext.Current.CancellationToken;
        using var logs = new CapturingLoggerProvider();
        await using var h = await Setup(ct, processor: inner => new ElsewhereProcessor(inner), logs: logs);
        using var tenant = await h.Login(1, ct);
        var op = await Submit(tenant, 100m, ct);
        await h.Tick(ct);

        await h.Deliver(h.Observation(op, "BankCredit"), ct);
        // Not through the processor, which here reads nothing: a notice for another generation, as the runner gets it.
        await h.App.Services.GetRequiredService<PaymentRunner>()
            .ReceiveAsync(h.Observation(op, "BankCredit") with { Generation = UuidV7.NewId() }, ct);

        var ignored = logs.Entries.Where(x => x.EventId.Id == 4602).ToArray();
        ignored.Length.ShouldBe(2);
        ignored.ShouldAllBe(x => x.Level == LogLevel.Information && x.EventId.Name == "PaymentCallbackIgnored"
            && !x.Message.Contains("imulated", StringComparison.Ordinal));
        logs.Entries.Where(x => x.EventId.Id is >= 4600 and < 4700).ShouldAllBe(x => !x.Message.Contains("imulated", StringComparison.Ordinal));
        logs.Entries.ShouldContain(x => x.EventId.Id == 4600 && x.EventId.Name == "PaymentProcessed"
            && x.Message.StartsWith("Online payment " + op.Id + " processed in ", StringComparison.Ordinal));
    }
}
