using System.Net;
using System.Net.Http.Json;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
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
    }

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
