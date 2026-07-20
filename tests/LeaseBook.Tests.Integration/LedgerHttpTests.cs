using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Audit;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-03: the M3 write/read surface end-to-end <b>over HTTP</b> against the seeded host — the first HTTP
/// producer of the AccountingExceptionHandler (P53/§C.5). Logs in as staff, drives record-payment →
/// ledger → trust-equation → void → audit → CSV, and proves the §C.5 status mapping (a valid post is
/// 200; an over-receivable deposit application is 409 <c>insufficient_receivable</c>). Setup runs in its
/// own seeded org so the demo org stays byte-stable (M3-E9).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LedgerHttpTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Feb3 = new(2026, 2, 3);

    [Fact]
    public async Task Record_payment_then_void_round_trips_over_http_with_audit_and_csv()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // Record a payment (no prior charge → auto-split to a prepayment liability, balance goes negative).
        var payment = await PostOkAsync<PostResult>(client,
            $"/api/accounting/tenants/{setup.TenantId}/payments",
            new { amount = 1450m, date = Feb1, method = "ach", bankAccountId = setup.TrustBankId, sourceRef = Key() }, ct);

        var afterPay = await GetAsync<TenantLedgerResponse>(client, $"/api/accounting/tenants/{setup.TenantId}/ledger", ct);
        afterPay.Rows.ShouldContain(r => r.EntryId == payment.EntryId && r.Payment == 1450m);
        afterPay.Balance.ShouldBe(-1450m);

        var equation = await GetAsync<TrustEquationResponse>(client, "/api/accounting/trust-equation", ct);
        equation.Rows.ShouldAllBe(r => r.Variance == 0m);

        // Void it → linked reversal, balance back to baseline.
        var reversal = await PostOkAsync<PostResult>(client,
            $"/api/accounting/entries/{payment.EntryId}/void",
            new { reason = "entered in error", sourceRef = Key() }, ct);

        var afterVoid = await GetAsync<TenantLedgerResponse>(client, $"/api/accounting/tenants/{setup.TenantId}/ledger", ct);
        afterVoid.Rows.ShouldContain(r => r.EntryId == payment.EntryId && r.IsVoided);
        afterVoid.Rows.ShouldContain(r => r.EntryId == reversal.EntryId && r.ReversesEntryId == payment.EntryId);
        afterVoid.Balance.ShouldBe(0m);

        // Audit trail shows both rows with the acting user.
        var audit = await GetAsync<EntryAuditResponse>(client, $"/api/accounting/entries/{payment.EntryId}/audit", ct);
        audit.Rows.Count.ShouldBe(2);
        audit.Rows.ShouldAllBe(r => r.ActorName == "Renée Calloway");

        // CSV downloads with the expected header row.
        var csv = await client.GetAsync($"/api/accounting/tenants/{setup.TenantId}/ledger.csv", ct);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        (await csv.Content.ReadAsStringAsync(ct)).ShouldStartWith("Date,Category,Description,Charge,Payment,Balance,Status");
    }

    [Fact]
    public async Task Deposit_collect_apply_and_over_apply_map_to_the_right_http_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        await PostOkAsync<PostResult>(client, $"/api/accounting/tenants/{setup.TenantId}/charges",
            new { amount = 1000m, date = Feb1, kind = "rent", sourceRef = Key() }, ct);
        await PostOkAsync<PostResult>(client, $"/api/accounting/tenants/{setup.TenantId}/deposits",
            new { amount = 1500m, date = Feb1, depositBankId = setup.DepositBankId, sourceRef = Key() }, ct);

        // Over the open receivable → 409 insufficient_receivable (the handler's first HTTP producer).
        var over = await client.PostAsJsonAsync($"/api/accounting/tenants/{setup.TenantId}/deposit-applications",
            new { amount = 1200m, date = Feb3, depositBankId = setup.DepositBankId, operatingBankId = setup.TrustBankId, target = "against-charges", reason = "move-out", sourceRef = Key() }, ct);
        over.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await over.Content.ReadFromJsonAsync<ProblemWithCode>(ct);
        problem!.Code.ShouldBe("insufficient_receivable");
        problem.CorrelationId.ShouldNotBeNullOrWhiteSpace(
            "every error response must carry a correlationId the operator can quote (ADR-025)");

        // Exactly the receivable → 200, ledger nets to 0, deposit register shows the remainder held.
        await PostOkAsync<PostResult>(client, $"/api/accounting/tenants/{setup.TenantId}/deposit-applications",
            new { amount = 1000m, date = Feb3, depositBankId = setup.DepositBankId, operatingBankId = setup.TrustBankId, target = "against-charges", reason = "move-out", sourceRef = Key() }, ct);

        var ledger = await GetAsync<TenantLedgerResponse>(client, $"/api/accounting/tenants/{setup.TenantId}/ledger", ct);
        ledger.Balance.ShouldBe(0m);
        var register = await GetAsync<DepositRegisterResponse>(client, "/api/accounting/deposits", ct);
        register.Rows.ShouldContain(r => r.TenantId == setup.TenantId && r.Kind == "deposit" && r.Held == 500m);
    }

    [Fact]
    public async Task A_validation_failure_maps_to_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // Negative amount fails the command validator → 400 (M0 ProblemDetails handler).
        var bad = await client.PostAsJsonAsync($"/api/accounting/tenants/{setup.TenantId}/charges",
            new { amount = -5m, date = Feb1, kind = "rent", sourceRef = Key() }, ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = (await bad.Content.ReadFromJsonAsync<ProblemWithCode>(ct))!;
        problem.Code.ShouldBe("validation_failed");
        problem.CorrelationId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Voiding_a_nonexistent_entry_maps_to_404_not_500()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // A random entry id — also the cross-org shape: RLS makes another org's entry invisible, so it
        // resolves to "not found" through the same path, with no existence oracle. Must be a 404
        // domain error (entry_not_found), not the generic 500 a bare exception would produce (F5).
        var response = await client.PostAsJsonAsync(
            $"/api/accounting/entries/{UuidV7.NewId()}/void",
            new { reason = "entered in error", sourceRef = Key() }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(ct));
        var problem = (await response.Content.ReadFromJsonAsync<ProblemWithCode>(ct))!;
        problem.Code.ShouldBe("entry_not_found");
        problem.CorrelationId.ShouldNotBeNullOrWhiteSpace(
            "every error response must carry a correlationId the operator can quote (ADR-025)");
    }

    private sealed record ProblemWithCode(string Code, string? CorrelationId);

    private sealed record Setup(Guid OrgId, string Email, Guid TenantId, Guid TrustBankId, Guid DepositBankId);

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Ledger HTTP Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"renee-{orgId:N}@example.com";
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser
            {
                Id = UuidV7.NewId(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                OrgId = orgId,
                DisplayName = "Renée Calloway",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        Guid tenantId = default, trustBankId = default, depositBankId = default;
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsync(orgId, async () =>
            {
                var ownerId = await sender.Send(new CreateOwner("Owner", null, null, null, 800, 0m), ct);
                var propertyId = await sender.Send(new CreateProperty(ownerId, "412 Oakmont Ave", "Asheville", "NC", "28801", null), ct);
                var unitId = await sender.Send(new CreateUnit(propertyId, "#2B", 1450m, "occupied"), ct);
                tenantId = await sender.Send(new CreateTenant("Jasmine Carter", null, null, "current"), ct);
                await sender.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 6, 1), new DateOnly(2026, 5, 31), 1450m, 1450m, "active"), ct);
                trustBankId = (await sender.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct)).Id;
                depositBankId = (await sender.Send(new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct)).Id;
            }, ct);
        }

        return new Setup(orgId, email, tenantId, trustBankId, depositBankId);
    }

    private async Task<HttpClient> LoggedInClientAsync(Setup setup, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(setup.Email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct); // XSRF token rotates on sign-in
        return client;
    }

    private static async Task<T> PostOkAsync<T>(HttpClient client, string url, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(url, body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
