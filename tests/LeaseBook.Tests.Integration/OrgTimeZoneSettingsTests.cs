using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Audit;
using LeaseBook.Web.Auth;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The organization's time zone (#642): the calendar its books keep. It is one of seven US zones,
/// stored as an IANA id, changed by an administrator, and audited like the rest of the settings.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OrgTimeZoneSettingsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_new_organization_keeps_eastern_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, staff) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMStaff, ct);
        using var client = staff;

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct);
        settings.GetProperty("timeZone").GetString().ShouldBe("America/New_York");
    }

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("America/Chicago")]
    [InlineData("America/Denver")]
    [InlineData("America/Phoenix")]
    [InlineData("America/Los_Angeles")]
    [InlineData("America/Anchorage")]
    [InlineData("Pacific/Honolulu")]
    public async Task An_administrator_can_choose_each_listed_zone(string zone)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var client = admin;

        var saved = await client.PutAsJsonAsync("/api/settings/org", new { timeZone = zone }, ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await saved.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("timeZone").GetString().ShouldBe(zone);
        (await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct))
            .GetProperty("timeZone").GetString().ShouldBe(zone);
    }

    [Theory]
    [InlineData("Europe/London")] // a real IANA id that is not on the list
    [InlineData("Eastern Standard Time")] // a Windows id
    [InlineData("america/chicago")]
    [InlineData("")]
    public async Task A_zone_off_the_list_is_refused_and_the_stored_zone_stays(string zone)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var client = admin;
        (await client.PutAsJsonAsync("/api/settings/org", new { timeZone = "America/Chicago" }, ct))
            .EnsureSuccessStatusCode();

        var refused = await client.PutAsJsonAsync("/api/settings/org", new { timeZone = zone }, ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe("validation_failed");

        (await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct))
            .GetProperty("timeZone").GetString().ShouldBe("America/Chicago");
    }

    [Fact]
    public async Task Saving_the_card_without_a_zone_keeps_the_stored_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var client = admin;
        (await client.PutAsJsonAsync("/api/settings/org", new { timeZone = "Pacific/Honolulu" }, ct))
            .EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/settings/org", new { legalName = "Renamed", lateFeeGraceDays = 7 }, ct))
            .EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/settings/portal-access", new { staffCanManagePortalAccess = false }, ct))
            .EnsureSuccessStatusCode();

        (await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct))
            .GetProperty("timeZone").GetString().ShouldBe("Pacific/Honolulu");
    }

    [Fact]
    public async Task Staff_can_read_the_zone_but_cannot_change_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, staff) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMStaff, ct);
        using var client = staff;

        (await client.PutAsJsonAsync("/api/settings/org", new { timeZone = "America/Chicago" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct))
            .GetProperty("timeZone").GetString().ShouldBe("America/New_York");
    }

    [Fact]
    public async Task A_change_of_zone_is_in_the_audit_trail()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var client = admin;
        // The first read creates the settings row, so the change below is an update of a stored zone.
        await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct);

        (await client.PutAsJsonAsync("/api/settings/org", new { timeZone = "America/Denver" }, ct))
            .EnsureSuccessStatusCode();

        var listed = await client.GetFromJsonAsync<AuditLogResponse>("/api/audit/events?entityType=org_settings", ct);
        var changes = new List<AuditFieldChange>();
        foreach (var row in listed!.Rows)
        {
            var detail = await client.GetFromJsonAsync<AuditEventDetail>($"/api/audit/events/{row.Id}", ct);
            // The audit payload names entity properties, not columns.
            changes.AddRange(detail!.Changes.Where(change => change.Field == "TimeZone"));
        }

        changes.ShouldContain(change => change.Before == "America/New_York" && change.After == "America/Denver");
    }
}
