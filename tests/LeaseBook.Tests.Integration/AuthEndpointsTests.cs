using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Auth flows against the real host (§C.6): password login, MFA enrollment + TOTP login, lockout,
/// and the WP-05 + WP-06 tie — an authenticated, org-scoped read returns only the caller's data.
/// All POSTs go through the cookie-to-header XSRF check, so each client primes a token first.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AuthEndpointsTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";

    /// <summary>
    /// #357. An expired or absent cookie is the error a signed-in operator meets most often, and it
    /// used to be the one error response carrying neither a code nor a correlationId: the cookie
    /// handler wrote a bare status and there is no UseStatusCodePages to dress it. ErrorContractTests
    /// cannot see this gap — it scans for direct Results.Problem calls, and this path never made one.
    /// </summary>
    [Fact]
    public async Task Unauthenticated_api_request_carries_the_error_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Api.CreateClient();

        var response = await client.GetAsync("/api/accounting/banks/balances", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe("not_authenticated");
        // The W3C trace id the operator quotes and App Insights indexes as operation_Id.
        problem.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Health_is_anonymous_and_reports_ok()
    {
        var client = fixture.Api.CreateClient();
        var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var health = await response.Content.ReadFromJsonAsync<HealthResponse>(TestContext.Current.CancellationToken);
        health!.Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Login_sets_cookie_and_me_returns_the_profile()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await CreateOrgAsync(orgId, "Tarheel Property Group", ct);
        var email = $"admin-{orgId:N}@example.com";
        await CreateUserAsync(orgId, email, "Renée Calloway", ct);

        var client = fixture.Api.CreateClient();
        await PrimeCsrfAsync(client, ct);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<LoginResponse>(ct))!.Status.ShouldBe(LoginStatus.Ok);

        var me = await client.GetAsync("/api/auth/me", ct);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var profile = await me.Content.ReadFromJsonAsync<MeResponse>(ct);
        profile!.Email.ShouldBe(email);
        profile.OrgId.ShouldBe(orgId);
        profile.OrgName.ShouldBe("Tarheel Property Group");
        profile.Role.ShouldBe(Roles.PMAdmin);
    }

    [Fact]
    public async Task Me_without_a_cookie_is_401()
    {
        var client = fixture.Api.CreateClient();
        var response = await client.GetAsync("/api/auth/me", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_password_returns_problem_and_locks_out_after_the_threshold()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await CreateOrgAsync(orgId, "Lockout Org", ct);
        var email = $"lock-{orgId:N}@example.com";
        await CreateUserAsync(orgId, email, "Lock Test", ct);

        var client = fixture.Api.CreateClient();
        await PrimeCsrfAsync(client, ct);

        // Five failures trip the lockout (MaxFailedAccessAttempts = 5).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var bad = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password"), ct);
            bad.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            bad.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        }

        // Now even the correct password is refused — proves the account is locked, not just wrong creds.
        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        locked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// #360. `GetTwoFactorAuthenticationUserAsync()` returns null when the partial cookie from the
    /// password step is gone, which is what a user who took too long over their authenticator app
    /// produces. That answered `invalid_credentials` — telling someone whose sign-in timed out that
    /// their credentials were wrong, and pointing them at the wrong next action.
    ///
    /// A client that never did the password step has no partial cookie, which is the same server
    /// state an expired one leaves behind.
    /// </summary>
    [Fact]
    public async Task An_expired_two_factor_session_is_a_timed_out_attempt_not_a_rejected_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Api.CreateClient();
        await PrimeCsrfAsync(client, ct);

        var response = await client.PostAsJsonAsync(
            "/api/auth/mfa", new MfaRequest(UuidV7.NewId().ToString(), "123456"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var code = body.RootElement.GetProperty("code").GetString();
        code.ShouldBe("mfa_session_expired");
        code.ShouldNotBe("invalid_credentials");
        // Not a credential judgement, so there is nothing to withhold: the reason and the reference
        // both travel, which is what lets the login page show a support reference here (ADR-025).
        body.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("timed out");
    }

    /// <summary>
    /// #360. The same split on the recovery-code path, which collapses one more condition than
    /// POST /api/auth/mfa does — a lockout — and must keep collapsing that one.
    /// </summary>
    [Fact]
    public async Task An_expired_two_factor_session_is_a_timed_out_attempt_on_the_recovery_path_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Api.CreateClient();
        await PrimeCsrfAsync(client, ct);

        var response = await client.PostAsJsonAsync(
            "/api/auth/mfa/recovery", new RecoveryLoginRequest(UuidV7.NewId().ToString(), "abcd-efgh"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        body.RootElement.GetProperty("code").GetString().ShouldBe("mfa_session_expired");
    }

    /// <summary>
    /// #360's load-bearing property, and the one most at risk from giving the timed-out attempt its
    /// own code: on the password step, a wrong password, an unknown email and a locked-out account
    /// must stay byte-identical. Login never reveals whether an email exists, so any difference here
    /// — status, code, or detail — is an account-enumeration oracle.
    /// </summary>
    [Fact]
    public async Task A_rejected_credential_never_reveals_which_credential_it_was()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await CreateOrgAsync(orgId, "Enumeration Org", ct);
        var email = $"enum-{orgId:N}@example.com";
        await CreateUserAsync(orgId, email, "Enum Test", ct);

        var client = fixture.Api.CreateClient();
        await PrimeCsrfAsync(client, ct);

        var wrongPassword = await Shape(client, email, "wrong-password", ct);
        var unknownEmail = await Shape(client, $"nobody-{UuidV7.NewId():N}@example.com", Password, ct);

        // Trip the lockout (MaxFailedAccessAttempts = 5), then present the *correct* password: a
        // locked account is a third distinct server state that must look like the other two.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password"), ct);
        }

        var lockedOut = await Shape(client, email, Password, ct);

        unknownEmail.ShouldBe(wrongPassword);
        lockedOut.ShouldBe(wrongPassword);
    }

    /// <summary>
    /// Status + code + detail of a login rejection — the whole response body a caller can read.
    /// Deliberately not "everything a caller could compare": it does not pin headers, and it does
    /// not pin timing, which is a separate channel tracked privately.
    /// </summary>
    private static async Task<string> Shape(
        HttpClient client, string email, string password, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var code = body.RootElement.GetProperty("code").GetString();
        var detail = body.RootElement.GetProperty("detail").GetString();
        return $"{(int)response.StatusCode}|{code}|{detail}";
    }

    [Fact]
    public async Task Mfa_enroll_then_confirm_then_login_with_a_totp_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await CreateOrgAsync(orgId, "MFA Org", ct);
        var email = $"mfa-{orgId:N}@example.com";
        await CreateUserAsync(orgId, email, "MFA User", ct);

        var client = fixture.Api.CreateClient();
        await PrimeCsrfAsync(client, ct);

        // Password login (no MFA yet) → ok.
        (await Login(client, email, ct)).Status.ShouldBe(LoginStatus.Ok);

        // Antiforgery tokens bind to the authenticated user, so refresh after the auth state changes.
        await PrimeCsrfAsync(client, ct);

        // A second session retains its pre-enrollment claims after the first session confirms.
        using var staleClient = fixture.Api.CreateClient();
        await PrimeCsrfAsync(staleClient, ct);
        (await Login(staleClient, email, ct)).Status.ShouldBe(LoginStatus.Ok);
        await PrimeCsrfAsync(staleClient, ct);

        // Enroll: get a secret, compute the current code via Identity, confirm it.
        var enroll = await client.PostAsync("/api/auth/mfa/enroll", content: null, ct);
        enroll.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secret = (await enroll.Content.ReadFromJsonAsync<EnrollResponse>(ct))!;
        secret.OtpauthUri.ShouldContain("otpauth://totp/");

        var confirm = await client.PostAsJsonAsync(
            "/api/auth/mfa/enroll/confirm", new ConfirmMfaRequest(ComputeTotp(secret.Secret)), ct);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Enrollment is for first-time setup. Repeating it must neither disclose nor replace
        // the active authenticator; the login below proves the original one still works.
        var repeatedEnrollment = await client.PostAsync("/api/auth/mfa/enroll", content: null, ct);
        repeatedEnrollment.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var enrollmentProblem = await repeatedEnrollment.Content.ReadAsStringAsync(ct);
        enrollmentProblem.ShouldContain("mfa_already_enrolled");
        enrollmentProblem.ShouldNotContain(secret.Secret);
        enrollmentProblem.ShouldNotContain("otpauth://");

        (await staleClient.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await PrimeCsrfAsync(staleClient, ct);
        var staleEnrollment = await staleClient.PostAsync("/api/auth/mfa/enroll", content: null, ct);
        staleEnrollment.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var staleProblem = await staleEnrollment.Content.ReadAsStringAsync(ct);
        staleProblem.ShouldNotContain(secret.Secret);

        await client.PostAsync("/api/auth/logout", content: null, ct);
        await PrimeCsrfAsync(client, ct); // back to anonymous → refresh again

        // Re-login now requires the second factor.
        var second = await Login(client, email, ct);
        second.Status.ShouldBe(LoginStatus.MfaRequired);
        second.MfaToken.ShouldNotBeNull();

        var mfa = await client.PostAsJsonAsync(
            "/api/auth/mfa", new MfaRequest(second.MfaToken!, ComputeTotp(secret.Secret)), ct);
        mfa.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await mfa.Content.ReadFromJsonAsync<LoginResponse>(ct))!.Status.ShouldBe(LoginStatus.Ok);

        (await client.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Authenticated_org_scoped_read_returns_only_the_callers_org_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var orgB = UuidV7.NewId();
        await CreateOrgAsync(orgA, "Org A", ct);
        await CreateOrgAsync(orgB, "Org B", ct);
        var emailA = $"a-{orgA:N}@example.com";
        var emailB = $"b-{orgB:N}@example.com";
        await CreateUserAsync(orgA, emailA, "User A", ct);
        await CreateUserAsync(orgB, emailB, "User B", ct);

        await ProvisionTrustBankAsync(orgA, "Trust A", ct);
        await ProvisionTrustBankAsync(orgB, "Trust B", ct);

        // Each authenticated user hits the real accounting read surface and sees only its org's bank
        // (cookie → org claim → org-context middleware → RLS), now against a real endpoint.
        (await BankNamesFor(emailA, ct)).ShouldBe(["Trust A"]);
        (await BankNamesFor(emailB, ct)).ShouldBe(["Trust B"]);
    }

    private async Task<IReadOnlyList<string>> BankNamesFor(string email, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await PrimeCsrfAsync(client, ct);
        await Login(client, email, ct);

        var response = await client.GetAsync("/api/accounting/banks/balances", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<BankBalancesResponse>(ct);
        return payload!.Rows.Select(r => r.Name).ToList();
    }

    private async Task ProvisionTrustBankAsync(Guid orgId, string bankName, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var chartOfAccounts = scope.ServiceProvider.GetRequiredService<IChartOfAccounts>();
        await executor.RunAsSystemAsync(orgId, "test-harness",
            () => chartOfAccounts.ProvisionAsync([new BankAccountSpec(UuidV7.NewId(), bankName, BankPurpose.Trust)], ct),
            ct);
    }

    private async Task<LoginResponse> Login(HttpClient client, string email, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(ct))!;
    }

    private async Task CreateOrgAsync(Guid orgId, string name, CancellationToken ct)
    {
        await using var db = fixture.CreateContext(fixture.MigratorConnectionString);
        db.Orgs.Add(new OrgEntity { Id = orgId, Name = name });
        await db.SaveChangesAsync(ct);
    }

    private async Task CreateUserAsync(Guid orgId, string email, string displayName, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            Id = UuidV7.NewId(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            OrgId = orgId,
            DisplayName = displayName,
        };
        var created = await userManager.CreateAsync(user, Password);
        created.Succeeded.ShouldBeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));
        (await userManager.AddToRoleAsync(user, Roles.PMAdmin)).Succeeded.ShouldBeTrue();
    }

    /// <summary>
    /// Computes the current RFC 6238 TOTP the way Identity's authenticator verifier does — the
    /// authenticator token provider deliberately cannot generate codes, so the test plays the role of
    /// the authenticator app: base32-decode the shared secret, HMAC-SHA1 over the 30s timestep.
    /// </summary>
    private static string ComputeTotp(string base32Secret)
    {
        var key = Base32Decode(base32Secret);
        var timestep = (long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30L);

        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timestep);

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                     | ((hash[offset + 1] & 0xff) << 16)
                     | ((hash[offset + 2] & 0xff) << 8)
                     | (hash[offset + 3] & 0xff);

        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();

        var output = new List<byte>(input.Length * 5 / 8);
        var bits = 0;
        var value = 0;
        foreach (var c in input)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0)
            {
                continue;
            }

            value = (value << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return [.. output];
    }

    private static async Task PrimeCsrfAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync("/api/auth/csrf", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var token = ExtractCookie(response, "XSRF-TOKEN")
            ?? throw new InvalidOperationException("CSRF endpoint did not set the XSRF-TOKEN cookie.");
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
    }

    private static string? ExtractCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return null;
        }

        var prefix = name + "=";
        foreach (var cookie in setCookies)
        {
            if (cookie.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = cookie[prefix.Length..];
                var end = value.IndexOf(';');
                return Uri.UnescapeDataString(end >= 0 ? value[..end] : value);
            }
        }

        return null;
    }
}
