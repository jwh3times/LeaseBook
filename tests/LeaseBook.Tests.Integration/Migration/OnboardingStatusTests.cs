using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Onboarding;
using LeaseBook.Web.Onboarding.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration.Migration;

/// <summary>
/// WP-5 Task 5.2: <c>GET /api/onboarding/status</c> — derived wizard state.
///
/// Each test starts with a fresh org and walks one flag from false → true, asserting that
/// only the expected flag flips and the others remain false. The five flags are independent:
/// banksConfigured / entitiesImported / balancesImported / verified / signedOff.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OnboardingStatusTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Cutover = new(2026, 6, 30);
    private const string CutoverStr = "2026-06-30";

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 1: fresh org → all five flags false
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fresh_org_returns_all_flags_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, client) = await SetupAsync("AllFalse", withBanks: false, ct);

        var status = await GetStatusAsync(client, ct);

        status.BanksConfigured.ShouldBeFalse();
        status.EntitiesImported.ShouldBeFalse();
        status.BalancesImported.ShouldBeFalse();
        status.Verified.ShouldBeFalse();
        status.SignedOff.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 2: after creating a bank account → banksConfigured becomes true
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task After_creating_bank_account_banksConfigured_is_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, client) = await SetupAsync("BanksConfigured", withBanks: true, ct);

        var status = await GetStatusAsync(client, ct);

        status.BanksConfigured.ShouldBeTrue();
        status.EntitiesImported.ShouldBeFalse();
        status.BalancesImported.ShouldBeFalse();
        status.Verified.ShouldBeFalse();
        status.SignedOff.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 3: after a posted entity import batch → entitiesImported becomes true
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task After_entity_import_batch_entitiesImported_is_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, client) = await SetupAsync("EntitiesImported", withBanks: true, ct);

        // Import one owners CSV → batch is "posted" with EntityKind = "Owners".
        await PostImportAsync(client, "owners",
            new { csvContent = "Owner ID,Owner Name,Reserve\nO-1,Status Test Owner,0\n", filename = "owners.csv" }, ct);

        var status = await GetStatusAsync(client, ct);

        status.BanksConfigured.ShouldBeTrue();
        status.EntitiesImported.ShouldBeTrue("a posted Owners batch must set entitiesImported=true");
        status.BalancesImported.ShouldBeFalse();
        status.Verified.ShouldBeFalse();
        status.SignedOff.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 4: after a posted balance import batch → balancesImported becomes true
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task After_balance_import_batch_balancesImported_is_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, client) = await SetupAsync("BalancesImported", withBanks: true, ct);

        // A bank_balances import requires the bank to exist by name — we created one in setup.
        await PostBalanceAsync(client,
            "bank_balances",
            $"Account ID,Account Name,Book Balance\nB-T,{setup.TrustBankName},500.00\n",
            ct);

        var status = await GetStatusAsync(client, ct);

        status.BanksConfigured.ShouldBeTrue();
        status.EntitiesImported.ShouldBeFalse();
        status.BalancesImported.ShouldBeTrue("a posted BankBalances batch must set balancesImported=true");
        status.Verified.ShouldBeFalse();
        status.SignedOff.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 5: after a verification row → verified becomes true
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task After_verification_row_verified_is_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, client) = await SetupAsync("Verified", withBanks: true, ct);

        // Post a verification (no need for it to be tied; just needs to exist).
        // Use $0 figures so the verification can proceed without a prior balance import.
        var (trustId, _) = await ResolveBankIdsAsync(setup.OrgId, ct);
        await PostVerificationAsync(client,
            new
            {
                cutoverDate = CutoverStr,
                ownerEquityTotal = 0m,
                depositLiabilityTotal = 0m,
                bankBookBalances = new[]
                {
                    new { bankAccountId = trustId, expectedBook = 0m, accountCode = (string?)null },
                },
            }, ct);

        var status = await GetStatusAsync(client, ct);

        status.BanksConfigured.ShouldBeTrue();
        status.EntitiesImported.ShouldBeFalse();
        status.BalancesImported.ShouldBeFalse();
        status.Verified.ShouldBeTrue("a MigrationVerification row must set verified=true");
        status.SignedOff.ShouldBeFalse("verified but not signed off yet");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 6: after a signed-off verification row → signedOff becomes true
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task After_signoff_signedOff_is_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, client) = await SetupAsync("SignedOff", withBanks: true, ct);

        // Build a tied set so sign-off can succeed (IsTied must be true).
        await ImportOwnerTenantChainAsync(client, ct);
        await PostBalanceAsync(client, "bank_balances",
            $"Account ID,Account Name,Book Balance\nB-T,{setup.TrustBankName},500.00\nB-D,{setup.DepositBankName},500.00\n", ct);
        await PostBalanceAsync(client, "owner_balances",
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,SignedOff Owner LLC,500.00,500.00\n", ct);
        await PostBalanceAsync(client, "deposit_liabilities",
            "Tenant ID,Owner ID,Deposit Held\nT-1,O-1,500.00\n", ct);

        var (trustId, depositId) = await ResolveBankIdsAsync(setup.OrgId, ct);

        var verificationId = await PostVerificationAsync(client,
            new
            {
                cutoverDate = CutoverStr,
                ownerEquityTotal = 500m,
                depositLiabilityTotal = 500m,
                bankBookBalances = new[]
                {
                    new { bankAccountId = trustId, expectedBook = 500m, accountCode = (string?)null },
                    new { bankAccountId = depositId, expectedBook = 500m, accountCode = (string?)null },
                },
            }, ct);

        // Sign off (must succeed because the import is tied).
        var signoffResponse = await client.PostAsJsonAsync(
            $"/api/onboarding/verification/{verificationId}/signoff", new { }, ct);
        signoffResponse.StatusCode.ShouldBe(HttpStatusCode.OK,
            await signoffResponse.Content.ReadAsStringAsync(ct));

        var status = await GetStatusAsync(client, ct);

        status.BanksConfigured.ShouldBeTrue();
        status.EntitiesImported.ShouldBeTrue();
        status.BalancesImported.ShouldBeTrue();
        status.Verified.ShouldBeTrue();
        status.SignedOff.ShouldBeTrue("a signed-off MigrationVerification row must set signedOff=true");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record TestSetup(
        Guid OrgId,
        string TrustBankName,
        string DepositBankName);

    private async Task<(TestSetup Setup, HttpClient Client)> SetupAsync(
        string tag,
        bool withBanks,
        CancellationToken ct)
    {
        var orgId = UuidV7.NewId();

        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Onboarding Status Test {tag} {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"status-{orgId:N}@example.com";
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
                DisplayName = "Status Test User",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        var trustBankName = $"Trust {orgId:N}";
        var depositBankName = $"Deposit {orgId:N}";

        if (withBanks)
        {
            await using var scope = fixture.Api.Services.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsync(orgId, async () =>
            {
                await sender.Send(new CreateBankAccount(trustBankName, null, null, "trust"), ct);
                await sender.Send(new CreateBankAccount(depositBankName, null, null, "deposit"), ct);
            }, ct);
        }

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(ct));
        await client.PrimeCsrfAsync(ct);

        return (new TestSetup(orgId, trustBankName, depositBankName), client);
    }

    /// <summary>GETs /api/onboarding/status and deserialises it.</summary>
    private static async Task<OnboardingStatusResponse> GetStatusAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync("/api/onboarding/status", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<OnboardingStatusResponse>(ct))!;
    }

    /// <summary>POSTs an entity import CSV and asserts HTTP 200.</summary>
    private static async Task PostImportAsync(HttpClient client, string kind, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync($"/api/onboarding/import/{kind}", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>POSTs a balance import CSV and asserts HTTP 200.</summary>
    private static async Task PostBalanceAsync(HttpClient client, string kind, string csv, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/onboarding/import-balances/{kind}",
            new { csvContent = csv, cutoverDate = CutoverStr, filename = $"{kind}.csv" }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// POSTs a verification and returns the VerificationId.
    /// </summary>
    private static async Task<Guid> PostVerificationAsync(HttpClient client, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/onboarding/verification", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var doc = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>(ct);
        return doc!.RootElement.GetProperty("verificationId").GetGuid();
    }

    /// <summary>Resolves (trustBankId, depositBankId) for the given org via a direct DB read.</summary>
    private async Task<(Guid TrustBankId, Guid DepositBankId)> ResolveBankIdsAsync(
        Guid orgId, CancellationToken ct)
    {
        var tenant = new TenantContext { OrgId = orgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        Guid trustId = default;
        Guid depositId = default;

        await executor.RunAsync(orgId, async () =>
        {
            var banks = await db.Set<BankAccount>()
                .AsNoTracking()
                .Where(b => b.IsActive)
                .ToListAsync(ct);

            trustId = banks.First(b => b.Purpose == BankPurpose.Trust).Id;
            depositId = banks.First(b => b.Purpose == BankPurpose.Deposit).Id;
        }, ct);

        return (trustId, depositId);
    }

    /// <summary>Imports owner → property → unit → tenant/lease chain (same as VerificationSignoffTests).</summary>
    private static async Task ImportOwnerTenantChainAsync(HttpClient client, CancellationToken ct)
    {
        await PostImportAsync(client, "owners",
            new { csvContent = "Owner ID,Owner Name,Reserve\nO-1,SignedOff Owner LLC,0\n", filename = "owners.csv" }, ct);
        await PostImportAsync(client, "properties",
            new { csvContent = "Property ID,Owner ID,Address\nP-1,O-1,1 SignedOff St\n", filename = "properties.csv" }, ct);
        await PostImportAsync(client, "units",
            new { csvContent = "Unit ID,Property ID,Unit,Rent,Status\nUNIT-1,P-1,Unit A,1000.00,occupied\n", filename = "units.csv" }, ct);
        await PostImportAsync(client, "tenants_leases",
            new
            {
                csvContent = "Tenant ID,Unit ID,Tenant Name,Lease Start,Lease End,Rent,Deposit,Status\n" +
                             "T-1,UNIT-1,SignedOff Tenant,2025-01-01,,1000.00,500.00,active\n",
                filename = "tenants.csv",
            }, ct);
    }
}
