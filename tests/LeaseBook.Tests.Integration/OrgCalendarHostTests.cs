using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using LeaseBook.Web.Reporting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Which day it is for the organization, across the host (#642, ADR-037). The clock is pinned at
/// 01:00 UTC on the first of next month: 20:00 or 21:00 on the last day of this month in New York, the
/// default zone. The UTC date and month are already the next ones; every "today" and "current month"
/// the server works out must still be the organization's.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OrgCalendarHostTests(PostgresFixture fixture)
{
    // In the future, so a sign-in cookie the test client holds is not expired by the real clock.
    private static readonly DateTimeOffset Evening = FirstOfNextMonthAt1amUtc();

    // Independent of the code under test: at 01:00 UTC the US day is still the previous one.
    private static readonly DateOnly UtcDate = DateOnly.FromDateTime(Evening.UtcDateTime);
    private static readonly DateOnly OrgToday = UtcDate.AddDays(-1);

    [Fact]
    public async Task A_void_dated_the_utc_date_is_refused_as_the_organizations_tomorrow_with_422()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = WithEveningClock();
        using var client = await SignedInAsync(host, ct);
        var entryId = UuidV7.NewId();

        var tomorrow = await client.PostAsJsonAsync($"/api/accounting/entries/{entryId}/void",
            new { reason = "posted twice", asOfDate = UtcDate, sourceRef = "void:utc" }, ct);
        tomorrow.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await tomorrow.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe("posting_date_in_future");

        // The organization's today, given or left to the default, gets past the date check to the
        // missing entry.
        (await client.PostAsJsonAsync($"/api/accounting/entries/{entryId}/void",
            new { reason = "posted twice", asOfDate = OrgToday, sourceRef = "void:org" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/accounting/entries/{entryId}/void",
            new { reason = "posted twice", asOfDate = (DateOnly?)null, sourceRef = "void:default" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_ownership_transfer_dated_the_utc_date_is_refused_with_the_same_code_as_money()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = WithEveningClock();
        var (orgId, client) = await SignedInWithOrgAsync(host, ct);
        using var _ = client;
        Guid propertyId = default, buyerId = default;
        await InOrgAsync(host, orgId, async sender =>
        {
            var sellerId = await sender.Send(new CreateOwner("Seller", null, null, null, 800, 0m), ct);
            buyerId = await sender.Send(new CreateOwner("Buyer", null, null, null, 800, 0m), ct);
            propertyId = await sender.Send(new CreateProperty(sellerId, "2 Sale Street", "Raleigh", "NC", null, null), ct);
        }, ct);

        var refused = await client.PostAsJsonAsync($"/api/directory/properties/{propertyId}/ownership-transfers",
            new { toOwnerId = buyerId, effectiveDate = UtcDate, sourceRef = "xfer:utc" }, ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe("posting_date_in_future");
        problem.GetProperty("detail").GetString().ShouldBe(
            $"The date {UtcDate:yyyy-MM-dd} is after today, {OrgToday:yyyy-MM-dd}, in the organization's time zone.");

        (await client.PostAsJsonAsync($"/api/directory/properties/{propertyId}/ownership-transfers",
            new { toOwnerId = buyerId, effectiveDate = OrgToday, sourceRef = "xfer:org" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Each zone at an instant where it is still the last day of the month but UTC, and New York in the
    /// Pacific case, have reached the first: Eastern at 01:00 UTC, Pacific at 06:30 UTC.
    /// </summary>
    public static TheoryData<string, int> Evenings() => new()
    {
        { "America/New_York", 0 },
        { "America/Los_Angeles", 330 },
    };

    [Theory]
    [MemberData(nameof(Evenings))]
    public async Task A_lease_ending_on_the_organizations_today_is_still_current_and_one_starting_tomorrow_is_not(
        string zone, int minutesAfterEvening)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = WithClock(Evening.AddMinutes(minutesAfterEvening));
        var (orgId, client) = await SignedInInZoneAsync(host, zone, ct);
        using var _ = client;

        Guid ending = default, starting = default;
        await InOrgAsync(host, orgId, async sender =>
        {
            var ownerId = await sender.Send(new CreateOwner("Calendar Owner", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(new CreateProperty(ownerId, "1 Evening Row", "Raleigh", "NC", null, null), ct);
            var unitA = await sender.Send(new CreateUnit(propertyId, "A", 1000m, "available"), ct);
            var unitB = await sender.Send(new CreateUnit(propertyId, "B", 1000m, "available"), ct);
            ending = await sender.Send(new CreateTenant("Ending Tonight", null, null, "current"), ct);
            starting = await sender.Send(new CreateTenant("Starting Tomorrow", null, null, "current"), ct);
            await sender.Send(new CreateLease(ending, unitA, OrgToday.AddYears(-1), OrgToday, 1000m, 1000m, "active"), ct);
            await sender.Send(new CreateLease(starting, unitB, UtcDate, UtcDate.AddYears(1), 1000m, 1000m, "active"), ct);
        }, ct);

        var endingDetail = await client.GetFromJsonAsync<JsonElement>($"/api/directory/tenants/{ending}", ct);
        endingDetail.GetProperty("lease").GetProperty("endDate").GetString().ShouldBe(OrgToday.ToString("yyyy-MM-dd"));
        var startingDetail = await client.GetFromJsonAsync<JsonElement>($"/api/directory/tenants/{starting}", ct);
        startingDetail.GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [MemberData(nameof(Evenings))]
    public async Task Default_periods_and_as_of_dates_are_the_organizations_month_and_day(
        string zone, int minutesAfterEvening)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = WithClock(Evening.AddMinutes(minutesAfterEvening));
        var (orgId, client) = await SignedInInZoneAsync(host, zone, ct);
        using var _ = client;
        Guid ownerId = default;
        await InOrgAsync(host, orgId, async sender =>
            ownerId = await sender.Send(new CreateOwner("Period Owner", null, null, null, 800, 0m), ct), ct);

        var statement = await client.GetFromJsonAsync<JsonElement>($"/api/statements/{ownerId}", ct);
        (statement.GetProperty("year").GetInt32(), statement.GetProperty("month").GetInt32())
            .ShouldBe((OrgToday.Year, OrgToday.Month));

        var run = await client.GetFromJsonAsync<JsonElement>("/api/operations/runs/rent/preview", ct);
        (run.GetProperty("year").GetInt32(), run.GetProperty("month").GetInt32())
            .ShouldBe((OrgToday.Year, OrgToday.Month));

        // The applied filters travel in the CSV export's header; read them from the service both use.
        ReportPreviewResult mgmtFee = null!, delinquency = null!;
        await InOrgAsync(host, orgId, async services =>
        {
            var previews = services.GetRequiredService<ReportPreviewService>();
            var none = new ReportFilters(null, null, null, null, null, null, null);
            mgmtFee = (await previews.PreviewAsync("mgmt-fee", none, ct))!;
            delinquency = (await previews.PreviewAsync("delinquency", none, ct))!;
        }, ct);
        mgmtFee.AppliedFilters!.Select(f => (f.Name, f.Value)).ShouldBe(
        [
            ("year", OrgToday.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("month", OrgToday.Month.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]);
        delinquency.AppliedFilters!.Select(f => (f.Name, f.Value)).ShouldBe([("asOf", OrgToday.ToString("yyyy-MM-dd"))]);
    }

    [Fact]
    public async Task The_stored_zone_decides_the_day()
    {
        // Five hours later, 06:00 UTC on the first, New York has reached the first (01:00 or 02:00)
        // but Honolulu (UTC-10) is still at 20:00 on the last day of the month.
        var ct = TestContext.Current.CancellationToken;
        await using var host = WithClock(Evening.AddHours(5));
        var (_, admin) = await SignedInWithOrgAsync(host, ct, Roles.PMAdmin);
        using var client = admin;
        var entryId = UuidV7.NewId();

        // Eastern, the default: the first is today, so the void gets past the date check.
        (await client.PostAsJsonAsync($"/api/accounting/entries/{entryId}/void",
            new { reason = "posted twice", asOfDate = UtcDate, sourceRef = "void:east" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await client.PutAsJsonAsync("/api/settings/org", new { timeZone = "Pacific/Honolulu" }, ct)).EnsureSuccessStatusCode();

        // Hawaii: the first is tomorrow.
        (await client.PostAsJsonAsync($"/api/accounting/entries/{entryId}/void",
            new { reason = "posted twice", asOfDate = UtcDate, sourceRef = "void:hnl" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.PostAsJsonAsync($"/api/accounting/entries/{entryId}/void",
            new { reason = "posted twice", asOfDate = OrgToday, sourceRef = "void:hnl-today" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static DateTimeOffset FirstOfNextMonthAt1amUtc()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, 1, 1, 0, 0, TimeSpan.Zero).AddMonths(1);
    }

    private WebApplicationFactory<Program> WithEveningClock() => WithClock(Evening);

    private WebApplicationFactory<Program> WithClock(DateTimeOffset now) =>
        fixture.Api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new StoppedClock(now));
        }));

    private async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> host, CancellationToken ct) =>
        (await SignedInWithOrgAsync(host, ct)).Client;

    private async Task<(Guid OrgId, HttpClient Client)> SignedInWithOrgAsync(
        WebApplicationFactory<Program> host, CancellationToken ct, string role = Roles.PMStaff)
    {
        var orgId = UuidV7.NewId();
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, $"Calendar org {orgId:N}", ct);
        var email = $"calendar-{orgId:N}@calendar.test";
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Calendar staff", role, ct);

        var client = host.CreateClient();
        await client.PrimeCsrfAsync(ct);
        (await AuthTestSupport.LoginAsync(client, email, ct)).Status.ShouldBe("ok");
        await client.PrimeCsrfAsync(ct);
        return (orgId, client);
    }

    /// <summary>An administrator signed in to a new organization that keeps its books in <paramref name="zone"/>.</summary>
    private async Task<(Guid OrgId, HttpClient Client)> SignedInInZoneAsync(
        WebApplicationFactory<Program> host, string zone, CancellationToken ct)
    {
        var (orgId, client) = await SignedInWithOrgAsync(host, ct, Roles.PMAdmin);
        if (zone != "America/New_York")
        {
            (await client.PutAsJsonAsync("/api/settings/org", new { timeZone = zone }, ct)).EnsureSuccessStatusCode();
        }

        return (orgId, client);
    }

    private static Task InOrgAsync(
    WebApplicationFactory<Program> host, Guid orgId, Func<ISender, Task> work, CancellationToken ct) =>
    InOrgAsync(host, orgId, services => work(services.GetRequiredService<ISender>()), ct);

    private static async Task InOrgAsync(
        WebApplicationFactory<Program> host, Guid orgId, Func<IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        await executor.RunAsSystemAsync(orgId, "test-harness", () => work(scope.ServiceProvider), ct);
    }
}
