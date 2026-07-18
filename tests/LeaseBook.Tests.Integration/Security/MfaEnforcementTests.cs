using System.Net;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

[Collection(nameof(DatabaseCollection))]
public sealed class MfaEnforcementTests(PostgresFixture fixture)
{
    // A protected, non-exempt PMStaff-or-above endpoint used as the probe.
    private const string ProtectedPath = "/api/accounting/banks/balances";

    private HttpClient EnforcingClient() =>
        fixture.Api.WithWebHostBuilder(b => b.UseSetting("Auth:EnforceAdminMfa", "true")).CreateClient();

    [Fact]
    public async Task Unenrolled_admin_is_blocked_with_problem_details_but_can_reach_enrollment()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        var email = $"admin-{orgId:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "MFA Enforce Org", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Admin", Roles.PMAdmin, ct);

        var client = EnforcingClient();
        await AuthTestSupport.PrimeCsrfAsync(client, ct);
        (await AuthTestSupport.LoginAsync(client, email, ct)).Status.ShouldBe(LoginStatus.Ok);

        // Blocked from business endpoints...
        var blocked = await client.GetAsync(ProtectedPath, ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        blocked.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        // ...but the enrollment endpoints stay reachable.
        await AuthTestSupport.PrimeCsrfAsync(client, ct);
        var enroll = await client.PostAsync("/api/auth/mfa/enroll", content: null, ct);
        enroll.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_gains_access_after_enrolling_without_a_relogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        var email = $"admin2-{orgId:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "MFA Enroll Org", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Admin", Roles.PMAdmin, ct);

        var client = EnforcingClient();
        await AuthTestSupport.PrimeCsrfAsync(client, ct);
        await AuthTestSupport.LoginAsync(client, email, ct);
        await AuthTestSupport.PrimeCsrfAsync(client, ct);

        await AuthTestSupport.EnrollMfaAsync(client, ct); // confirm refreshes the sign-in cookie

        var afterEnroll = await client.GetAsync(ProtectedPath, ct);
        afterEnroll.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Staff_is_unaffected_by_admin_mfa_enforcement()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        var email = $"staff-{orgId:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "MFA Staff Org", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Staff", Roles.PMStaff, ct);

        var client = EnforcingClient();
        await AuthTestSupport.PrimeCsrfAsync(client, ct);
        await AuthTestSupport.LoginAsync(client, email, ct);

        (await client.GetAsync(ProtectedPath, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Enforcement_off_by_default_lets_an_unenrolled_admin_through()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        var email = $"admin3-{orgId:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "MFA Off Org", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Admin", Roles.PMAdmin, ct);

        var client = fixture.Api.CreateClient(); // default Development config: flag off
        await AuthTestSupport.PrimeCsrfAsync(client, ct);
        await AuthTestSupport.LoginAsync(client, email, ct);

        (await client.GetAsync(ProtectedPath, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
