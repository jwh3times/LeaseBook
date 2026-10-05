using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class SimulatedPaymentTests(PostgresFixture fixture)
{
    private const string Path = "/api/portal/tenant/payments";

    [Theory]
    [InlineData(600, 400)]
    [InlineData(1200, -200)]
    public async Task Bank_evidence_alone_posts_once_with_real_attribution_and_invariants(decimal amount, decimal balance)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var op = await Submit(client, amount, ct);
        foreach (var kind in new[] { "Processing", "Succeeded", "Available", "PayoutPaid" })
        {
            await h.Emit(op, kind, ct);
            (await Read(client, op.Id, ct)).Status.ShouldBe("Processing");
            (await h.Balance(1, ct)).ShouldBe(1000m);
        }
        var credit = h.Observation(op, "BankCredit");
        await h.Deliver(credit, ct);
        await h.Tick(ct);
        (await Read(client, op.Id, ct)).Status.ShouldBe("Settled");
        (await h.Balance(1, ct)).ShouldBe(balance);
        await Task.WhenAll(h.Deliver(credit, ct), h.Deliver(credit with { EventId = "semantic-duplicate" }, ct));
        await h.Deliver(h.Observation(op, "Processing") with { EventId = "late-progress" }, ct);
        await h.Tick(ct); // late progress cannot regress settled
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<PaymentEffect>().CountAsync(ct)).ShouldBe(1);
            (await db.Set<JournalEntry>().CountAsync(x => x.SourceRef == $"sim-payment:{op.Id:N}:receipt", ct)).ShouldBe(1);
            (await new InvariantChecks(db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
        (await Read(client, op.Id, ct)).Status.ShouldBe("Settled");
    }

    [Fact]
    public async Task Concurrent_request_keys_replay_and_changed_payload_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var body = new SubmitPaymentBody(UuidV7.NewId(), 100m, "USD");
        var responses = await Task.WhenAll(client.PostAsJsonAsync(Path, body, ct), client.PostAsJsonAsync(Path, body, ct));
        var first = (await responses[0].Content.ReadFromJsonAsync<PaymentView>(ct))!;
        responses.ShouldAllBe(x => x.StatusCode == HttpStatusCode.Accepted);
        (await responses[1].Content.ReadFromJsonAsync<PaymentView>(ct))!.Id.ShouldBe(first.Id);
        (await client.PostAsJsonAsync(Path, body with { Amount = 101m }, ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await h.Deliver(h.Observation(first, "BankCredit"), ct); // callback before dispatch mapping
        await Task.WhenAll(h.Tick(ct), h.Tick(ct));
        (await Read(client, first.Id, ct)).Status.ShouldBe("Settled");
        (await client.PostAsJsonAsync(Path, body, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<PaymentOperation>().CountAsync(ct)).ShouldBe(1), ct);
    }

    [Theory]
    [InlineData("Failed", "Failed", false)]
    [InlineData("Return", "NeedsReview", false)]
    [InlineData("PayoutFailed", "Processing", false)]
    public async Task Failure_return_and_payout_failure_do_not_invent_receipts(string kind, string expected, bool receipt)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var op = await Submit(client, 100m, ct);
        await h.Emit(op, kind, ct);
        var result = await Read(client, op.Id, ct);
        result.Status.ShouldBe(expected); result.ReceiptRecorded.ShouldBe(receipt);
        (await h.Balance(1, ct)).ShouldBe(1000m);
        if (kind == "Failed")
        {
            await h.Emit(op, "Processing", ct);
            (await Read(client, op.Id, ct)).Status.ShouldBe("Failed");
            await h.Emit(op, "BankCredit", ct);
            (await Read(client, op.Id, ct)).Status.ShouldBe("NeedsReview");
        }
    }

    [Fact]
    public async Task Net_fee_and_late_return_require_review_without_corrective_posting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var net = await Submit(client, 1000m, ct);
        await h.Deliver(h.Observation(net, "BankCredit") with { Fee = 30m, Net = 970m }, ct);
        await h.Tick(ct);
        (await Read(client, net.Id, ct)).Reason.ShouldBe("unsupported_settlement");
        (await h.Balance(1, ct)).ShouldBe(1000m);
        var gross = await Submit(client, 1200m, ct);
        await h.Emit(gross, "BankCredit", ct);
        await h.Emit(gross, "Return", ct);
        var result = await Read(client, gross.Id, ct);
        result.Status.ShouldBe("NeedsReview"); result.ReceiptRecorded.ShouldBeTrue();
        (await h.Balance(1, ct)).ShouldBe(-200m);
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<PaymentEffect>().CountAsync(ct)).ShouldBe(1), ct);
    }

    [Fact]
    public async Task Signature_mode_account_generation_and_event_content_are_bound()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var op = await Submit(client, 100m, ct);
        using var callback = h.App.CreateClient();
        var credit = h.Observation(op, "BankCredit");
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/callbacks/payments/simulation")
        { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(credit)) };
        invalid.Headers.Add("X-Simulation-Signature", "bad");
        (await callback.SendAsync(invalid, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await h.Deliver(credit with { Mode = "Live" }, ct);
        await h.Deliver(credit with { Account = "sim_unknown" }, ct);
        await h.Deliver(credit with { Generation = UuidV7.NewId() }, ct);
        await h.Tick(ct);
        (await h.Balance(1, ct)).ShouldBe(1000m);
        await h.Deliver(credit, ct);
        await h.Deliver(credit with { Gross = 101m }, ct);
        await h.Tick(ct);
        (await Read(client, op.Id, ct)).Reason.ShouldBe("conflicting_evidence");
        (await h.Balance(1, ct)).ShouldBe(1000m);
    }

    [Fact]
    public async Task Tenant_selectors_csrf_and_revoked_links_cannot_bypass_ownership()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var a = await h.Login(1, ct);
        using var b = await h.Login(2, ct);
        var op = await Submit(a, 100m, ct);
        (await b.GetAsync(Path + "/" + op.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await a.PostAsJsonAsync(Path, new { key = UuidV7.NewId(), amount = 10, currency = "USD", tenantId = UuidV7.NewId() }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        a.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        (await a.PostAsJsonAsync(Path, new SubmitPaymentBody(UuidV7.NewId(), 10, "USD"), ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var userId = await db.Users.Where(x => x.OrgId == h.Binding.OrgId && x.Email == h.Email(1)).Select(x => x.Id).SingleAsync(ct);
            await sp.GetRequiredService<ResidentAccessService>().RevokeAsync(userId, ct);
        }, ct);
        (await a.GetAsync(Path, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await h.Emit(op, "BankCredit", ct); // accepted work survives loss of the login link
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<PaymentEffect>().CountAsync(ct)).ShouldBe(1), ct);
    }

    [Fact]
    public async Task Ledger_failure_rolls_back_receipt_and_effect_then_retries_the_same_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        var fault = new AfterPostingFailure();
        await using var h = await Setup(ct, fault);
        using var client = await h.Login(1, ct);
        var op = await Submit(client, 600m, ct);
        await h.Emit(op, "BankCredit", ct);
        (await Read(client, op.Id, ct)).Reason.ShouldBe("technical_failure");
        (await h.Balance(1, ct)).ShouldBe(1000m);
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<PaymentEffect>().CountAsync(ct)).ShouldBe(0), ct);
        await h.Tick(ct);
        (await Read(client, op.Id, ct)).Status.ShouldBe("Settled");
        (await h.Balance(1, ct)).ShouldBe(400m);
    }

    [Fact]
    public async Task Locked_period_retains_evidence_and_never_moves_the_posting_date()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var op = await Submit(client, 100m, ct);
        await h.InOrg(sp => sp.GetRequiredService<IAccountingPeriods>().CloseAsync(h.Clock.GetUtcNow().Year, h.Clock.GetUtcNow().Month, ct), ct);
        await h.Emit(op, "BankCredit", ct);
        (await Read(client, op.Id, ct)).Reason.ShouldBe("accounting_period_locked");
        (await h.Balance(1, ct)).ShouldBe(1000m);
    }

    [Fact]
    public async Task Another_host_recovers_provider_acceptance_without_creating_another_collection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var op = await Submit(client, 100m, ct);
        string fingerprint = "";
        await h.InOrg(async sp => fingerprint = await sp.GetRequiredService<AppDbContext>()
            .Set<PaymentOperation>().Where(x => x.Id == op.Id).Select(x => x.Fingerprint).SingleAsync(ct), ct);
        var request = new ProcessorRequest(h.Binding, op.Id, fingerprint);
        await h.InOrg(async sp =>
            (await sp.GetRequiredService<PaymentEngine>().ClaimAsync(h.Binding, op.Id, ct)).ShouldNotBeNull(), ct);
        await h.App.Services.GetRequiredService<IPaymentProcessor>().SubmitAsync(request, ct);
        // Provider acceptance committed, but the application has not saved any provider mapping.
        // Simulate a crashed worker, then let its persisted lease expire on the next host.
        for (var i = 0; i < 16; i++) { h.Clock.Advance(); }
        await using var restarted = h.App.WithWebHostBuilder(_ => { });
        await restarted.Services.GetRequiredService<PaymentRunner>().RunOnceAsync(ct);
        await h.Deliver(h.Observation(op, "BankCredit"), ct);
        h.Clock.Advance();
        await restarted.Services.GetRequiredService<PaymentRunner>().RunOnceAsync(ct);
        (await Read(client, op.Id, ct)).Status.ShouldBe("Settled");
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<SimulatedCollection>().CountAsync(ct)).ShouldBe(1);
            (await db.Set<PaymentEffect>().CountAsync(ct)).ShouldBe(1);
            (await sp.GetRequiredService<PaymentEngine>().RetryAsync(h.Binding, op.Id, ct)).ShouldBeTrue();
            (await db.Set<PaymentOperation>().SingleAsync(x => x.Id == op.Id, ct)).Status.ShouldBe("Settled");
        }, ct);
    }

    [Fact]
    public async Task Foreign_org_cannot_read_submit_retry_or_rebind_existing_fixture_data()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var a = await Setup(ct);
        await using var b = await Setup(ct);
        using var first = await a.Login(1, ct);
        using var other = await b.Login(1, ct);
        var op = await Submit(first, 100m, ct);
        (await other.GetAsync(Path + "/" + op.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await b.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<PaymentOperation>().IgnoreQueryFilters().CountAsync(x => x.Id == op.Id, ct)).ShouldBe(0);
            (await sp.GetRequiredService<PaymentEngine>().RetryAsync(b.Binding, op.Id, ct)).ShouldBeFalse();
            await Should.ThrowAsync<PaymentUnavailableException>(() => sp.GetRequiredService<PaymentEngine>().RequireFixtureAsync(a.Binding, ct));
        }, ct);
        await Should.ThrowAsync<InvalidOperationException>(() => PaymentFixtureBootstrap.SeedAsync(fixture.Api.Services, a.Binding, "bad", ct));
        await using var connection = await fixture.OpenAppConnectionAsync(ct);
        await using var query = new Npgsql.NpgsqlCommand("SELECT count(*) FROM payment_operations", connection);
        (await query.ExecuteScalarAsync(ct)).ShouldBe(0L); // no context, despite rows existing
        using var ordinary = fixture.Api.CreateClient();
        (await ordinary.PostAsJsonAsync(Path, new SubmitPaymentBody(UuidV7.NewId(), 100, "USD"), ct)).StatusCode
            .ShouldNotBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Concurrent_distinct_payments_split_the_real_receivable_under_accounting_locks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var a = await Submit(client, 700m, ct); var b = await Submit(client, 700m, ct);
        await h.Deliver(h.Observation(a, "BankCredit"), ct);
        await h.Deliver(h.Observation(b, "BankCredit"), ct);
        var runner = h.App.Services.GetRequiredService<PaymentRunner>();
        await Task.WhenAll(runner.ProcessAsync(h.Binding, a.Id, ct), runner.ProcessAsync(h.Binding, b.Id, ct));
        (await h.Balance(1, ct)).ShouldBe(-400m);
        await h.InOrg(async sp => (await new InvariantChecks(sp.GetRequiredService<AppDbContext>()).CheckCoreAsync(ct)).ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task Stale_signature_and_incomplete_settlement_never_post()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var op = await Submit(client, 100m, ct);
        var processor = h.App.Services.GetRequiredService<SimulatedProcessor>();
        var body = JsonSerializer.SerializeToUtf8Bytes(h.Observation(op, "BankCredit"));
        var signature = processor.Sign(body);
        for (var i = 0; i < 151; i++) { h.Clock.Advance(); }
        processor.VerifyAndNormalize(body, signature).ShouldBeNull();
        await h.Deliver(h.Observation(op, "BankCredit") with { Complete = false }, ct);
        await h.Tick(ct);
        (await Read(client, op.Id, ct)).Status.ShouldBe("Processing");
        (await h.Balance(1, ct)).ShouldBe(1000m);
    }

    [Fact]
    public async Task Retired_bank_after_submission_requires_review_without_receipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        var op = await Submit(client, 100m, ct);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var bank = await db.Set<Modules.Directory.Domain.BankAccount>().SingleAsync(x => x.Id == h.Binding.BankId, ct);
            bank.IsActive = false;
            await db.SaveChangesAsync(ct);
        }, ct);
        await h.Emit(op, "BankCredit", ct);
        var result = await Read(client, op.Id, ct);
        result.Reason.ShouldBe("attribution_unavailable");
        result.ReceiptRecorded.ShouldBeFalse();
        (await h.Balance(1, ct)).ShouldBe(1000m);
    }

    [Fact]
    public async Task A_full_return_posts_one_linked_reversal_on_the_evidence_date_only_when_an_admin_asks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var tenant = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Submit(tenant, 600m, ct);
        await h.Emit(op, "BankCredit", ct);
        var returned = h.Observation(op, "Return");
        await h.Deliver(returned, ct);
        await h.Tick(ct);

        // Evidence alone posts nothing: the receipt stands until a person acts.
        (await h.Balance(1, ct)).ShouldBe(400m);
        var inReview = await Staff(admin, op.Id, ct);
        inReview.Status.ShouldBe("NeedsReview");
        inReview.CanPostReturn.ShouldBeTrue();
        // The tenant's view carries none of the staff review surface.
        var mine = await Read(tenant, op.Id, ct);
        (mine.CanPostReturn, mine.CanCloseReview, mine.ReceiptEntryId, mine.ReviewNote).ShouldBe((false, false, null, null));
        (await tenant.PostAsync($"/api/payments/{op.Id}/return", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var response = await admin.PostAsync($"/api/payments/{op.Id}/return", null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var posted = (await response.Content.ReadFromJsonAsync<PaymentView>(ct))!;
        posted.Status.ShouldBe("Returned");
        posted.Reason.ShouldBeNull();
        posted.ReceiptEntryId.ShouldNotBeNull();
        posted.ReturnEntryId.ShouldNotBeNull();
        (await h.Balance(1, ct)).ShouldBe(1000m);

        // A repeated action and a replayed observation both find the effect and post nothing more.
        (await admin.PostAsync($"/api/payments/{op.Id}/return", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await h.Deliver(returned, ct);
        await h.Deliver(returned with { EventId = "replayed-return" }, ct);
        await h.Tick(ct);
        (await Read(tenant, op.Id, ct)).Status.ShouldBe("Returned");
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var reversal = await db.Set<JournalEntry>().SingleAsync(x => x.ReversesEntryId == posted.ReceiptEntryId, ct);
            reversal.Id.ShouldBe(posted.ReturnEntryId!.Value);
            reversal.EntryDate.ShouldBe(returned.BankDate);
            reversal.SourceRef.ShouldBe($"sim-payment:{op.Id:N}:return");
            (await db.Set<PaymentEffect>().Where(x => x.OperationId == op.Id).Select(x => x.Kind).ToListAsync(ct))
                .ShouldBe(["Receipt", "Return"], ignoreOrder: true);
            (await new InvariantChecks(db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
        (await h.Balance(1, ct)).ShouldBe(1000m);

        // A posted return answers the evidence it was posted from, not what arrives afterwards.
        await h.Deliver(h.Observation(op, "Dispute"), ct);
        await h.Tick(ct);
        var again = await Staff(admin, op.Id, ct);
        (again.Status, again.Reason, again.CanPostReturn, again.CanCloseReview)
            .ShouldBe(("NeedsReview", "evidence_after_return", false, true));
        again.ReturnEntryId.ShouldBe(posted.ReturnEntryId);
        (await admin.PostAsync($"/api/payments/{op.Id}/return", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.Balance(1, ct)).ShouldBe(1000m);
    }

    [Theory]
    [InlineData("prepayment", "return_prepayment_consumed")]
    [InlineData("disbursed", "return_owner_funds_disbursed")]
    [InlineData("locked", "return_period_locked")]
    public async Task A_return_that_would_break_a_balance_or_a_lock_is_refused_by_name_and_posts_nothing(string guard, string code)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var tenant = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Submit(tenant, 1200m, ct); // 1,000 to the receivable, 200 to prepaid credit
        await h.Emit(op, "BankCredit", ct);
        await h.Emit(op, "Return", ct);
        var today = DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var receipt = await db.Set<JournalEntry>().SingleAsync(x => x.SourceRef == $"sim-payment:{op.Id:N}:receipt", ct);
            var dims = await db.Set<JournalLine>().SingleAsync(x => x.EntryId == receipt.Id
                && x.AccountClass == AccountClass.TenantReceivable, ct);
            var events = sp.GetRequiredService<IAccountingEvents>();
            switch (guard)
            {
                case "prepayment":
                    await events.PostAsync(new RentCharged(dims.TenantId!.Value, dims.PropertyId!.Value, dims.OwnerId!.Value,
                        null, new Money(200m), today, "Second charge"), ct);
                    await events.PostAsync(new PrepaymentApplied(dims.TenantId.Value, dims.PropertyId.Value, dims.OwnerId.Value,
                        new Money(200m), today, h.Binding.BankId, "Consume the excess"), ct);
                    break;
                case "disbursed":
                    await events.PostAsync(new OwnerDisbursed(dims.OwnerId!.Value, new Money(1000m), today,
                        h.Binding.BankId, "Owner draw"), ct);
                    break;
                default:
                    await sp.GetRequiredService<IAccountingPeriods>().CloseAsync(today.Year, today.Month, ct);
                    break;
            }
        }, ct);
        var before = await h.Balance(1, ct);

        var refused = await admin.PostAsync($"/api/payments/{op.Id}/return", null, ct);
        var body = await refused.Content.ReadAsStringAsync(ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe(code);

        // The refusal is durable, the payment is still in review, and it can still be closed.
        var view = await Staff(admin, op.Id, ct);
        (view.Status, view.Reason, view.CanCloseReview, view.ReturnEntryId).ShouldBe(("NeedsReview", code, true, null));
        // Why it was refused describes the owner's and the ledger's position; the tenant is not told.
        (await Read(tenant, op.Id, ct)).Reason.ShouldBe("return_requires_review");
        (await h.Balance(1, ct)).ShouldBe(before);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<JournalEntry>().CountAsync(x => x.ReversesEntryId != null, ct)).ShouldBe(0);
            (await db.Set<PaymentEffect>().CountAsync(x => x.Kind == "Return", ct)).ShouldBe(0);
        }, ct);
    }

    [Theory]
    [InlineData("Return", 50, "return_partial_unsupported")]
    [InlineData("Refund", 600, "return_kind_unsupported")]
    [InlineData("Dispute", 600, "return_kind_unsupported")]
    public async Task Partial_returns_refunds_and_disputes_cannot_be_posted_and_close_with_a_note(string kind, decimal gross, string code)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var tenant = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Submit(tenant, 600m, ct);
        await h.Emit(op, "BankCredit", ct);
        await h.Deliver(h.Observation(op, kind) with { Gross = gross, Net = gross }, ct);
        await h.Tick(ct);

        var refused = await admin.PostAsync($"/api/payments/{op.Id}/return", null, ct);
        var body = await refused.Content.ReadAsStringAsync(ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe(code);

        var close = $"/api/payments/{op.Id}/close-review";
        (await admin.PostAsJsonAsync(close, new ClosePaymentReviewBody("   "), ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tenant.PostAsJsonAsync(close, new ClosePaymentReviewBody("mine"), ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Staff(admin, op.Id, ct)).Status.ShouldBe("NeedsReview");

        var response = await admin.PostAsJsonAsync(close, new ClosePaymentReviewBody(" Corrected by journal adjustment. "), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var closed = await Staff(admin, op.Id, ct);
        (closed.Status, closed.Reason, closed.ReviewNote, closed.CanCloseReview, closed.CanPostReturn)
            .ShouldBe(("ReviewClosed", code, "Corrected by journal adjustment.", false, false));
        closed.ReviewClosedAt.ShouldNotBeNull();
        closed.EvidenceReference.ShouldNotBeNull();
        (await Read(tenant, op.Id, ct)).ReviewNote.ShouldBeNull();
        (await h.Balance(1, ct)).ShouldBe(400m); // closing posts nothing: the receipt still stands
        // A closed review is not a posted return.
        (await admin.PostAsync($"/api/payments/{op.Id}/return", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<JournalEntry>().CountAsync(x => x.ReversesEntryId != null, ct)).ShouldBe(0);
            var adminId = (await db.Users.SingleAsync(x => x.Email == h.AdminEmail, ct)).Id;
            var audit = await db.AuditEvents.Where(x => x.EntityType == "payment_operations" && x.EntityId == op.Id)
                .OrderByDescending(x => x.OccurredAt).FirstAsync(ct);
            audit.ActorUserId.ShouldBe(adminId);
            audit.After!.ShouldContain("ReviewClosed");
        }, ct);

        // Evidence arriving after the closure reopens the review instead of hiding behind it.
        await h.Deliver(h.Observation(op, "Dispute") with { EventId = "late-dispute" }, ct);
        await h.Tick(ct);
        var reopened = await Staff(admin, op.Id, ct);
        (reopened.Status, reopened.CanCloseReview).ShouldBe(("NeedsReview", true));
    }

    [Fact]
    public async Task Review_actions_are_bound_to_the_organization_and_absent_without_the_simulation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var a = await Setup(ct);
        await using var b = await Setup(ct);
        using var tenant = await a.Login(1, ct);
        using var otherAdmin = await b.LoginAdmin(ct);
        var op = await Submit(tenant, 600m, ct);
        await a.Emit(op, "BankCredit", ct);
        await a.Emit(op, "Return", ct);

        (await otherAdmin.PostAsync($"/api/payments/{op.Id}/return", null, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.PostAsJsonAsync($"/api/payments/{op.Id}/close-review", new ClosePaymentReviewBody("not mine"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var admin = await a.LoginAdmin(ct);
        (await Staff(admin, op.Id, ct)).Status.ShouldBe("NeedsReview");

        string?[] Routes(IServiceProvider services) => services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(x => x.RoutePattern.RawText).ToArray();
        Routes(a.App.Services).ShouldContain("/api/payments/{id:guid}/return");
        Routes(a.App.Services).ShouldContain("/api/payments/{id:guid}/close-review");
        Routes(fixture.Api.Services).ShouldNotContain(x => x != null && x.StartsWith("/api/payments/{id:guid}/"));
    }

    [Fact]
    public async Task Staff_see_unmatched_observations_narrowly_and_only_until_a_payment_answers_for_them()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var a = await Setup(ct);
        await using var b = await Setup(ct);
        using var tenant = await a.Login(1, ct);
        using var admin = await a.LoginAdmin(ct);
        using var otherAdmin = await b.LoginAdmin(ct);
        // Not dispatched yet, so the payment carries no provider reference and answers for nothing.
        var op = await Submit(tenant, 125.50m, ct);
        var early = a.Observation(op, "Succeeded");
        var orphan = a.Observation(op, "BankCredit") with
        {
            EventId = "orphan-event",
            ProviderId = "sim_orphan",
            Gross = 77.25m,
            Net = 77.25m,
        };
        await a.Deliver(early, ct);
        await a.Deliver(orphan, ct);

        // Inside the ten-minute window a notification may simply be ahead of its payment.
        (await Unmatched(admin, ct)).ShouldBeEmpty();
        a.Clock.Advance(TimeSpan.FromMinutes(11));

        var body = await admin.GetStringAsync("/api/payments/unmatched", ct);
        var items = await Unmatched(admin, ct);
        items.Select(x => (x.Kind, x.Amount, x.Currency, x.ProviderReference)).ShouldBe(
            [("BankCredit", 77.25m, "USD", "sim_orphan"), ("Succeeded", 125.50m, "USD", early.ProviderId)], ignoreOrder: true);
        items.ShouldAllBe(x => x.AgeMinutes >= 10 && x.AgeMinutes <= 12 && x.ReceivedAt != default);
        (await admin.GetFromJsonAsync<PaymentsResponse>("/api/payments", ct))!.UnmatchedObservations.ShouldBe(items.Count);

        // The view is the whole disclosure: these seven fields, and nothing that identifies the bank,
        // the account, the bank evidence or the payout.
        JsonDocument.Parse(body).RootElement.GetProperty("items")[0].EnumerateObject().Select(x => x.Name).ShouldBe(
            ["id", "receivedAt", "kind", "amount", "currency", "providerReference", "ageMinutes"], ignoreOrder: true);
        foreach (var secret in new[] { orphan.EvidenceId, orphan.PayoutId, a.Binding.Account, a.Binding.BankId.ToString(),
            a.Binding.Generation.ToString(), orphan.EventId })
        { body.ShouldNotContain(secret, Case.Insensitive); }

        // Once the payment is dispatched it carries the reference, and its notification is no longer unmatched.
        await a.Tick(ct);
        (await Unmatched(admin, ct)).Select(x => x.ProviderReference).ShouldBe(["sim_orphan"]);
        (await admin.GetFromJsonAsync<PaymentsResponse>("/api/payments", ct))!.UnmatchedObservations.ShouldBe(1);

        b.Clock.Advance(TimeSpan.FromMinutes(11));
        (await Unmatched(otherAdmin, ct)).ShouldBeEmpty();
        (await tenant.GetAsync("/api/payments/unmatched", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        a.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText).ShouldContain("/api/payments/unmatched");
        fixture.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText).ShouldNotContain("/api/payments/unmatched");
    }

    private static readonly object FeeRules = new
    {
        cardFeeRateBps = 290,
        cardFeeFixed = 0.30m,
        achFeeRateBps = 80,
        achFeeFixed = 0m,
        achFeeCap = 5m,
    };

    private static async Task<PaymentQuoteView> Quote(HttpClient tenant, decimal amount, string method, CancellationToken ct) =>
        (await tenant.GetFromJsonAsync<PaymentQuoteView>(
            $"{Path}/quote?amount={amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}&method={method}", ct))!;

    [Fact]
    public async Task A_quoted_fee_rides_on_top_of_the_ledger_amount_and_only_the_ledger_amount_posts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var tenant = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        // With no rule set, a payment is charged exactly what goes to the ledger.
        (await Quote(tenant, 500m, "card", ct)).ShouldBe(new PaymentQuoteView("card", 500m, 0m, 500m));
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();

        (await Quote(tenant, 500m, "ach", ct)).ShouldBe(new PaymentQuoteView("ach", 500m, 4.03m, 504.03m));
        var quote = await Quote(tenant, 500m, "card", ct);
        quote.ShouldBe(new PaymentQuoteView("card", 500m, 15.24m, 515.24m));

        var response = await tenant.PostAsJsonAsync(Path, new SubmitPaymentBody(UuidV7.NewId(), 500m, "USD", "card", quote.Fee), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(ct));
        var op = (await response.Content.ReadFromJsonAsync<PaymentView>(ct))!;
        (op.Amount, op.QuotedFee, op.ChargedAmount, op.Method, op.PaidAt).ShouldBe((500m, 15.24m, 515.24m, "card", null));

        var succeeded = h.Observation(op, "Succeeded");
        await h.Deliver(succeeded, ct);
        await h.Tick(ct);
        (await Read(tenant, op.Id, ct)).PaidAt!.Value.ShouldBe(succeeded.ObservedAt, TimeSpan.FromMilliseconds(1));
        (await h.Balance(1, ct)).ShouldBe(1000m);

        // The processor kept exactly the quoted fee, so the bank received the 500.00.
        await h.Emit(op, "BankCredit", ct);
        (await Read(tenant, op.Id, ct)).Status.ShouldBe("Settled");
        (await h.Balance(1, ct)).ShouldBe(500m);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var receipt = await db.Set<JournalEntry>().SingleAsync(x => x.SourceRef == $"sim-payment:{op.Id:N}:receipt", ct);
            receipt.EventSubtype.ShouldBe("Card");
            var lines = await db.Set<JournalLine>().Where(x => x.EntryId == receipt.Id).ToListAsync(ct);
            // The fee is nowhere in the journal: the bank line is the ledger amount, and no line is the fee or the charge.
            lines.Single(x => x.AccountClass == AccountClass.TrustBank).Debit!.Value.Amount.ShouldBe(500m);
            lines.ShouldAllBe(x => (x.Debit ?? x.Credit)!.Value.Amount == 500m);
            (await new InvariantChecks(db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    [Theory]
    [InlineData(22.74, 492.50)] // the processor kept more than was quoted
    [InlineData(14.84, 500.40)] // and less
    [InlineData(0, 500)]        // fee-free evidence for a fee-bearing payment
    public async Task Bank_evidence_whose_fee_is_not_the_quoted_fee_posts_nothing(double fee, double net)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var tenant = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        var response = await tenant.PostAsJsonAsync(Path, new SubmitPaymentBody(UuidV7.NewId(), 500m, "USD", "card", 15.24m), ct);
        var op = (await response.Content.ReadFromJsonAsync<PaymentView>(ct))!;

        await h.Deliver(h.Observation(op, "BankCredit") with { Gross = (decimal)net + (decimal)fee, Fee = (decimal)fee, Net = (decimal)net }, ct);
        await h.Tick(ct);

        var view = await Read(tenant, op.Id, ct);
        (view.Status, view.Reason, view.ReceiptRecorded).ShouldBe(("NeedsReview", "unsupported_settlement", false));
        (await h.Balance(1, ct)).ShouldBe(1000m);
    }

    [Fact]
    public async Task A_fee_that_changed_since_the_quote_is_refused_until_the_tenant_confirms_the_new_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var tenant = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        var shown = await Quote(tenant, 500m, "card", ct);

        // The organization changes its card rule between the quote and the confirmation.
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", new
        { cardFeeRateBps = 300, cardFeeFixed = 0.30m, achFeeRateBps = 80, achFeeFixed = 0m, achFeeCap = 5m }, ct))
            .EnsureSuccessStatusCode();
        var stale = new SubmitPaymentBody(UuidV7.NewId(), 500m, "USD", "card", shown.Fee);
        await ShouldBeProblem(await tenant.PostAsJsonAsync(Path, stale, ct), "fee_quote_changed", ct);
        // A request that names no fee is refused the same way; it cannot be charged one it never saw.
        await ShouldBeProblem(await tenant.PostAsJsonAsync(Path, new SubmitPaymentBody(UuidV7.NewId(), 500m, "USD", "card"), ct),
            "fee_quote_changed", ct);
        (await tenant.GetFromJsonAsync<PaymentsResponse>(Path, ct))!.Items.ShouldBeEmpty();

        var current = await Quote(tenant, 500m, "card", ct);
        current.Fee.ShouldNotBe(shown.Fee);
        var confirmed = new SubmitPaymentBody(UuidV7.NewId(), 500m, "USD", "card", current.Fee);
        var accepted = await tenant.PostAsJsonAsync(Path, confirmed, ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(ct));
        var op = (await accepted.Content.ReadFromJsonAsync<PaymentView>(ct))!;

        // Once accepted, the request stands at the fee the tenant confirmed, whatever the rule does next:
        // a replay finds the same payment, and the payment keeps its fee.
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        var replay = await tenant.PostAsJsonAsync(Path, confirmed, ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var again = (await replay.Content.ReadFromJsonAsync<PaymentView>(ct))!;
        (again.Id, again.QuotedFee).ShouldBe((op.Id, current.Fee));
    }

    [Fact]
    public async Task A_quote_is_the_tenants_own_and_refuses_an_amount_or_method_it_cannot_price()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var tenant = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);

        (await tenant.GetAsync($"{Path}/quote?amount=0&method=card", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tenant.GetAsync($"{Path}/quote?amount=10000.01&method=card", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tenant.GetAsync($"{Path}/quote?amount=10.005&method=card", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tenant.GetAsync($"{Path}/quote?amount=10&method=wire", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"{Path}/quote?amount=10&method=card", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var wire = await tenant.PostAsJsonAsync(Path, new SubmitPaymentBody(UuidV7.NewId(), 10m, "USD", "wire"), ct);
        wire.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        fixture.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText).ShouldNotContain(Path + "/quote");
    }

    private static async Task ShouldBeProblem(HttpResponseMessage response, string code, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe(code);
    }

    private static async Task<IReadOnlyList<UnmatchedObservationView>> Unmatched(HttpClient staff, CancellationToken ct) =>
        (await staff.GetFromJsonAsync<UnmatchedObservationsResponse>("/api/payments/unmatched", ct))!.Items;

    private static async Task<PaymentView> Staff(HttpClient admin, Guid id, CancellationToken ct) =>
        (await admin.GetFromJsonAsync<PaymentsResponse>("/api/payments", ct))!.Items.Single(x => x.Id == id);

    private static async Task<PaymentView> Submit(HttpClient client, decimal amount, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Path, new SubmitPaymentBody(UuidV7.NewId(), amount, "USD"), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<PaymentView>(ct))!;
    }
    private static async Task<PaymentView> Read(HttpClient client, Guid id, CancellationToken ct) =>
        (await client.GetFromJsonAsync<PaymentView>(Path + "/" + id, ct))!;

    private async Task<Harness> Setup(CancellationToken ct, AfterPostingFailure? fault = null)
    {
        var binding = new FixtureBinding(UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId(), "sim_" + UuidV7.NewId().ToString("N"));
        var suffix = UuidV7.NewId().ToString("N");
        await PaymentFixtureBootstrap.SeedAsync(fixture.Api.Services, binding, suffix, ct);
        var clock = new PaymentClock();
        var factory = new ApiFactory(fixture.AppConnectionString, new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Payments:Mode"] = "Simulation",
            ["Payments:SigningKey"] = new string('x', 64),
            ["Payments:Fixtures:0:OrgId"] = binding.OrgId.ToString(),
            ["Payments:Fixtures:0:Generation"] = binding.Generation.ToString(),
            ["Payments:Fixtures:0:BankId"] = binding.BankId.ToString(),
            ["Payments:Fixtures:0:Account"] = binding.Account,
        });
        var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService)
                && x.ImplementationType?.Name == "PaymentWorker").ToArray()) { services.Remove(service); }
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            if (fault is not null)
            {
                services.RemoveAll<IPaymentLedger>();
                services.AddScoped<IPaymentLedger>(sp => new FailingLedger(sp.GetRequiredService<ISender>(), fault));
            }
        }));
        return new Harness(factory, app, binding, suffix, clock);
    }

    private sealed class PaymentClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(2);
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
    private sealed class AfterPostingFailure { public bool Fail { get; set; } = true; }
    private sealed class FailingLedger(ISender sender, AfterPostingFailure fault) : IPaymentLedger
    {
        public async Task<Guid> RecordSettledReceiptAsync(Guid tenantId, Guid bankId, decimal amount, DateOnly date, string method, string sourceRef, CancellationToken ct)
        {
            var result = await sender.Send(new Modules.Accounting.Features.LedgerPosting.RecordPayment(tenantId, amount, date, method, bankId, "Simulation", sourceRef), ct);
            if (fault.Fail) { fault.Fail = false; throw new IOException("Injected crash after ledger SaveChanges"); }
            return result.EntryId;
        }

        public Task<PaymentReturnOutcome> ReturnSettledReceiptAsync(Guid receiptJournalId, DateOnly date, string sourceRef, string note, CancellationToken ct) =>
            throw new NotSupportedException("The failing ledger exercises receipts only.");
    }
    private sealed class Harness(ApiFactory factory, WebApplicationFactory<Program> app, FixtureBinding binding, string suffix, PaymentClock clock) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> App => app;
        public FixtureBinding Binding => binding;
        public PaymentClock Clock => clock;
        public string Email(int tenant) => $"tenant-{suffix}{tenant}@payments.test";
        public async Task<HttpClient> Login(int tenant, CancellationToken ct)
        {
            var client = app.CreateClient(); await client.PrimeCsrfAsync(ct);
            (await client.PostAsJsonAsync("/api/auth/login", new { email = Email(tenant), password = PaymentFixtureBootstrap.Password }, ct)).EnsureSuccessStatusCode();
            await client.PrimeCsrfAsync(ct); return client;
        }
        public string AdminEmail => $"admin-{suffix}@payments.test";
        public async Task<HttpClient> LoginAdmin(CancellationToken ct)
        {
            var client = app.CreateClient(); await client.PrimeCsrfAsync(ct);
            (await client.PostAsJsonAsync("/api/auth/login", new { email = AdminEmail, password = PaymentFixtureBootstrap.Password }, ct)).EnsureSuccessStatusCode();
            await client.PrimeCsrfAsync(ct); return client;
        }
        public async Task InOrg(Func<IServiceProvider, Task> work, CancellationToken ct)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(binding.OrgId, "test:payments", () => work(scope.ServiceProvider), ct);
        }
        public async Task<decimal> Balance(int tenant, CancellationToken ct)
        {
            decimal balance = 0;
            await InOrg(async sp =>
            {
                var db = sp.GetRequiredService<AppDbContext>();
                var user = await db.Users.SingleAsync(x => x.OrgId == binding.OrgId && x.Email == Email(tenant), ct);
                var link = await db.Set<ResidentAccess>().SingleAsync(x => x.UserId == user.Id, ct);
                balance = (await sp.GetRequiredService<ISender>().Query(new GetTenantLedger(link.TenantId), ct)).Balance;
            }, ct);
            return balance;
        }
        // As the fixture CLI emits it: bank credit is the clean item (the charge, the quoted fee, the
        // ledger amount net); every other kind keeps the fee-free shape.
        public ProcessorObservation Observation(PaymentView op, string kind) => new($"{op.Id:N}:{kind}",
            $"sim_{binding.Generation:N}_{op.Id:N}", binding.Account, "Simulation", binding.Generation, kind,
            kind == "BankCredit" ? op.ChargedAmount : op.Amount, kind == "BankCredit" ? op.QuotedFee : 0m, op.Amount,
            "USD", binding.BankId, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
            "evidence-" + op.Id.ToString("N"), "payout-" + op.Id.ToString("N"), true, clock.GetUtcNow().UtcDateTime);
        public async Task Deliver(ProcessorObservation value, CancellationToken ct)
        {
            var processor = app.Services.GetRequiredService<SimulatedProcessor>();
            var body = JsonSerializer.SerializeToUtf8Bytes(value);
            using var client = app.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/callbacks/payments/simulation") { Content = new ByteArrayContent(body) };
            request.Headers.Add("X-Simulation-Signature", processor.Sign(body));
            (await client.SendAsync(request, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
        public async Task Emit(PaymentView op, string kind, CancellationToken ct) { await Deliver(Observation(op, kind), ct); await Tick(ct); }
        public async Task Tick(CancellationToken ct) { clock.Advance(); await app.Services.GetRequiredService<PaymentRunner>().RunOnceAsync(ct); }
        public async ValueTask DisposeAsync() { await app.DisposeAsync(); await factory.DisposeAsync(); }
    }
}
