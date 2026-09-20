using System.Net;
using System.Net.Http.Json;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

/// <summary>
/// The property under test: outside Development, no cookie this application sets reaches the client
/// without <c>Secure</c> — <b>whatever scheme Kestrel observes</b>. Behind an edge that terminates
/// TLS, the origin sees plain HTTP, so <c>Request.IsHttps</c> is not the signal; the environment is.
/// <para>
/// Two altitudes, and the lower one is not optional. The DI theory below reads
/// <see cref="CookieAuthenticationOptions"/> and <see cref="AntiforgeryOptions"/> out of the
/// container — which can only see cookies whose flags come from those option objects. A cookie
/// written by hand onto <c>HttpResponse.Cookies</c>, or minted by a framework scheme nobody
/// configured, passes through neither, so an options assertion stays green no matter what those
/// cookies put on the wire. The wire tests exist for exactly that class of cookie and assert the
/// real <c>Set-Cookie</c> header from the real host over plain HTTP.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CookieSecurePolicyTests(PostgresFixture fixture)
{
    [Theory]
    // Environments.Development/Production are `static readonly`, not `const` (verified via
    // reflection: IsLiteral=false), so InlineData needs their literal values directly (CS0182).
    [InlineData("Development", CookieSecurePolicy.SameAsRequest)]
    [InlineData("Production", CookieSecurePolicy.Always)]
    public void Cookie_secure_policy_is_environment_driven(string environmentName, CookieSecurePolicy expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        // A DbContext is required by AddIdentity's EF stores; it need not connect for options resolution.
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql("Host=localhost;Database=none"));
        services.AddLeaseBookIdentity(new StubEnvironment(environmentName));

        using var provider = services.BuildServiceProvider();
        var cookie = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var antiforgery = provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        cookie.Cookie.SecurePolicy.ShouldBe(expected);

        // The antiforgery cookie is the deliberate exception, and it is pinned here so nobody
        // "restores the symmetry" by hand. DefaultAntiforgery.CheckSSLConfig treats
        // Cookie.SecurePolicy == Always as an assertion that the origin itself observes HTTPS and
        // throws on every GetAndStoreTokens when it does not — which behind a TLS-terminating edge
        // means a 500 on the token endpoint and no sign-in at all. Its Secure flag comes from the
        // pipeline policy instead, and the wire tests below are what prove it arrives.
        antiforgery.Cookie.SecurePolicy.ShouldNotBe(
            CookieSecurePolicy.Always,
            "setting Always here would make the antiforgery token endpoint throw wherever the " +
            "origin cannot see HTTPS; UseLeaseBookCookiePolicy supplies the flag instead. This is " +
            "pinned as 'not Always' rather than an exact value so a benign framework default " +
            "change stays green while a reintroduced Always does not");
    }

    // ---------------------------------------------------------------------------------------------
    // Wire-level: what the browser actually receives.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>GET /api/auth/csrf</c> mints both antiforgery cookies. <c>LeaseBook.Antiforgery</c> comes
    /// from <see cref="AntiforgeryOptions"/>; <c>XSRF-TOKEN</c> is written by hand in
    /// <c>AuthEndpoints</c> so the SPA can read it, and is therefore invisible to the DI theory above.
    /// </summary>
    [Fact]
    public async Task Antiforgery_cookies_are_secure_in_production_over_plain_http()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = PlainHttpHost("Production");
        using var client = factory.CreateDefaultClient();

        var response = await client.GetAsync("/api/auth/csrf", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        IsSecure(SetCookie(response, "XSRF-TOKEN")).ShouldBeTrue(
            "the JS-readable XSRF-TOKEN cookie must carry Secure outside Development. It is written " +
            "by hand onto the response, so it is the DI theory's blind spot: " +
            SetCookie(response, "XSRF-TOKEN"));
        IsSecure(SetCookie(response, "LeaseBook.Antiforgery")).ShouldBeTrue(
            "the antiforgery cookie must carry Secure outside Development: " +
            SetCookie(response, "LeaseBook.Antiforgery"));
    }

    /// <summary>
    /// The other side of the same property: local development is plain <c>http://localhost</c>, so
    /// forcing Secure unconditionally would make every cookie undeliverable and the inner loop
    /// unusable. This one is expected to pass both before and after the fix — it is the
    /// over-correction guard, not the defect guard.
    /// </summary>
    [Fact]
    public async Task Antiforgery_cookies_are_not_forced_secure_in_development_over_plain_http()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = PlainHttpHost("Development");
        using var client = factory.CreateDefaultClient();

        var response = await client.GetAsync("/api/auth/csrf", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        IsSecure(SetCookie(response, "XSRF-TOKEN")).ShouldBeFalse(
            "http://localhost cannot deliver a Secure cookie; Development must stay SameAsRequest.");
        IsSecure(SetCookie(response, "LeaseBook.Antiforgery")).ShouldBeFalse(
            "http://localhost cannot deliver a Secure cookie; Development must stay SameAsRequest.");
    }

    /// <summary>The signed-in session cookie, asserted on the wire rather than out of DI.</summary>
    [Fact]
    public async Task Application_cookie_is_secure_in_production_over_plain_http()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = await CreateStaffUserAsync("cookie-app", ct);

        using var factory = PlainHttpHost("Production");
        using var client = factory.CreateDefaultClient();

        var response = await SignInAsync(client, email, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<LoginResponse>(ct))!.Status.ShouldBe(LoginStatus.Ok);

        IsSecure(SetCookie(response, "LeaseBook.Auth")).ShouldBeTrue(
            "the session cookie must carry Secure outside Development: " +
            SetCookie(response, "LeaseBook.Auth"));
    }

    /// <summary>
    /// <c>Identity.TwoFactorUserId</c> — the five-minute correlation cookie that identifies a user
    /// between the password step and the code step. Its scheme is registered by
    /// <c>AddIdentity</c> and nothing in this repository configures its <c>CookieBuilder</c>, so it
    /// carries whatever the framework default is. That makes it the cookie no options assertion in
    /// this file could ever reach: it is not the application scheme, and it is not antiforgery.
    /// </summary>
    [Fact]
    public async Task Two_factor_correlation_cookie_is_secure_in_production_over_plain_http()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = await CreateStaffUserAsync("cookie-2fa", ct);

        // Enrollment needs an authenticated round trip, which a Secure-cookie host cannot give a
        // plain-HTTP client. Do it against the default Development host; both share the database and
        // the Postgres-backed keyring (ADR-041), so the enrolled state is visible either side.
        using (var enrollmentClient = fixture.Api.CreateClient())
        {
            await enrollmentClient.PrimeCsrfAsync(ct);
            (await AuthTestSupport.LoginAsync(enrollmentClient, email, ct)).Status.ShouldBe(LoginStatus.Ok);
            await enrollmentClient.PrimeCsrfAsync(ct);
            await AuthTestSupport.EnrollMfaAsync(enrollmentClient, ct);
        }

        using var factory = PlainHttpHost("Production");
        using var client = factory.CreateDefaultClient();

        var response = await SignInAsync(client, email, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<LoginResponse>(ct))!.Status
            .ShouldBe(LoginStatus.MfaRequired, "the password step must stop at MFA for this user");

        IsSecure(SetCookie(response, "Identity.TwoFactorUserId")).ShouldBeTrue(
            "the partial-login cookie identifies a user mid-sign-in and must carry Secure outside " +
            "Development. Nothing configures this scheme's cookie, so it inherits the framework " +
            "default unless a pipeline-level policy covers it: " +
            SetCookie(response, "Identity.TwoFactorUserId"));
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The real host in a named environment, reachable over plain <c>http://localhost</c> — the shape
    /// a TLS-terminating edge presents to the origin. <c>AllowedHosts</c> must be set because
    /// <c>ProductionSecurityGuards</c> refuses to boot without it outside Development, and
    /// <c>Jobs:Enabled</c> because appsettings.Production.json turns the scheduler on.
    /// </summary>
    private WebApplicationFactory<Program> PlainHttpHost(string environment)
    {
        var factory = fixture.Api.WithWebHostBuilder(builder => builder
            .UseEnvironment(environment)
            .UseSetting("AllowedHosts", "localhost")
            .UseSetting("Jobs:Enabled", "false"));
        factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.ShouldBe(environment);
        return factory;
    }

    private async Task<string> CreateStaffUserAsync(string prefix, CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        var email = $"{prefix}-{orgId:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Cookie Policy Org", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Staff", Roles.PMStaff, ct);
        return email;
    }

    /// <summary>
    /// Posts the password step with the antiforgery pair carried by hand. A <c>CookieContainer</c>
    /// cannot be used here: it declines to echo a Secure cookie back over <c>http</c>, which is the
    /// very combination these tests exist to exercise, so the priming cookie would silently vanish
    /// and every sign-in would fail antiforgery instead of asserting anything.
    /// </summary>
    private static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client, string email, CancellationToken ct)
    {
        var primed = await client.GetAsync("/api/auth/csrf", ct);
        primed.EnsureSuccessStatusCode();
        var antiforgeryCookie = SetCookie(primed, "LeaseBook.Antiforgery");
        var token = Uri.UnescapeDataString(Value(SetCookie(primed, "XSRF-TOKEN")));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(email, AuthTestSupport.DefaultPassword)),
        };
        request.Headers.Add("Cookie", $"LeaseBook.Antiforgery={Value(antiforgeryCookie)}");
        request.Headers.Add("X-XSRF-TOKEN", token);
        return await client.SendAsync(request, ct);
    }

    /// <summary>The whole <c>Set-Cookie</c> header line for <paramref name="name"/>, attributes included.</summary>
    private static string SetCookie(HttpResponseMessage response, string name)
    {
        var headers = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];
        return Array.Find(headers, h => h.StartsWith(name + "=", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The response set no '{name}' cookie. Set-Cookie headers present: " +
                (headers.Length == 0 ? "(none)" : string.Join(" || ", headers)));
    }

    /// <summary>The cookie value, exactly as it sits on the wire (no unescaping).</summary>
    private static string Value(string setCookieHeader)
    {
        var afterName = setCookieHeader[(setCookieHeader.IndexOf('=', StringComparison.Ordinal) + 1)..];
        var end = afterName.IndexOf(';', StringComparison.Ordinal);
        return end >= 0 ? afterName[..end] : afterName;
    }

    /// <summary>
    /// True when the header carries the <c>secure</c> attribute. Attributes only — skipping the
    /// name=value pair matters, because a cookie <i>value</i> can contain the literal text and a
    /// substring match would call that a pass.
    /// </summary>
    private static bool IsSecure(string setCookieHeader) => setCookieHeader
        .Split(';')
        .Skip(1)
        .Any(attribute => attribute.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase));

    private sealed class StubEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "test";
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
