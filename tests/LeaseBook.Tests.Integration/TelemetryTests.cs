using System.Net;
using System.Net.Http.Json;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Endpoints;
using LeaseBook.Web.Seeding;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The click-budget telemetry sink (§C.8). It is staff-gated (401 unauthenticated) and accepts a
/// tags-only sample, returning 204 so the fire-and-forget client call never blocks the UI.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class TelemetryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Budget_endpoint_requires_auth_and_accepts_a_sample()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        // Unauthenticated → 401 (RequirePMStaff), even with a valid CSRF token.
        var anon = fixture.Api.CreateClient();
        await anon.PrimeCsrfAsync(ct);
        var unauth = await anon.PostAsJsonAsync(
            "/api/telemetry/budget", new { task = "entity-jump", interactions = 2, met = true }, ct);
        unauth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Authenticated as the seeded admin → 204.
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(DemoSeeder.AdminEmail, DemoSeeder.AdminPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The XSRF token rotates on sign-in — re-prime before the authenticated mutation (as the SPA does).
        await client.PrimeCsrfAsync(ct);
        var sample = await client.PostAsJsonAsync(
            "/api/telemetry/budget", new { task = "owner-balances-visible", interactions = 0, met = true }, ct);
        sample.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// Every sample lands as span tags, so the sink accepts exactly the budgeted tasks the SPA
    /// reports — matched ordinally — and a non-negative interaction count.
    /// </summary>
    [Theory]
    [InlineData("not-a-budgeted-task", 1)]
    [InlineData("", 1)]
    [InlineData("record-payment\nforged-line", 1)]
    [InlineData("RECORD-PAYMENT", 1)]
    [InlineData("record-payment", -1)]
    public async Task Budget_endpoint_rejects_a_sample_outside_the_budgeted_contract(string task, int interactions)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await SignedInClientAsync(ct);

        var response = await client.PostAsJsonAsync(
            "/api/telemetry/budget", new { task, interactions, met = true }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Budget_endpoint_rejects_an_oversized_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await SignedInClientAsync(ct);

        var response = await client.PostAsJsonAsync(
            "/api/telemetry/budget", new { task = new string('x', 100_000), interactions = 1, met = true }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Budget_endpoint_accepts_every_budgeted_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await SignedInClientAsync(ct);

        foreach (var task in BudgetTasks.All)
        {
            var response = await client.PostAsJsonAsync(
                "/api/telemetry/budget", new { task, interactions = 2, met = true }, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, task);
        }
    }

    private async Task<HttpClient> SignedInClientAsync(CancellationToken ct)
    {
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(DemoSeeder.AdminEmail, DemoSeeder.AdminPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct);
        return client;
    }
}
