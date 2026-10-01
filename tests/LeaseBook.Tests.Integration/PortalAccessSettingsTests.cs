using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Shouldly;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class PortalAccessSettingsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Permission_only_update_preserves_newer_profile_accounting_and_late_fee_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var client = admin;
        (await client.PutAsJsonAsync("/api/settings/org", new
        {
            accountingBasis = "accrual",
            moneyNegativeDisplay = "parens",
            legalName = "Updated management company",
            address = "Updated address",
            lateFeeGraceDays = 8,
            lateFeeAmount = 75m,
        }, ct)).EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/settings/portal-access", new { staffCanManagePortalAccess = false }, ct))
            .EnsureSuccessStatusCode();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct);
        settings.GetProperty("staffCanManagePortalAccess").GetBoolean().ShouldBeFalse();
        settings.GetProperty("accountingBasis").GetString().ShouldBe("accrual");
        settings.GetProperty("moneyNegativeDisplay").GetString().ShouldBe("parens");
        settings.GetProperty("legalName").GetString().ShouldBe("Updated management company");
        settings.GetProperty("address").GetString().ShouldBe("Updated address");
        settings.GetProperty("lateFeeGraceDays").GetInt32().ShouldBe(8);
        settings.GetProperty("lateFeeAmount").GetDecimal().ShouldBe(75m);
    }

    [Fact]
    public async Task Admin_can_restrict_staff_portal_management_and_unrelated_updates_preserve_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var client = admin;
        var defaults = await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct);
        defaults.GetProperty("staffCanManagePortalAccess").GetBoolean().ShouldBeTrue();

        var changed = await client.PutAsJsonAsync("/api/settings/org", new { staffCanManagePortalAccess = false }, ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await changed.Content.ReadFromJsonAsync<JsonElement>(ct))
            .GetProperty("staffCanManagePortalAccess").GetBoolean().ShouldBeFalse();

        (await client.PutAsJsonAsync("/api/settings/org", new { moneyNegativeDisplay = "parens" }, ct))
            .EnsureSuccessStatusCode();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct);
        settings.GetProperty("staffCanManagePortalAccess").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Staff_can_read_but_cannot_change_the_policy_and_other_organizations_keep_the_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var adminClient = admin;
        (await adminClient.PutAsJsonAsync("/api/settings/org", new { staffCanManagePortalAccess = false }, ct))
            .EnsureSuccessStatusCode();
        var email = $"portal-staff-{orgId:N}@example.test";
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Staff", Roles.PMStaff, ct);
        using var staff = fixture.Api.CreateClient();
        await staff.PrimeCsrfAsync(ct);
        await AuthTestSupport.LoginAsync(staff, email, ct);
        await staff.PrimeCsrfAsync(ct);

        var settings = await staff.GetFromJsonAsync<JsonElement>("/api/settings/org", ct);
        settings.GetProperty("staffCanManagePortalAccess").GetBoolean().ShouldBeFalse();
        (await staff.PutAsJsonAsync("/api/settings/org", new { staffCanManagePortalAccess = true }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await staff.PutAsJsonAsync("/api/settings/portal-access", new { staffCanManagePortalAccess = true }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (_, other) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMStaff, ct);
        using var otherClient = other;
        var otherSettings = await otherClient.GetFromJsonAsync<JsonElement>("/api/settings/org", ct);
        otherSettings.GetProperty("staffCanManagePortalAccess").GetBoolean().ShouldBeTrue();
    }
}
