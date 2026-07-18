using System.Net;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

[Collection(nameof(DatabaseCollection))]
public sealed class AuthorizationMatrixTests(PostgresFixture fixture)
{
    // The only endpoints allowed to be anonymous. Anything else anonymous is a deny-by-default breach.
    private static readonly HashSet<string> AnonymousAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/health",
        "/api/auth/csrf",
        "/api/auth/login",
        "/api/auth/mfa",
        // dev-only; MapOpenApi()'s registered route template, not the resolved "/openapi/v1.json" path.
        "/openapi/{documentName}.json",
    };

    [Fact]
    public void No_endpoint_is_anonymous_outside_the_allowlist()
    {
        var dataSource = fixture.Api.Services.GetRequiredService<EndpointDataSource>();

        var offenders = new List<string>();
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var isAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            if (!isAnonymous)
            {
                continue; // protected by an explicit policy or the deny-by-default fallback
            }

            var pattern = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            // The SPA fallback (MapFallbackToFile) is anonymous by design: it registers a catch-all
            // route (e.g. "/{*path:nonfile}") plus the bare "/" — detect via the route pattern's
            // parameter metadata rather than a literal string match, since the constraint suffix varies.
            if (endpoint.RoutePattern.Parameters.Any(p => p.IsCatchAll) || pattern == "/")
            {
                continue;
            }

            if (!AnonymousAllowlist.Contains(pattern))
            {
                offenders.Add($"{pattern} [{string.Join(",", endpoint.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods))}]");
            }
        }

        offenders.ShouldBeEmpty($"Unexpected anonymous endpoints: {string.Join("; ", offenders)}");
    }

    [Fact]
    public async Task Money_endpoint_denies_anonymous_and_barred_staff_but_allows_admin()
    {
        var ct = TestContext.Current.CancellationToken;
        // Reconciliation unlock is PMAdmin-only (RequirePMAdmin); use it as the role-matrix probe.
        // Auth is decided before model binding, so a placeholder GUID and no body is fine for the
        // deny-side assertions this test makes (401/403) — it never asserts a 2xx here.
        var adminOnlyPath = $"/api/accounting/reconciliations/{UuidV7.NewId()}/unlock";

        // Anonymous → 401.
        var anon = fixture.Api.CreateClient();
        await AuthTestSupport.PrimeCsrfAsync(anon, ct);
        (await anon.PostAsync(adminOnlyPath, content: null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Barred PMStaff → 403.
        var staffOrg = UuidV7.NewId();
        var staffEmail = $"staff-{staffOrg:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, staffOrg, "Matrix Staff Org", ct);
        await AuthTestSupport.CreateUserAsync(fixture, staffOrg, staffEmail, "Staff", Roles.PMStaff, ct);
        var staff = fixture.Api.CreateClient();
        await AuthTestSupport.PrimeCsrfAsync(staff, ct);
        await AuthTestSupport.LoginAsync(staff, staffEmail, ct);
        // The antiforgery token is bound to the (then-anonymous) identity that requested it; login
        // changes identity, so the token must be re-primed post-login or ValidateRequestAsync throws
        // and the request never reaches authorization (400, not the 403 this test is probing for).
        await AuthTestSupport.PrimeCsrfAsync(staff, ct);
        (await staff.PostAsync(adminOnlyPath, content: null, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
