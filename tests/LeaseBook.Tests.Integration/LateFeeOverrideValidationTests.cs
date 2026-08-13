using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// WP-6: the per-lease late-fee overrides are a write path into money policy, and they were
/// accepted unvalidated — `UpdateOrgSettings` validates all five equivalent org-default fields
/// while `CreateLease`/`UpdateLease` validated none of them.
/// <para>
/// Two distinct failure modes are covered. An unknown <c>lateFeeKindOverride</c> reached
/// <c>LateFeeKindConverter.FromDb</c>, which throws <see cref="ArgumentOutOfRangeException"/> — an
/// unhandled exception surfacing as a 500 where the identical org-settings input returns a 400. The
/// numeric fields were worse than a wrong status code: a negative fee or an out-of-range rent due
/// day was accepted and persisted, then fed the late-fee run through
/// <c>ILateFeePolicyData</c>.
/// </para>
/// <para>
/// Null stays legal throughout — null means "inherit the org default" for that field, so these
/// tests must never make absence itself an error.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LateFeeOverrideValidationTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";

    public static TheoryData<string, object?> InvalidOverrides() => new()
    {
        // Not in LateFeeKindConverter.DbValues — the 500-instead-of-400 case.
        { "lateFeeKindOverride", "flatt" },
        { "lateFeeKindOverride", "FLAT" },     // converter is case-sensitive
        { "lateFeeKindOverride", "" },
        // Rent due day is bounded 1–28 org-side (28 so every month has the day).
        { "lateFeeRentDueDayOverride", 0 },
        { "lateFeeRentDueDayOverride", 29 },
        { "lateFeeRentDueDayOverride", -1 },
        // Negative money / rates are never legal.
        { "lateFeeGraceDaysOverride", -1 },
        { "lateFeeAmountOverride", -0.01 },
        { "lateFeeRateBpsOverride", -1 },
    };

    [Theory]
    [MemberData(nameof(InvalidOverrides))]
    public async Task Create_rejects_an_invalid_late_fee_override(string field, object? value)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var body = LeaseBody(setup, status: "pending");
        body[field] = value;

        var response = await client.PostAsJsonAsync("/api/directory/leases", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            $"{field}={value} must be rejected as a validation error, not accepted or thrown as a 500; " +
            $"got {(int)response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(InvalidOverrides))]
    public async Task Update_rejects_an_invalid_late_fee_override(string field, object? value)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var body = LeaseBody(setup);
        body[field] = value;

        var response = await client.PutAsJsonAsync($"/api/directory/leases/{setup.LeaseId}", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            $"{field}={value} must be rejected as a validation error, not accepted or thrown as a 500; " +
            $"got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Omitting_every_override_is_valid_because_null_means_inherit()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var response = await client.PostAsJsonAsync(
            "/api/directory/leases",
            LeaseBody(setup, status: "pending"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "null overrides mean 'use the org default' — validation must not require them");
    }

    [Fact]
    public async Task Valid_explicit_overrides_are_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var body = LeaseBody(setup, status: "pending");
        body["lateFeeRentDueDayOverride"] = 28;   // upper bound is legal
        body["lateFeeGraceDaysOverride"] = 0;     // zero grace is legal, and is NOT "inherit"
        body["lateFeeKindOverride"] = "percent";
        body["lateFeeAmountOverride"] = 0m;       // a zero flat fee is legal
        body["lateFeeRateBpsOverride"] = 500;

        var response = await client.PostAsJsonAsync("/api/directory/leases", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "boundary-legal override values must not be rejected");
    }

    [Fact]
    public async Task Tenant_detail_returns_the_overrides_so_an_edit_can_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // Override two of the five; the other three stay inherited.
        var body = LeaseBody(setup);
        body["lateFeeGraceDaysOverride"] = 0;   // explicit zero, not "inherit"
        body["lateFeeKindOverride"] = "percent";
        (await client.PutAsJsonAsync($"/api/directory/leases/{setup.LeaseId}", body, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var detail = await client.GetFromJsonAsync<JsonElement>(
            $"/api/directory/tenants/{setup.TenantId}", ct);

        var lease = detail.GetProperty("lease");

        // The read has to carry the lease id and unit id too: UpdateLease replaces the whole lease,
        // so a client that cannot read them back cannot save one field without destroying the rest.
        lease.GetProperty("id").GetGuid().ShouldBe(setup.LeaseId);
        lease.GetProperty("unitId").GetGuid().ShouldBe(setup.UnitId);

        lease.GetProperty("lateFeeGraceDaysOverride").GetInt32().ShouldBe(0);
        lease.GetProperty("lateFeeKindOverride").GetString().ShouldBe("percent");

        // Inherited fields must come back as null, never as the org default copied onto the lease —
        // copying would pin the lease to today's default and silently break inheritance.
        lease.GetProperty("lateFeeRentDueDayOverride").ValueKind.ShouldBe(JsonValueKind.Null);
        lease.GetProperty("lateFeeAmountOverride").ValueKind.ShouldBe(JsonValueKind.Null);
        lease.GetProperty("lateFeeRateBpsOverride").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Setup(Guid OrgId, string Email, Guid TenantId, Guid UnitId, Guid LeaseId);

    /// <summary>A minimal valid lease payload; each test mutates the one field under test.</summary>
    private static Dictionary<string, object?> LeaseBody(Setup setup, string status = "active") => new()
    {
        ["tenantId"] = setup.TenantId,
        ["unitId"] = setup.UnitId,
        ["startDate"] = "2025-01-01",
        ["endDate"] = "2027-12-31",
        ["rent"] = 1200m,
        ["depositRequired"] = 1200m,
        ["status"] = status,
    };

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Late Fee Validation Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"latefee-validate-{orgId:N}@example.com";
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
                DisplayName = "Late Fee Validator",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        Guid tenantId = default, unitId = default, leaseId = default;
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
            {
                var ownerId = await sender.Send(new CreateOwner("Validate Owner", null, null, null, null, 0m), ct);
                var propId = await sender.Send(new CreateProperty(ownerId, "1 Validate St", "Raleigh", "NC", null, null), ct);
                unitId = await sender.Send(new CreateUnit(propId, "#1", 1200m, "occupied"), ct);
                tenantId = await sender.Send(new CreateTenant("Validate Tenant", null, null, "current"), ct);
                leaseId = await sender.Send(
                    new CreateLease(tenantId, unitId, new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31),
                        1200m, 1200m, "active"), ct);
                await sender.Send(new CreateBankAccount("Trust", null, null, "trust"), ct);
            }, ct);
        }

        return new Setup(orgId, email, tenantId, unitId, leaseId);
    }

    private async Task<HttpClient> LoggedInClientAsync(Setup setup, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = setup.Email, password = Password }, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, "login must succeed before testing lease endpoints");
        await client.PrimeCsrfAsync(ct); // XSRF token rotates on sign-in
        return client;
    }
}
