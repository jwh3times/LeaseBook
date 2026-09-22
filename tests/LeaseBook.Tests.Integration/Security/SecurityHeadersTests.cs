using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

[Collection(nameof(DatabaseCollection))]
public sealed class SecurityHeadersTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("/api/health")]
    [InlineData("/")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var client = fixture.Api.CreateClient();
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.GetValues("Referrer-Policy").ShouldContain("no-referrer");
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
        response.Headers.Contains("Permissions-Policy").ShouldBeTrue();
        response.Headers.GetValues("Cross-Origin-Opener-Policy").ShouldContain("same-origin");
        response.Headers.GetValues("Cross-Origin-Resource-Policy").ShouldContain("same-origin");
    }

    /// <summary>
    /// Local development is plain <c>http://localhost</c>, and browsers apply
    /// <c>upgrade-insecure-requests</c> to localhost too — the SPA's own assets would be rewritten to
    /// an https origin nothing serves. So the directive rides with HSTS: present wherever the edge
    /// terminates TLS, absent in Development.
    /// </summary>
    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    public async Task Https_upgrade_directives_are_sent_only_outside_Development(string environment, bool expected)
    {
        await using var host = HostIn(environment);
        var response = await host.CreateClient().GetAsync("/", TestContext.Current.CancellationToken);

        response.Headers.GetValues("Content-Security-Policy").Single()
            .Contains("upgrade-insecure-requests").ShouldBe(expected);
        response.Headers.Contains("Strict-Transport-Security").ShouldBe(expected);
    }

    /// <summary>
    /// The test server is not Kestrel, so the <c>Server</c> header cannot be observed on the wire
    /// here; this pins the configuration that suppresses it, and CI's full-stack smoke job checks the
    /// real container's response.
    /// </summary>
    [Fact]
    public void Kestrel_does_not_announce_itself()
    {
        fixture.Api.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.AddServerHeader
            .ShouldBeFalse();
    }

    [Fact]
    public async Task The_anonymous_liveness_answer_carries_only_the_status()
    {
        var response = await fixture.Api.CreateClient().GetAsync("/api/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["status"]);
    }

    private WebApplicationFactory<Program> HostIn(string environment) =>
        fixture.Api.WithWebHostBuilder(builder => builder
            .UseEnvironment(environment)
            .UseSetting("AllowedHosts", "localhost")
            .UseSetting("Jobs:Enabled", "false"));

    /// <summary>
    /// Critical-finding regression: prior to the OnStarting fix, headers were assigned directly on
    /// context.Response.Headers, so any response driven by ASP.NET Core's ExceptionHandlerMiddleware
    /// shipped with none. That middleware clears the response (wiping headers) after a downstream
    /// handler throws, then invokes the app's IExceptionHandlers directly (they write the response
    /// without re-entering this middleware) — so ordinary 400/409/422 traffic never re-runs
    /// SecurityHeadersMiddleware's next(context) continuation. Posting a negative charge amount fails
    /// AddChargeValidator inside the CQRS ValidationCommandDecorator, which throws a FluentValidation
    /// ValidationException that propagates out of the (thin, unguarded) endpoint lambda uncaught — the
    /// exact trigger LedgerHttpTests.A_validation_failure_maps_to_400 uses — landing in
    /// ValidationExceptionHandler (§C.8 / P23) for the 400. This proves the fix survives the real
    /// clear-response path, not just the happy path already covered above.
    /// </summary>
    [Fact]
    public async Task Exception_handler_driven_response_still_carries_the_security_headers()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, $"Security Headers Org {orgId:N}", ct);
        var email = $"secheaders-{orgId:N}@example.com";
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Security Headers Tester", Roles.PMStaff, ct);

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await AuthTestSupport.LoginAsync(client, email, ct);
        login.Status.ShouldBe(LoginStatus.Ok);
        await client.PrimeCsrfAsync(ct); // XSRF token rotates on sign-in

        var response = await client.PostAsJsonAsync(
            $"/api/accounting/tenants/{Guid.NewGuid()}/charges",
            new { amount = -5m, date = new DateOnly(2026, 2, 1), kind = "rent", sourceRef = UuidV7.NewId().ToString() },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
    }
}
