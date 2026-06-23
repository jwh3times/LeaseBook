using System.Net;
using System.Net.Http.Json;
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
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Fix E: HTTP-level validation tests for the M6 bulk-run preview and confirm endpoints.
/// Ensures that out-of-range month or year values return 400 (invalid_period) rather than
/// a 500 from <c>DateOnly</c> throwing on construction.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OperationsValidationTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";

    [Theory]
    [InlineData("rent", 2026, 13)]  // month > 12
    [InlineData("rent", 2026, 0)]  // month < 1
    [InlineData("latefee", 2026, 13)]
    [InlineData("disbursement", 1999, 6)]  // year < 2000
    [InlineData("rent", 2101, 1)]  // year > 2100
    public async Task Preview_returns_400_for_invalid_period(string runType, int year, int month)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var response = await client.GetAsync(
            $"/api/operations/runs/{runType}/preview?year={year}&month={month}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            $"month={month} year={year} run={runType}: expected 400 invalid_period, got {response.StatusCode}");
    }

    [Theory]
    [InlineData("rent", 2026, 13)]
    [InlineData("rent", 2026, 0)]
    [InlineData("latefee", 2026, 13)]
    [InlineData("disbursement", 1999, 6)]
    [InlineData("rent", 2101, 1)]
    public async Task Confirm_returns_400_for_invalid_period(string runType, int year, int month)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var response = await client.PostAsJsonAsync(
            $"/api/operations/runs/{runType}/confirm",
            new { year, month, selectedTargetIds = Array.Empty<Guid>() },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            $"month={month} year={year} run={runType}: expected 400 invalid_period, got {response.StatusCode}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Setup(Guid OrgId, string Email);

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Ops Validation Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"ops-validate-{orgId:N}@example.com";
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
                DisplayName = "Ops Validator",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        // Provision a trust bank account so the org passes basic pre-flight checks.
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsync(orgId, async () =>
            {
                var ownerId = await sender.Send(new CreateOwner("Validate Owner", null, null, null, null, 0m), ct);
                var propId = await sender.Send(new CreateProperty(ownerId, "1 Validate St", "Raleigh", "NC", null, null), ct);
                var unitId = await sender.Send(new CreateUnit(propId, "#1", 1200m, "occupied"), ct);
                var tenantId = await sender.Send(new CreateTenant("Validate Tenant", null, null, "current"), ct);
                await sender.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31), 1200m, 1200m, "active"), ct);
                await sender.Send(new CreateBankAccount("Trust", null, null, "trust"), ct);
            }, ct);
        }

        return new Setup(orgId, email);
    }

    private async Task<HttpClient> LoggedInClientAsync(Setup setup, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = setup.Email, password = Password }, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, "login must succeed before testing operations endpoints");
        await client.PrimeCsrfAsync(ct); // XSRF token rotates on sign-in
        return client;
    }
}
