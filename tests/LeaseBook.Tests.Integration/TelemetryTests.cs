using System.Net;
using System.Net.Http.Json;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Seeding;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-10: the click-budget telemetry sink (§C.8). It is staff-gated (401 unauthenticated) and accepts a
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
}
