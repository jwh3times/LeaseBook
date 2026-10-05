using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Periods;
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

/// <summary>
/// Payout batches in the simulation (ADR-053, #500): stored once, checked, and posted completely or
/// not at all. The figures of the first test are the worked batch in
/// <c>docs/payments/fee-and-settlement-spec.md</c>.
/// </summary>
public sealed partial class SimulatedPaymentTests
{
    private const string Settlements = "/api/payments/settlements";

    [Fact]
    public async Task The_worked_payout_posts_by_itself_and_ties_to_the_bank_amount_to_the_cent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        await HoldFees(h, 80m, ct);
        var clean = await Pay(h, two, 1000m, ct);    // quoted 30.17, charged 1,030.17
        var shortfall = await Pay(h, one, 500m, ct); // quoted 15.24, charged 515.24
        var surplus = await Pay(h, one, 250m, ct);   // quoted 7.78, charged 257.78
        var bankBefore = await BankBook(h, ct);

        var evidence = h.Payout("po_1", 1742.90m,
            h.Line("1", "Payment", clean, 1030.17m, 30.17m, 1000m),
            h.Line("2", "Payment", shortfall, 515.24m, 22.74m, 492.50m),
            h.Line("3", "Payment", surplus, 257.78m, 7.38m, 250.40m));
        await h.Settle(evidence, ct);

        var payout = (await List(admin, ct)).Single();
        (payout.PayoutReference, payout.Status, payout.Reason, payout.BankAmount, payout.CanPost, payout.CanClose)
            .ShouldBe(("po_1", "Posted", null, 1742.90m, false, false));
        payout.Items.Select(x => (x.Item, x.Kind, x.PaymentId, x.EntryId.HasValue, x.FeeEntryId.HasValue)).ShouldBe(
        [
            ("1", "Payment", clean.Id, true, false),
            ("2", "Payment", shortfall.Id, true, true),
            ("3", "Payment", surplus.Id, true, true),
        ]);
        foreach (var op in new[] { clean, shortfall, surplus }) { (await Staff(admin, op.Id, ct)).Status.ShouldBe("Settled"); }
        // Each tenant is credited what they paid toward the ledger, whatever the processor kept.
        (await h.Balance(1, ct), await h.Balance(2, ct)).ShouldBe((250m, 0m));
        (await BankBook(h, ct) - bankBefore).ShouldBe(1742.90m);
        (await HeldFees(h, ct)).ShouldBe(72.90m);

