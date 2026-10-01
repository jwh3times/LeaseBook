using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using UglyToad.PdfPig;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #473 over the real HTTP host: a staff user issues a refund check against a held deposit, prints it,
/// and voids it; numbering is unique per bank, a retried issue is idempotent, a cleared check cannot be
/// voided, the generic ledger void refuses a check, and the surface is closed to other roles and orgs.
/// Each test seeds its own org so golden figures stay byte-stable.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RefundCheckHttpTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Mar2 = new(2026, 3, 2);

    [Fact]
    public async Task A_deposit_refund_check_is_issued_printed_and_voided()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup.StaffEmail, ct);

        var options = await GetAsync<RefundCheckOptions>(client, $"/api/refund-checks/options?tenantId={setup.TenantId}", ct);
        var fund = options.Funds.ShouldHaveSingleItem();
        fund.Source.ShouldBe("deposit");
        fund.Held.ShouldBe(1450m);
        fund.BankAccountId.ShouldBe(setup.DepositBankId);
        fund.NextCheckNumber.ShouldBeNull();

        var issued = await PostOkAsync<RefundCheckView>(client, "/api/refund-checks", Body(setup, 1043), ct);
        issued.Status.ShouldBe("outstanding");
        issued.BankAccountId.ShouldBe(setup.DepositBankId);
        issued.CheckNumber.ShouldBe(1043);
        issued.PayeeName.ShouldBe("Jasmine Carter");
        issued.PrintCount.ShouldBe(0);

        var list = await GetAsync<IReadOnlyList<RefundCheckView>>(client, $"/api/refund-checks?bankAccountId={setup.DepositBankId}", ct);
        list.ShouldHaveSingleItem().Id.ShouldBe(issued.Id);

        var pdf = await client.PostAsync($"/api/refund-checks/{issued.Id}/pdf", null, ct);
        pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        pdf.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var text = PdfText(await pdf.Content.ReadAsByteArrayAsync(ct));
        text.ShouldContain("Jasmine Carter");
        text.ShouldContain("1,450.00");
        text.ShouldContain("One thousand four hundred fifty and 00/100");
        text.ShouldContain("1043");
        text.ShouldContain("412 Oakmont Ave");

        var printed = await GetAsync<IReadOnlyList<RefundCheckView>>(client, $"/api/refund-checks?tenantId={setup.TenantId}", ct);
        printed.ShouldHaveSingleItem().PrintCount.ShouldBe(1);

        var voided = await PostOkAsync<RefundCheckView>(client, $"/api/refund-checks/{issued.Id}/void", new { reason = "Misprinted" }, ct);
        voided.Status.ShouldBe("voided");

        var after = await GetAsync<RefundCheckOptions>(client, $"/api/refund-checks/options?tenantId={setup.TenantId}", ct);
        after.Funds.ShouldHaveSingleItem().Held.ShouldBe(1450m);
        after.Funds[0].NextCheckNumber.ShouldBe(1044);

        var reprint = await client.PostAsync($"/api/refund-checks/{issued.Id}/pdf", null, ct);
        await ShouldBeProblemAsync(reprint, HttpStatusCode.Conflict, "refund_check_voided", ct);
    }

    [Fact]
    public async Task A_check_number_is_unique_per_bank_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup.StaffEmail, ct);

        await PostOkAsync<RefundCheckView>(client, "/api/refund-checks", Body(setup, 1043, amount: 450m), ct);
        var duplicate = await client.PostAsJsonAsync("/api/refund-checks", Body(setup, 1043, amount: 450m), ct);
        await ShouldBeProblemAsync(duplicate, HttpStatusCode.Conflict, "check_number_taken", ct);

        // The rejected issue posted nothing: 1,000 of the 1,450 is still held.
        var options = await GetAsync<RefundCheckOptions>(client, $"/api/refund-checks/options?tenantId={setup.TenantId}", ct);
        options.Funds.ShouldHaveSingleItem().Held.ShouldBe(1000m);
    }

    [Fact]
    public async Task Retrying_an_issue_with_the_same_key_returns_the_same_check_and_posts_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup.StaffEmail, ct);
        var body = Body(setup, 1043, amount: 450m);

        var first = await PostOkAsync<RefundCheckView>(client, "/api/refund-checks", body, ct);
        var second = await PostOkAsync<RefundCheckView>(client, "/api/refund-checks", body, ct);

        second.Id.ShouldBe(first.Id);
        var options = await GetAsync<RefundCheckOptions>(client, $"/api/refund-checks/options?tenantId={setup.TenantId}", ct);
        options.Funds.ShouldHaveSingleItem().Held.ShouldBe(1000m);

        var conflicting = await client.PostAsJsonAsync("/api/refund-checks", Body(setup, 1044, amount: 450m, key: body.Key), ct);
        await ShouldBeProblemAsync(conflicting, HttpStatusCode.Conflict, "refund_check_conflict", ct);
    }

    [Fact]
    public async Task A_cleared_check_cannot_be_voided_and_the_ledger_void_refuses_any_check()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup.StaffEmail, ct);
        var issued = await PostOkAsync<RefundCheckView>(client, "/api/refund-checks", Body(setup, 1043), ct);

        var genericVoid = await client.PostAsJsonAsync($"/api/accounting/entries/{issued.EntryId}/void",
            new { reason = "entered in error", sourceRef = UuidV7.NewId().ToString() }, ct);
        await ShouldBeProblemAsync(genericVoid, HttpStatusCode.Conflict, "refund_check_void_required", ct);

        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.DepositBankId}/register", ct);
        var line = register.Rows.Single(r => r.Withdrawal == 1450m);
        line.Description.ShouldBe("Refund check #1043 — security deposit");
        await PostOkAsync<JsonElement>(client, "/api/accounting/banks/clearances", new { journalLineIds = new[] { line.JournalLineId } }, ct);

        var cleared = await GetAsync<IReadOnlyList<RefundCheckView>>(client, $"/api/refund-checks?tenantId={setup.TenantId}", ct);
        cleared.ShouldHaveSingleItem().Status.ShouldBe("cleared");

        var voidCleared = await client.PostAsJsonAsync($"/api/refund-checks/{issued.Id}/void", new { reason = "Lost" }, ct);
        await ShouldBeProblemAsync(voidCleared, HttpStatusCode.Conflict, "refund_check_cleared", ct);
    }

    [Fact]
    public async Task A_payee_address_is_required()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup.StaffEmail, ct);

        var bad = await client.PostAsJsonAsync("/api/refund-checks", Body(setup, 1043) with { AddressLine1 = "", PostalCode = "2880" }, ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Print_offsets_are_saved_per_bank_and_the_alignment_page_renders()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup.StaffEmail, ct);
        var url = $"/api/refund-checks/print-settings/{setup.DepositBankId}";

        (await GetAsync<CheckPrintSettingsView>(client, url, ct)).ShouldBe(new CheckPrintSettingsView(setup.DepositBankId, 0m, 0m));
        var saved = await client.PutAsJsonAsync(url, new { offsetXPoints = 4.5m, offsetYPoints = -3m }, ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(ct));
        (await GetAsync<CheckPrintSettingsView>(client, url, ct)).ShouldBe(new CheckPrintSettingsView(setup.DepositBankId, 4.5m, -3m));

        var tooFar = await client.PutAsJsonAsync(url, new { offsetXPoints = 400m, offsetYPoints = 0m }, ct);
        tooFar.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var alignment = await client.PostAsync($"{url}/alignment", null, ct);
        alignment.StatusCode.ShouldBe(HttpStatusCode.OK);
        PdfText(await alignment.Content.ReadAsByteArrayAsync(ct)).ShouldContain("ALIGNMENT TEST");
    }

    [Fact]
    public async Task The_surface_is_closed_to_users_without_a_staff_role_and_to_other_orgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var staff = await LoggedInClientAsync(setup.StaffEmail, ct);
        var issued = await PostOkAsync<RefundCheckView>(staff, "/api/refund-checks", Body(setup, 1043), ct);

        var anonymous = fixture.Api.CreateClient();
        (await anonymous.GetAsync($"/api/refund-checks?tenantId={setup.TenantId}", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var roleless = await LoggedInClientAsync(setup.RolelessEmail, ct);
        (await roleless.GetAsync($"/api/refund-checks?tenantId={setup.TenantId}", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await roleless.PostAsJsonAsync("/api/refund-checks", Body(setup, 1044), ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var other = await SetupAsync(ct);
        var otherStaff = await LoggedInClientAsync(other.StaffEmail, ct);
        (await otherStaff.GetFromJsonAsync<IReadOnlyList<RefundCheckView>>($"/api/refund-checks?tenantId={setup.TenantId}", ct))
            .ShouldBeEmpty();
        (await otherStaff.PostAsync($"/api/refund-checks/{issued.Id}/pdf", null, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherStaff.PostAsJsonAsync($"/api/refund-checks/{issued.Id}/void", new { reason = "x" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var foreignTenant = await otherStaff.PostAsJsonAsync("/api/refund-checks", Body(setup, 1044), ct);
        foreignTenant.StatusCode.ShouldBe(HttpStatusCode.Conflict); // no held funds visible in org B
    }

    private sealed record Setup(Guid OrgId, string StaffEmail, string RolelessEmail, Guid TenantId, Guid DepositBankId);

    public sealed record IssueBody(Guid Key, Guid TenantId, string Source, decimal Amount, DateOnly Date, int CheckNumber,
        string PayeeName, string AddressLine1, string? AddressLine2, string City, string State, string PostalCode,
        string? Memo, string? InternalNote);

    private static IssueBody Body(Setup setup, int checkNumber, decimal amount = 1450m, Guid? key = null) => new(
        key ?? UuidV7.NewId(), setup.TenantId, "deposit", amount, Mar2, checkNumber, "Jasmine Carter",
        "412 Oakmont Ave", "#2B", "Asheville", "NC", "28801", "Deposit return", null);

    private static string PdfText(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        return string.Join("\n", document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(status, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe(code);
    }

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Refund Check HTTP Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var staffEmail = await CreateUserAsync(orgId, "staff", Roles.PMStaff, ct);
        var rolelessEmail = await CreateUserAsync(orgId, "nobody", role: null, ct);

        Guid tenantId = default, depositBankId = default;
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var s = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
            {
                var ownerId = await s.Send(new CreateOwner("Owner", null, null, null, 800, 0m), ct);
                var propertyId = await s.Send(new CreateProperty(ownerId, "412 Oakmont Ave", "Asheville", "NC", "28801", null), ct);
                var unitId = await s.Send(new CreateUnit(propertyId, "#2B", 1450m, "available"), ct);
                tenantId = await s.Send(new CreateTenant("Jasmine Carter", null, null, "current"), ct);
                await s.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 6, 1), new DateOnly(2026, 5, 31), 1450m, 1450m, "active"), ct);
                await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
                depositBankId = (await s.Send(new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct)).Id;
                await s.Send(new CollectDeposit(tenantId, 1450m, Feb1, depositBankId, null, UuidV7.NewId().ToString()), ct);
            }, ct);
        }

        return new Setup(orgId, staffEmail, rolelessEmail, tenantId, depositBankId);
    }

    private async Task<string> CreateUserAsync(Guid orgId, string name, string? role, CancellationToken ct)
    {
        var email = $"{name}-{orgId:N}@example.com";
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            Id = UuidV7.NewId(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            OrgId = orgId,
            DisplayName = name,
        };
        (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
        if (role is not null)
        {
            (await userManager.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        }

        return email;
    }

    private async Task<HttpClient> LoggedInClientAsync(string email, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(ct));
        await client.PrimeCsrfAsync(ct);
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
