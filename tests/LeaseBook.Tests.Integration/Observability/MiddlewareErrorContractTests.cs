using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Shouldly;

namespace LeaseBook.Tests.Integration.Observability;

/// <summary>
/// The error contract, checked on the responses the host actually emits rather than on the calls its
/// source makes.
///
/// <para>
/// <c>ErrorContractTests</c> reads IL for direct <c>Results.Problem</c> calls outside
/// <c>ProblemResults</c>. That proves the responses the factory builds are well-formed and is blind,
/// structurally and at any scan fidelity, to an error response written by middleware or a framework
/// event — such a path never makes a call for it to find. Two responses went missing through exactly
/// that gap: the cookie handler's unauthenticated 401, bare from WP-06 until #357, and the
/// MFA-enrollment 403, which served <c>application/problem+json</c> carrying neither <c>code</c> nor
/// <c>correlationId</c> until #361. Two of a kind was ADR-025's trigger to gate this surface by what
/// it emits instead.
/// </para>
///
/// <para>
/// So this suite drives each middleware-written error path against the real host and asserts the
/// emitted response. A third one written by hand is then caught by the shape it puts on the wire,
/// whether or not its author ever touches the factory.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class MiddlewareErrorContractTests(PostgresFixture fixture)
{
    // A protected, non-exempt PMStaff-or-above endpoint, used as the probe for both 403 shapes.
    private const string ProtectedPath = "/api/accounting/banks/balances";

    /// <summary>
    /// The cookie handler's 401 (`OnRedirectToLogin`), written by a framework event before any
    /// endpoint runs.
    /// </summary>
    [Fact]
    public async Task The_unauthenticated_401_carries_the_contract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await fixture.Api.CreateClient().GetAsync(ProtectedPath, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldCarryTheContractAsync(response, "not_authenticated", ct);
    }

    /// <summary>
    /// The MFA-enrollment 403 (`MfaAuthorizationResultHandler`), written by an
    /// <c>IAuthorizationMiddlewareResultHandler</c>. This is the response #361 fixed: it was already
    /// problem+json, so only reading the body could tell that the contract was missing from it.
    /// </summary>
    [Fact]
    public async Task The_mfa_enrollment_403_carries_the_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        var email = $"mfa403-{orgId:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "MFA Contract Org", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Admin", Roles.PMAdmin, ct);

        var client = fixture.Api
            .WithWebHostBuilder(b => b.UseSetting("Auth:EnforceAdminMfa", "true"))
            .CreateClient();
        await client.PrimeCsrfAsync(ct);
        (await AuthTestSupport.LoginAsync(client, email, ct)).Status.ShouldBe(LoginStatus.Ok);

        var response = await client.GetAsync(ProtectedPath, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await ShouldCarryTheContractAsync(response, "mfa_enrollment_required", ct);
    }

    /// <summary>
    /// The antiforgery rejection (`ApiAntiforgeryMiddleware`), which runs ahead of authorization — so
    /// an unsafe /api request without a token is answered here, before any endpoint or policy is
    /// consulted. It already used the factory; it is driven here because the suite's value is the
    /// list of paths it covers, and a middleware-written response absent from that list is invisible
    /// to it exactly as these responses were to the IL scan.
    /// </summary>
    [Fact]
    public async Task The_antiforgery_400_carries_the_contract()
    {
        var ct = TestContext.Current.CancellationToken;

        // No PrimeCsrfAsync: the token is deliberately absent.
        var response = await fixture.Api.CreateClient().PostAsync(ProtectedPath, content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await ShouldCarryTheContractAsync(response, "antiforgery_rejected", ct);
    }

    /// <summary>
    /// The other half of the pair, and the reason the test above matters: a *role* denial stays bare.
    /// `AuthorizationMatrixTests` owns that assertion; this one states the consequence the two
    /// together buy — on a 403, a problem body means MFA enforcement and nothing else. Giving the
    /// role denial a body would void that inference without failing either test in isolation.
    /// </summary>
    [Fact]
    public async Task A_problem_body_on_a_403_means_mfa_enforcement_and_not_a_role_denial()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        var email = $"staff403-{orgId:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Role Denial Org", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Staff", Roles.PMStaff, ct);

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        await AuthTestSupport.LoginAsync(client, email, ct);
        await client.PrimeCsrfAsync(ct);

        var adminOnlyPath = $"/api/accounting/reconciliations/{UuidV7.NewId()}/unlock";
        var denied = await client.PostAsync(adminOnlyPath, content: null, ct);

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        denied.Content.Headers.ContentType?.MediaType.ShouldNotBe("application/problem+json");
        // Asserted on the body, not just the content type: `ContentType?.MediaType` is null on a
        // body-less response, so the check above passes trivially and would keep passing if this
        // denial grew a body under any other media type — which would quietly falsify the runbook,
        // the deliberately-bare table, and the inference the MFA test above depends on.
        (await denied.Content.ReadAsStringAsync(ct)).ShouldBeEmpty();

        // And the other half of the inference: the same status, from MFA enforcement, does carry one.
        // Asserting both here is what makes "a problem body on a 403 means MFA" a property of this
        // file rather than a claim spread across two suites that cannot see each other.
        await The_mfa_enrollment_403_carries_the_contract();
    }

    /// <summary>
    /// The rate limiter's 429 (`OnRejected`), the third middleware-written error response and the one
    /// that is deliberately bare: it carries <c>Retry-After</c> and nothing else, because the header
    /// is the actionable part. Asserted so that giving it a body becomes a deliberate edit here
    /// rather than a silent change — the bare list is only meaningful if something reads it.
    /// </summary>
    [Fact]
    public async Task The_rate_limit_429_is_deliberately_bare_and_carries_only_retry_after()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Api
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("RateLimiting:AuthPermitLimit", "1");
                b.UseSetting("RateLimiting:AuthWindowSeconds", "60");
            })
            .CreateClient();
        await client.PrimeCsrfAsync(ct);

        // The "auth" policy partitions per IP, and the TestServer reports none, so every client in
        // this host shares the "unknown" partition — one permitted request, then rejection.
        HttpResponseMessage? limited = null;
        for (var attempt = 0; attempt < 6 && limited is null; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login", new LoginRequest($"nobody-{UuidV7.NewId():N}@example.com", "irrelevant"), ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
        }

        limited.ShouldNotBeNull("the rate limiter never rejected, so this test proved nothing");
        limited.Content.Headers.ContentType?.MediaType.ShouldNotBe("application/problem+json");
        (await limited.Content.ReadAsStringAsync(ct)).ShouldBeEmpty();
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }

    /// <summary>
    /// A problem response carries a machine-readable <c>code</c> and a correlation id an operator can
    /// quote. Reads the wire body rather than a typed result, because the point of this suite is what
    /// the response contains, not what built it.
    /// </summary>
    private static async Task ShouldCarryTheContractAsync(
        HttpResponseMessage response, string expectedCode, CancellationToken ct)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe(
            "application/problem+json",
            "a bare status here means the response never reached the factory at all");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = body.RootElement;

        root.TryGetProperty("code", out var code).ShouldBeTrue(
            $"a middleware-written error response must carry `code` — '{expectedCode}' had only a title, "
            + "which the SPA's `code ?? title` fallback then promotes into the machine-readable slot");
        code.GetString().ShouldBe(expectedCode);

        root.TryGetProperty("correlationId", out var correlationId).ShouldBeTrue(
            "without a correlationId the operator has a problem body with no reference to quote, "
            + "which is worse than a bare status: it looks diagnosable and is not");
        correlationId.GetString().ShouldNotBeNullOrWhiteSpace();

        root.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