        // A redelivery changes nothing.
        await h.Settle(evidence, ct);
        (await BankBook(h, ct) - bankBefore).ShouldBe(1742.90m);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<PaymentEffect>().Where(x => x.SettlementId != null).Select(x => x.Kind).ToListAsync(ct))
                .ShouldBe(["Receipt", "Receipt", "Receipt", "PaymentFee", "PaymentFee"], ignoreOrder: true);
            (await db.Set<JournalEntry>().CountAsync(x => x.SourceRef != null && x.SourceRef.StartsWith("payout:po_1:"), ct)).ShouldBe(5);
            (await new InvariantChecks(db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
        (await one.GetAsync(Settlements, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    public static TheoryData<string, string> Held() => new()
    {
        { "bank amount a cent off", "settlement_untied" },
        { "a charge that is not the payment's", "settlement_untied" },
        { "a payment nobody knows", "settlement_incomplete" },
        { "a refund", "unsupported_settlement" },
        { "an instant payout", "unsupported_settlement" },
        { "a shortfall nothing covers", "pm_fees_insufficient" },
        { "a locked bank date", "settlement_period_locked" },
        { "a payment already receipted", "conflicting_evidence" },
    };

    [Theory]
    [MemberData(nameof(Held))]
    public async Task A_payout_that_cannot_post_says_why_and_posts_nothing(string scenario, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        var first = await Pay(h, one, 600m, ct);
        var second = await Pay(h, two, 400m, ct);
        var lines = new List<ProcessorSettlementItem>
        {
            h.Line("1", "Payment", first, 600m, 0m, 600m),
            h.Line("2", "Payment", second, 400m, 0m, 400m),
        };
        var evidence = h.Payout("po_1", 1000m, [.. lines]);
        switch (scenario)
        {
            case "bank amount a cent off": evidence = evidence with { BankAmount = 999.99m }; break;
            case "a charge that is not the payment's":
                lines[1] = h.Line("2", "Payment", second, 410m, 10m, 400m);
                evidence = evidence with { Items = lines };
                break;
            case "a payment nobody knows":
                lines[1] = lines[1] with { ProviderId = "sim_unknown" };
                evidence = evidence with { Items = lines };
                break;
            case "a refund":
                lines[1] = lines[1] with { Kind = "Refund", Net = -400m };
                evidence = evidence with { Items = lines, BankAmount = 200m };
                break;
            case "an instant payout": evidence = evidence with { PayoutType = "instant" }; break;
            case "a shortfall nothing covers":
                lines[1] = h.Line("2", "Payment", second, 400m, 7.50m, 392.50m);
                evidence = evidence with { Items = lines, BankAmount = 992.50m };
                break;
            case "a locked bank date":
                await h.InOrg(sp => sp.GetRequiredService<IAccountingPeriods>()
                    .CloseAsync(evidence.BankDate.Year, evidence.BankDate.Month, ct), ct);
                break;
            default: // the second payment settles on its own evidence before the payout arrives
                await h.Emit(second, "BankCredit", ct);
                break;
        }
        var before = (await BankBook(h, ct), await h.Balance(1, ct), await h.Balance(2, ct));

        await h.Settle(evidence, ct);

        var payout = (await List(admin, ct)).Single();
        (payout.Status, payout.Reason, payout.CanClose).ShouldBe(("NeedsReview", reason, true));
        payout.Items.ShouldAllBe(x => x.EntryId == null && x.FeeEntryId == null);
        // All or nothing: the clean first line did not post either.
        (await BankBook(h, ct), await h.Balance(1, ct), await h.Balance(2, ct)).ShouldBe(before);
        (await Staff(admin, first.Id, ct)).Status.ShouldBe("Processing");
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<PaymentEffect>()
            .CountAsync(x => x.SettlementId != null, ct)).ShouldBe(0), ct);
    }

    [Fact]
    public async Task A_payout_with_a_return_waits_for_an_administrator_who_posts_the_whole_of_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        var returned = await Pay(h, one, 600m, ct);
        await h.Settle(h.Payout("po_1", 600m, h.Line("1", "Payment", returned, 600m, 0m, 600m)), ct);
        (await h.Balance(1, ct)).ShouldBe(400m);
        var later = await Pay(h, two, 400m, ct);
        var bankBefore = await BankBook(h, ct);

        // 400.00 arrives and the earlier 600.00 goes back: the bank is debited the net 200.00.
        await h.Settle(h.Payout("po_2", -200m,
            h.Line("1", "Payment", later, 400m, 0m, 400m),
            h.Line("2", "Return", returned, 600m, 0m, -600m)), ct);
        var waiting = (await List(admin, ct)).Single(x => x.PayoutReference == "po_2");
        (waiting.Status, waiting.Reason, waiting.CanPost).ShouldBe(("NeedsReview", "settlement_requires_confirmation", true));
        (await BankBook(h, ct), await h.Balance(1, ct), await h.Balance(2, ct)).ShouldBe((bankBefore, 400m, 1000m));
        (await one.PostAsync($"{Settlements}/{waiting.Id}/post", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var response = await admin.PostAsync($"{Settlements}/{waiting.Id}/post", null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var posted = (await response.Content.ReadFromJsonAsync<SettlementView>(ct))!;
        (posted.Status, posted.Reason, posted.PostedAt.HasValue).ShouldBe(("Posted", null, true));
        (await BankBook(h, ct) - bankBefore).ShouldBe(-200m);
        (await h.Balance(1, ct), await h.Balance(2, ct)).ShouldBe((1000m, 600m));
        (await Staff(admin, returned.Id, ct)).Status.ShouldBe("Returned");
        (await Staff(admin, later.Id, ct)).Status.ShouldBe("Settled");
        // Posting again, and later progress about the returned payment, change nothing.
        (await admin.PostAsync($"{Settlements}/{waiting.Id}/post", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await h.Deliver(h.Observation(returned, "Processing") with { EventId = "late-progress" }, ct);
        await h.Tick(ct);
        (await Staff(admin, returned.Id, ct)).Status.ShouldBe("Returned");
        (await BankBook(h, ct) - bankBefore).ShouldBe(-200m);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var adminId = (await db.Users.SingleAsync(x => x.Email == h.AdminEmail, ct)).Id;
            (await db.Set<PaymentSettlement>().SingleAsync(x => x.PayoutId == "po_2", ct)).PostedBy.ShouldBe(adminId);
            (await new InvariantChecks(db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    [Fact]
    public async Task A_return_that_fails_its_guard_holds_the_whole_payout_which_can_then_only_be_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        var returned = await Pay(h, one, 600m, ct);
        await h.Settle(h.Payout("po_1", 600m, h.Line("1", "Payment", returned, 600m, 0m, 600m)), ct);
        await Disburse(h, 600m, ct);
        var later = await Pay(h, two, 100m, ct);
        await h.Settle(h.Payout("po_2", -500m,
            h.Line("1", "Payment", later, 100m, 0m, 100m),
            h.Line("2", "Return", returned, 600m, 0m, -600m)), ct);
        var waiting = (await List(admin, ct)).Single(x => x.PayoutReference == "po_2");
        var before = (await BankBook(h, ct), await h.Balance(1, ct), await h.Balance(2, ct));

        var refused = await admin.PostAsync($"{Settlements}/{waiting.Id}/post", null, ct);
        var body = await refused.Content.ReadAsStringAsync(ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("return_owner_funds_disbursed");

        // The reason and the line it stopped on are kept, and the clean payment in the payout did not post.
        var held = (await List(admin, ct)).Single(x => x.Id == waiting.Id);
        (held.Status, held.Reason, held.ReasonItem).ShouldBe(("NeedsReview", "return_owner_funds_disbursed", "2"));
        (await BankBook(h, ct), await h.Balance(1, ct), await h.Balance(2, ct)).ShouldBe(before);
        (await Staff(admin, later.Id, ct)).Status.ShouldBe("Processing");

        var close = $"{Settlements}/{waiting.Id}/close";
        (await admin.PostAsJsonAsync(close, new ClosePaymentReviewBody(" "), ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await one.PostAsJsonAsync(close, new ClosePaymentReviewBody("mine"), ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var closed = await admin.PostAsJsonAsync(close, new ClosePaymentReviewBody(" Recovered from the owner by hand. "), ct);
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync(ct));
        var view = (await closed.Content.ReadFromJsonAsync<SettlementView>(ct))!;
        (view.Status, view.ReviewNote, view.CanPost, view.CanClose)
            .ShouldBe(("Closed", "Recovered from the owner by hand.", false, false));
        (await admin.PostAsync($"{Settlements}/{waiting.Id}/post", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BankBook(h, ct), await h.Balance(1, ct), await h.Balance(2, ct)).ShouldBe(before);
    }

    [Fact]
    public async Task A_held_payout_posts_when_an_administrator_tries_again_after_the_cause_is_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 500m, ct);
        await h.Settle(h.Payout("po_1", 492.50m, h.Line("1", "Payment", op, 500m, 7.50m, 492.50m)), ct);
        var held = (await List(admin, ct)).Single();
        held.Reason.ShouldBe("pm_fees_insufficient");

        await HoldFees(h, 10m, ct);
        var response = await admin.PostAsync($"{Settlements}/{held.Id}/post", null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        (await h.Balance(1, ct)).ShouldBe(500m);
        (await HeldFees(h, ct)).ShouldBe(2.50m);
    }

    [Fact]
    public async Task The_same_payout_with_different_content_is_conflicting_and_can_only_be_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 500m, ct);
        var untied = h.Payout("po_1", 499m, h.Line("1", "Payment", op, 500m, 0m, 500m));
        await h.Settle(untied, ct);
        await h.Settle(untied with { BankAmount = 500m }, ct);

        var payout = (await List(admin, ct)).Single();
        (payout.Status, payout.Reason, payout.BankAmount, payout.CanPost, payout.CanClose)
            .ShouldBe(("NeedsReview", "conflicting_evidence", 499m, false, true));
        (await admin.PostAsync($"{Settlements}/{payout.Id}/post", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.Balance(1, ct)).ShouldBe(1000m);
        (await admin.PostAsJsonAsync($"{Settlements}/{payout.Id}/close", new ClosePaymentReviewBody("Provider corrected it."), ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Payouts_are_bound_to_the_organization_and_absent_without_the_simulation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var a = await Setup(ct);
        await using var b = await Setup(ct);
        using var one = await a.Login(1, ct);
        using var otherAdmin = await b.LoginAdmin(ct);
        var op = await Pay(a, one, 500m, ct);
        await a.Settle(a.Payout("po_1", 499m, a.Line("1", "Payment", op, 500m, 0m, 500m)), ct);
        using var admin = await a.LoginAdmin(ct);
        var payout = (await List(admin, ct)).Single();

        (await List(otherAdmin, ct)).ShouldBeEmpty();
        (await otherAdmin.PostAsync($"{Settlements}/{payout.Id}/post", null, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.PostAsJsonAsync($"{Settlements}/{payout.Id}/close", new ClosePaymentReviewBody("not mine"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // Evidence for one fixture's account is not accepted by another's engine.
        await b.InOrg(async sp => await Should.ThrowAsync<PaymentUnavailableException>(() => sp.GetRequiredService<SettlementEngine>()
            .ReceiveAsync(b.Binding, a.Payout("po_9", 1m), ct)), ct);
        (await List(admin, ct)).Single().Status.ShouldBe("NeedsReview");
        fixture.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText).ShouldNotContain(x => x != null && x.StartsWith(Settlements));
    }

    public static TheoryData<string, string, double, double, double> CraftedLines() => new()
    {
        // A return whose net is not the whole charge going back: reversing 600.00 against a 100.00
        // debit would have posted 500.00 of the tenant's money as the PM's fee surplus.
        { "a return that names an arbitrary net", "Return", 600, 0, -100 },
        // The same trick with a fee invented to make the line add up.
        { "a return that gives back more fee than was quoted", "Return", 600, 500, -100 },
        { "a return with a negative fee", "Return", 600, -10, -610 },
        { "a return of less than the whole charge", "Return", 300, 0, -300 },
    };

    [Theory]
    [MemberData(nameof(CraftedLines))]
    public async Task A_return_line_cannot_post_more_or_less_than_the_charge_going_back(
        string what, string kind, double gross, double fee, double net)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        await h.Settle(h.Payout("po_1", 600m, h.Line("1", "Payment", op, 600m, 0m, 600m)), ct);
        await HoldFees(h, 1000m, ct); // enough that no fee guard is what stops it
        var before = (await BankBook(h, ct), await HeldFees(h, ct), await h.Balance(1, ct));

        await h.Settle(h.Payout("po_2", (decimal)net, h.Line("1", kind, op, (decimal)gross, (decimal)fee, (decimal)net)), ct);
        var payout = (await List(admin, ct)).Single(x => x.PayoutReference == "po_2");
        var posted = await admin.PostAsync($"{Settlements}/{payout.Id}/post", null, ct);

        posted.StatusCode.ShouldBe(HttpStatusCode.Conflict, what);
        (await List(admin, ct)).Single(x => x.Id == payout.Id).Reason
            .ShouldBeOneOf("settlement_untied", "unsupported_settlement");
        (await BankBook(h, ct), await HeldFees(h, ct), await h.Balance(1, ct)).ShouldBe(before);
    }

    [Theory]
    [InlineData(-50, 650)]  // a negative fee inflating the net
    [InlineData(700, -100)] // a fee larger than the charge
    [InlineData(10, 600)]   // a net that is not the charge less the fee
    public async Task A_payment_line_whose_net_does_not_follow_from_its_charge_and_fee_posts_nothing(double fee, double net)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        await HoldFees(h, 1000m, ct);

        await h.Settle(h.Payout("po_1", (decimal)net, h.Line("1", "Payment", op, 600m, (decimal)fee, (decimal)net)), ct);

        (await List(admin, ct)).Single().Reason.ShouldBe("settlement_untied");
        (await h.Balance(1, ct), await HeldFees(h, ct)).ShouldBe((1000m, 1000m));
    }

    [Fact]
    public async Task Returning_a_fee_bearing_payment_costs_the_pm_the_fee_it_does_not_get_back_and_any_return_fee()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        await HoldFees(h, 80m, ct);
        var op = await Pay(h, one, 500m, ct); // quoted 15.24, charged 515.24
        await h.Settle(h.Payout("po_1", 500m, h.Line("1", "Payment", op, 515.24m, 15.24m, 500m)), ct);
        var bankBefore = await BankBook(h, ct);

        // The whole 515.24 goes back to the tenant, the processor keeps its 15.24, and charges 4.00 for the return.
        await h.Settle(h.Payout("po_2", -519.24m,
            h.Line("1", "Return", op, 515.24m, 0m, -515.24m),
            new ProcessorSettlementItem("2", "Fee", "", 0m, 4m, -4m, "USD")), ct);
        var payout = (await List(admin, ct)).Single(x => x.PayoutReference == "po_2");
        payout.Reason.ShouldBe("settlement_requires_confirmation");
        (await admin.PostAsync($"{Settlements}/{payout.Id}/post", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await BankBook(h, ct) - bankBefore).ShouldBe(-519.24m);
        (await HeldFees(h, ct)).ShouldBe(80m - 15.24m - 4m);
        (await h.Balance(1, ct)).ShouldBe(1000m); // the tenant owes exactly what they paid toward the ledger again
        var lines = (await List(admin, ct)).Single(x => x.Id == payout.Id).Items;
        lines.Select(x => (x.Item, x.Kind, x.PaymentId, x.EntryId.HasValue, x.FeeEntryId.HasValue)).ShouldBe(
            [("1", "Return", op.Id, true, true), ("2", "Fee", null, false, true)]);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<PaymentEffect>().Where(x => x.OperationId == op.Id).Select(x => x.Kind).ToListAsync(ct))
                .ShouldBe(["Receipt", "Return", "ReturnFee"], ignoreOrder: true);
            (await new InvariantChecks(db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    [Fact]
    public async Task A_surplus_in_a_payout_can_cover_a_shortfall_in_it_when_nothing_else_is_held()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        var shortfall = await Pay(h, one, 500m, ct); // quoted 15.24
        var surplus = await Pay(h, two, 250m, ct);   // quoted 7.78

        await h.Settle(h.Payout("po_1", 750.10m,
            h.Line("1", "Payment", shortfall, 515.24m, 15.54m, 499.70m),
            h.Line("2", "Payment", surplus, 257.78m, 7.38m, 250.40m)), ct);

        (await List(admin, ct)).Single().Status.ShouldBe("Posted");
        (await HeldFees(h, ct)).ShouldBe(0.10m);
        (await h.Balance(1, ct), await h.Balance(2, ct)).ShouldBe((500m, 750m));
    }

    [Fact]
    public async Task Evidence_that_cannot_be_stored_is_not_stored_and_evidence_that_cannot_be_posted_is_held()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        var line = h.Line("1", "Payment", op, 600m, 0m, 600m);

        // Two lines under one reference, an empty reference, and text that does not fit: not evidence
        // LeaseBook can keep as given. None of it is kept, and the sender is told it was not delivered.
        await h.Settle(h.Payout("po_dup", 1200m, line, line), ct, HttpStatusCode.BadRequest);
        await h.Settle(h.Payout("po_blank", 600m, line with { Item = " " }), ct, HttpStatusCode.BadRequest);
        await h.Settle(h.Payout("po_long", 600m, line with { Kind = new string('k', 21) }), ct, HttpStatusCode.BadRequest);
        await h.Settle(h.Payout(new string('p', 101), 600m, line), ct, HttpStatusCode.BadRequest);
        (await List(admin, ct)).ShouldBeEmpty();

        // Another currency is stored as it arrived and held: what evidence says is judged, not refused.
        await h.Settle(h.Payout("po_eur", 600m, line) with { Currency = "EUR" }, ct);
        await h.Settle(h.Payout("po_line_eur", 600m, line with { Currency = "EUR" }), ct);
        (await List(admin, ct)).Select(x => (x.PayoutReference, x.Status, x.Reason)).ShouldBe(
            [("po_eur", "NeedsReview", "unsupported_settlement"), ("po_line_eur", "NeedsReview", "unsupported_settlement")],
            ignoreOrder: true);
        (await h.Balance(1, ct)).ShouldBe(1000m);
    }

    [Fact]
    public async Task A_payout_whose_check_fails_technically_is_shown_as_waiting_and_can_be_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        // This ledger throws on a payout, as an outage would.
        await using var h = await Setup(ct, new AfterPostingFailure { Fail = false });
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);

        await h.Settle(h.Payout("po_1", 600m, h.Line("1", "Payment", op, 600m, 0m, 600m)), ct);

        var payout = (await List(admin, ct)).Single();
        (payout.Status, payout.Reason, payout.CanPost, payout.CanClose).ShouldBe(("NeedsReview", "technical_failure", true, true));
        (await h.Balance(1, ct)).ShouldBe(1000m);
        (await admin.PostAsJsonAsync($"{Settlements}/{payout.Id}/close", new ClosePaymentReviewBody("Posted by hand."), ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_redelivery_is_the_same_evidence_whatever_its_timestamp_or_line_order_and_never_reopens_an_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        var first = await Pay(h, one, 600m, ct);
        var second = await Pay(h, two, 400m, ct);
        var evidence = h.Payout("po_1", 999m,
            h.Line("1", "Payment", first, 600m, 0m, 600m), h.Line("2", "Payment", second, 400m, 0m, 400m));
        await h.Settle(evidence, ct);
        (await List(admin, ct)).Single().Reason.ShouldBe("settlement_untied");

        // Re-stamped and reordered: the same payout, so still merely untied and still postable.
        await h.Settle(evidence with { ObservedAt = evidence.ObservedAt.AddHours(3), Items = [.. evidence.Items.Reverse()] }, ct);
        var payout = (await List(admin, ct)).Single();
        (payout.Reason, payout.CanPost).ShouldBe(("settlement_untied", true));

        // Closed stays closed when different content arrives for it.
        (await admin.PostAsJsonAsync($"{Settlements}/{payout.Id}/close", new ClosePaymentReviewBody("Wrong amount."), ct))
            .EnsureSuccessStatusCode();
        await h.Settle(evidence with { BankAmount = 1000m }, ct);
        (await List(admin, ct)).Single().Status.ShouldBe("Closed");

        // Posted stays posted too.
        var third = await Pay(h, one, 100m, ct);
        var good = h.Payout("po_2", 100m, h.Line("1", "Payment", third, 100m, 0m, 100m));
        await h.Settle(good, ct);
        await h.Settle(good with { BankAmount = 99m }, ct);
        (await List(admin, ct)).Single(x => x.PayoutReference == "po_2").Status.ShouldBe("Posted");
    }

    [Fact]
    public async Task A_payment_settled_or_returned_by_a_payout_is_not_demoted_by_its_own_single_payment_notices()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        await HoldFees(h, 80m, ct);

        // Settled by a payout with a fee difference; then the bank credit notice for the same money
        // arrives, carrying that fee. It is not a second settlement and not a reason for review.
        var settled = await Pay(h, one, 500m, ct);
        await h.Settle(h.Payout("po_1", 492.50m, h.Line("1", "Payment", settled, 515.24m, 22.74m, 492.50m)), ct);
        await h.Deliver(h.Observation(settled, "BankCredit") with { Fee = 22.74m, Net = 492.50m }, ct);
        await h.Tick(ct);
        var view = await Staff(admin, settled.Id, ct);
        (view.Status, view.Reason).ShouldBe(("Settled", null));
        (await h.Balance(1, ct)).ShouldBe(500m);

        // The return notice arrives first, as it usually would; the payout then posts the return; and
        // later progress about the payment leaves it returned.
        var returned = await Pay(h, two, 400m, ct);
        await h.Settle(h.Payout("po_2", 400m, h.Line("1", "Payment", returned, returned.ChargedAmount, returned.QuotedFee, 400m)), ct);
        await h.Emit(returned, "Return", ct);
        (await Staff(admin, returned.Id, ct)).Reason.ShouldBe("return_requires_review");
        await h.Settle(h.Payout("po_3", -400m, h.Line("1", "Return", returned, returned.ChargedAmount, returned.QuotedFee, -400m)), ct);
        var payout = (await List(admin, ct)).Single(x => x.PayoutReference == "po_3");
        (await admin.PostAsync($"{Settlements}/{payout.Id}/post", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await h.Deliver(h.Observation(returned, "Processing") with { EventId = "late-progress" }, ct);
        await h.Tick(ct);
        (await Staff(admin, returned.Id, ct)).Status.ShouldBe("Returned");
        (await h.Balance(2, ct)).ShouldBe(1000m);

        // A dispute is never answered by a posted return.
        await h.Deliver(h.Observation(returned, "Dispute"), ct);
        await h.Tick(ct);
        var disputed = await Staff(admin, returned.Id, ct);
        (disputed.Status, disputed.Reason).ShouldBe(("NeedsReview", "evidence_after_return"));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("review closed")]
    public async Task A_payout_does_not_settle_or_return_a_payment_behind_a_decision_already_recorded(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        ProcessorSettlement evidence;
        if (scenario == "failed")
        {
            // The collection failed; a payout that then pays it out contradicts that.
            await h.Emit(op, "Failed", ct);
            evidence = h.Payout("po_1", 600m, h.Line("1", "Payment", op, 600m, 0m, 600m));
        }
        else
        {
            // Staff closed the return review after correcting the books by hand; a payout must not reverse it again.
            await h.Emit(op, "BankCredit", ct);
            await h.Emit(op, "Return", ct);
            (await admin.PostAsJsonAsync($"/api/payments/{op.Id}/close-review", new ClosePaymentReviewBody("Corrected by hand."), ct))
                .EnsureSuccessStatusCode();
            evidence = h.Payout("po_1", -600m, h.Line("1", "Return", op, 600m, 0m, -600m));
        }
        var before = (await BankBook(h, ct), await h.Balance(1, ct));

        await h.Settle(evidence, ct);

        var payout = (await List(admin, ct)).Single();
        (payout.Status, payout.Reason, payout.CanPost).ShouldBe(("NeedsReview", "conflicting_evidence", false));
        (await BankBook(h, ct), await h.Balance(1, ct)).ShouldBe(before);
    }

    [Fact]
    public async Task The_payout_callback_accepts_only_signed_evidence_and_ignores_another_fixtures_account()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        var evidence = h.Payout("po_1", 600m, h.Line("1", "Payment", op, 600m, 0m, 600m));
        var body = JsonSerializer.SerializeToUtf8Bytes(evidence);
        var signature = h.App.Services.GetRequiredService<SimulatedProcessor>().Sign(body);

        // A changed body under a good signature, a made-up signature, and no signature at all.
        var tampered = JsonSerializer.SerializeToUtf8Bytes(evidence with { BankAmount = 6000m });
        (await h.PostPayout(tampered, signature, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostPayout(body, signature[..^4] + "0000", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostPayout(body, "", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // Not evidence at all, and amounts in fractions of a cent, even when signed.
        var junk = "not json"u8.ToArray();
        (await h.PostPayout(junk, h.App.Services.GetRequiredService<SimulatedProcessor>().Sign(junk), ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await h.Settle(evidence with { BankAmount = 600.005m }, ct, HttpStatusCode.BadRequest);
        (await List(admin, ct)).ShouldBeEmpty();
        (await h.Balance(1, ct)).ShouldBe(1000m);

        // Signed evidence for an account no fixture here is bound to is acknowledged and ignored.
        await h.Settle(evidence with { Account = "sim_someone_else" }, ct);
        (await List(admin, ct)).ShouldBeEmpty();

        await h.Settle(evidence, ct);
        (await List(admin, ct)).Single().Status.ShouldBe("Posted");
        (await h.Balance(1, ct)).ShouldBe(400m);
    }

    [Fact]
    public async Task The_fixture_cli_drives_a_clean_payout_a_fee_difference_a_return_and_the_held_cases()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        await HoldFees(h, 80m, ct);
        var org = h.Binding.OrgId.ToString();
        var today = DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd");
        // Submitted and not yet dispatched: the command runs the worker itself before delivering.
        var clean = await Submit(one, 500m, "card", ct);     // quoted 15.24
        var shortfall = await Submit(two, 250m, "card", ct); // quoted 7.78

        // No fee named: the processor kept exactly the quoted fee. A fee named: what it kept instead.
        (await h.Cli(ct, "payout", org, today, "po_1", $"pay:{clean.Id}", $"pay:{shortfall.Id}:9.28")).ShouldBe(0);
        var first = (await List(admin, ct)).Single();
        (first.Status, first.BankAmount).ShouldBe(("Posted", 500m + 248.50m));
        (await h.Balance(1, ct), await h.Balance(2, ct)).ShouldBe((500m, 750m));
        (await HeldFees(h, ct)).ShouldBe(78.50m);

        // A return with the processor keeping its fee, and a return fee beside it. It waits for a person.
        (await h.Cli(ct, "payout", org, today, "po_2", $"return:{clean.Id}:0", "fee:4.00")).ShouldBe(0);
        var second = (await List(admin, ct)).Single(x => x.PayoutReference == "po_2");
        (second.Status, second.Reason, second.BankAmount).ShouldBe(("NeedsReview", "settlement_requires_confirmation", -519.24m));
        (await admin.PostAsync($"{Settlements}/{second.Id}/post", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await h.Balance(1, ct)).ShouldBe(1000m);

        // The held cases an operator needs to be able to produce: untied, unsupported kind, unsupported type.
        var third = await Submit(two, 100m, "card", ct);
        (await h.Cli(ct, "payout", org, today, "po_3", $"pay:{third.Id}", "--bank-amount=99.00")).ShouldBe(0);
        (await h.Cli(ct, "payout", org, today, "po_4", $"refund:{shortfall.Id}")).ShouldBe(0);
        (await h.Cli(ct, "payout", org, today, "po_5", $"pay:{third.Id}", "--type=instant")).ShouldBe(0);
        (await List(admin, ct)).Where(x => x.Status == "NeedsReview").Select(x => (x.PayoutReference, x.Reason)).ShouldBe(
            [("po_3", "settlement_untied"), ("po_4", "unsupported_settlement"), ("po_5", "unsupported_settlement")], ignoreOrder: true);

        // A payment the fixture does not have is refused before anything is delivered.
        (await h.Cli(ct, "payout", org, today, "po_6", $"pay:{UuidV7.NewId()}")).ShouldBe(1);
        (await List(admin, ct)).ShouldNotContain(x => x.PayoutReference == "po_6");
    }

    public static TheoryData<string[]> BadPayoutArguments() => new()
    {
        new[] { "po_1" },                                  // no lines
        new[] { "po_1", "pay:not-a-guid" },
        new[] { "po_1", "pay:00000000-0000-0000-0000-000000000001:-1" },
        new[] { "po_1", "pay:00000000-0000-0000-0000-000000000001:1.005" },
        new[] { "po_1", "refund:00000000-0000-0000-0000-000000000001:5" },
        new[] { "po_1", "fee:0" },
        new[] { "po_1", "fee:abc" },
        new[] { "po_1", "wire:00000000-0000-0000-0000-000000000001" },
        new[] { "po_1", "fee:4.00", "--bank-amount=abc" },
        new[] { "--type=instant", "fee:4.00" },            // an option where the payout id belongs
    };

    [Theory]
    [MemberData(nameof(BadPayoutArguments))]
    public void The_payout_command_refuses_arguments_it_cannot_read(string[] arguments)
    {
        PayoutRequest.TryParse(arguments[0], arguments[1..], out _).ShouldBeFalse();
        new PaymentSimulationVerb().TryCreateInvocation(
            ["payment-simulation", "payout", Guid.NewGuid().ToString(), "2026-10-05", .. arguments], out _, out var usage).ShouldBeFalse();
        usage.ShouldContain("payout <org-id>");
    }

    private static async Task<PaymentView> Submit(HttpClient tenant, decimal amount, string method, CancellationToken ct)
    {
        var quote = await Quote(tenant, amount, method, ct);
        var response = await tenant.PostAsJsonAsync(Path, new SubmitPaymentBody(UuidV7.NewId(), amount, "USD", method, quote.Fee), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<PaymentView>(ct))!;
    }

    [Fact]
    public async Task Funds_in_transit_are_what_was_collected_and_not_yet_banked_and_never_touch_a_balance_or_the_journal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        var first = await Pay(h, one, 500m, ct);  // charged 515.24: only the 500.00 is the tenant's ledger amount
        var second = await Pay(h, two, 250m, ct);
        var failing = await Pay(h, two, 100m, ct);
        var entries = await JournalEntries(h, ct);

        // Submitted and accepted is not collected: nothing is in transit until the processor says so.
        (await InTransit(one, ct), await InTransit(two, ct), await InTransit(admin, ct, staff: true)).ShouldBe((0m, 0m, 0m));
        foreach (var op in new[] { first, second, failing }) { await h.Emit(op, "Succeeded", ct); }

        // Each tenant sees their own; staff see the organization's. The ledger amount, never the charge.
        (await InTransit(one, ct), await InTransit(two, ct), await InTransit(admin, ct, staff: true)).ShouldBe((500m, 350m, 850m));
        // It is a figure beside the balance. The balance and the journal have not moved.
        (await h.Balance(1, ct), await h.Balance(2, ct), await JournalEntries(h, ct)).ShouldBe((1000m, 1000m, entries));

        // What Operations reads through its own port: per tenant, with the date the tenant paid.
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var tenants = await db.Set<PaymentOperation>().Where(x => x.Id == first.Id || x.Id == second.Id)
                .ToDictionaryAsync(x => x.Id, x => x.TenantId, ct);
            var paidOn = DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime);
            var map = await sp.GetRequiredService<Modules.Operations.Contracts.IFundsInTransit>()
                .GetAsync([tenants[first.Id], tenants[second.Id], UuidV7.NewId()], ct);
            map.Count.ShouldBe(2);
            (map[tenants[first.Id]].Amount, map[tenants[first.Id]].PaidOn).ShouldBe((500m, paidOn));
            map[tenants[second.Id]].Amount.ShouldBe(350m);
        }, ct);

        // A payment that fails leaves transit; one the bank receives leaves it for the ledger.
        await h.Emit(failing, "Failed", ct);
        (await InTransit(two, ct)).ShouldBe(250m);
        await h.Settle(h.Payout("po_1", 500m, h.Line("1", "Payment", first, first.ChargedAmount, first.QuotedFee, 500m)), ct);
        (await InTransit(one, ct), await InTransit(admin, ct, staff: true)).ShouldBe((0m, 250m));
        (await h.Balance(1, ct)).ShouldBe(500m);
    }

    private static async Task<decimal> InTransit(HttpClient client, CancellationToken ct, bool staff = false) =>
        (await client.GetFromJsonAsync<PaymentsResponse>(staff ? "/api/payments" : Path, ct))!.FundsInTransit;

    private static async Task<int> JournalEntries(Harness h, CancellationToken ct)
    {
        var count = 0;
        await h.InOrg(async sp => count = await sp.GetRequiredService<AppDbContext>().Set<JournalEntry>().CountAsync(ct), ct);
        return count;
    }

    // Submits a payment at the fee quoted for it, and runs the worker so that the provider reference exists.
    private static async Task<PaymentView> Pay(Harness h, HttpClient tenant, decimal amount, CancellationToken ct)
    {
        var quote = await Quote(tenant, amount, "card", ct);
        var response = await tenant.PostAsJsonAsync(Path, new SubmitPaymentBody(UuidV7.NewId(), amount, "USD", "card", quote.Fee), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(ct));
        var op = (await response.Content.ReadFromJsonAsync<PaymentView>(ct))!;
        await h.Tick(ct);
        return op;
    }

    private static async Task<IReadOnlyList<SettlementView>> List(HttpClient staff, CancellationToken ct) =>
        (await staff.GetFromJsonAsync<SettlementsResponse>(Settlements, ct))!.Items;

    private static Task HoldFees(Harness h, decimal amount, CancellationToken ct) => h.InOrg(sp =>
        sp.GetRequiredService<IAccountingEvents>().PostAsync(new InterestEarned(new Money(amount),
            DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime).AddDays(-1), h.Binding.BankId, "Fees held"), ct), ct);

    private static async Task<decimal> HeldFees(Harness h, CancellationToken ct)
    {
        decimal held = 0;
        await h.InOrg(async sp => held = await new BalanceReader(sp.GetRequiredService<AppDbContext>())
            .HeldFeesAsync(h.Binding.BankId, ct), ct);
        return held;
    }

    private static async Task<decimal> BankBook(Harness h, CancellationToken ct)
    {
        decimal book = 0;
        await h.InOrg(async sp => book = (await new GetBankRegisterHandler(sp.GetRequiredService<AppDbContext>())
            .Handle(new GetBankRegister(h.Binding.BankId), ct)).Totals.Book, ct);
        return book;
    }

    // Pays the fixture's one owner out of the trust bank, as a disbursement run would.
    private static Task Disburse(Harness h, decimal amount, CancellationToken ct) => h.InOrg(async sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var ownerId = await db.Set<JournalLine>().Where(x => x.AccountClass == AccountClass.OwnerEquity && x.OwnerId != null)
            .Select(x => x.OwnerId!.Value).FirstAsync(ct);
        await sp.GetRequiredService<IAccountingEvents>().PostAsync(new OwnerDisbursed(ownerId, new Money(amount),
            DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime), h.Binding.BankId, "Owner draw"), ct);
    }, ct);
}
