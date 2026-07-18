using System.Net;
using System.Net.Http.Json;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

/// <summary>
/// Proves the "auth" rate-limit policy trips on a tiny, test-local limit. Runs against an isolated
/// <see cref="ApiFactory"/> built via <c>WithWebHostBuilder</c> with <c>RateLimiting:AuthPermitLimit</c>
/// overridden to 3 — its own limiter state, so it never bleeds into (or is bled into by) the shared
/// <see cref="PostgresFixture.Api"/> host used by every other auth test in the suite. The shared host
/// keeps the generous appsettings.json default (1000/min) so TestServer's shared "unknown" IP partition
/// (no RemoteIpAddress under TestServer) never trips for other tests.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AuthRateLimitTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Login_is_throttled_after_the_configured_window_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = fixture.Api.WithWebHostBuilder(b => b.UseSetting("RateLimiting:AuthPermitLimit", "3"));
        var client = factory.CreateClient();
        await AuthTestSupport.PrimeCsrfAsync(client, ct);

        HttpStatusCode last = HttpStatusCode.OK;
        // Limit is 3/min for this isolated host; the 4th unauthenticated attempt trips the limiter
        // regardless of credentials.
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login", new LoginRequest("nobody@example.com", "wrong-password-123!"), ct);
            last = response.StatusCode;
        }

        last.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
