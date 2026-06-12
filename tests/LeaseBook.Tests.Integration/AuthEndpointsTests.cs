using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LeaseBook.SharedKernel;
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

        // Enroll: get a secret, compute the current code via Identity, confirm it.
        var enroll = await client.PostAsync("/api/auth/mfa/enroll", content: null, ct);
        enroll.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secret = (await enroll.Content.ReadFromJsonAsync<EnrollResponse>(ct))!;
        secret.OtpauthUri.ShouldContain("otpauth://totp/");

        var confirm = await client.PostAsJsonAsync(
            "/api/auth/mfa/enroll/confirm", new ConfirmMfaRequest(ComputeTotp(secret.Secret)), ct);
        confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);

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

        await SeedAuditEventsAsync(orgA, 2, ct);
        await SeedAuditEventsAsync(orgB, 1, ct);

        (await AuditCountFor(emailA, ct)).ShouldBe(2);
        (await AuditCountFor(emailB, ct)).ShouldBe(1);
    }

    private async Task<int> AuditCountFor(string email, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await PrimeCsrfAsync(client, ct);
        await Login(client, email, ct);

        var response = await client.GetAsync("/api/diagnostics/audit-count", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuditCountResponse>(ct))!.Count;
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

    private async Task SeedAuditEventsAsync(Guid orgId, int count, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);
        for (var i = 0; i < count; i++)
        {
            await RlsProbe.InsertEventAsync(conn, tx, orgId, ct);
        }

        await tx.CommitAsync(ct);
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
