using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Common;

/// <summary>Shared auth-flow helpers for the integration suites (org/user creation, login,
/// MFA enrollment, RFC 6238 TOTP). Extracted from AuthEndpointsTests so WP-5's security tests reuse it.
/// CSRF priming/cookie extraction lives in <see cref="ApiClientExtensions"/> — use the
/// <c>client.PrimeCsrfAsync(ct)</c> extension form.</summary>
public static class AuthTestSupport
{
    public const string DefaultPassword = "Tarheel-Trust-2026!";

    public static async Task CreateOrgAsync(PostgresFixture fixture, Guid orgId, string name, CancellationToken ct)
    {
        await using var db = fixture.CreateContext(fixture.MigratorConnectionString);
        db.Orgs.Add(new OrgEntity { Id = orgId, Name = name });
        await db.SaveChangesAsync(ct);
    }

    public static async Task CreateUserAsync(
        PostgresFixture fixture, Guid orgId, string email, string displayName, string role, CancellationToken ct)
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
        var created = await userManager.CreateAsync(user, DefaultPassword);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        var roled = await userManager.AddToRoleAsync(user, role);
        if (!roled.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", roled.Errors.Select(e => e.Description)));
        }
    }

    public static async Task<LoginResponse> LoginAsync(HttpClient client, string email, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, DefaultPassword), ct);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(ct))!;
    }

    /// <summary>Enrolls the currently-authenticated client in TOTP MFA and returns the base32 secret.
    /// Caller must have logged in and primed CSRF first.</summary>
    public static async Task<string> EnrollMfaAsync(HttpClient client, CancellationToken ct)
    {
        var enroll = await client.PostAsync("/api/auth/mfa/enroll", content: null, ct);
        var secret = (await enroll.Content.ReadFromJsonAsync<EnrollResponse>(ct))!.Secret;
        var confirm = await client.PostAsJsonAsync(
            "/api/auth/mfa/enroll/confirm", new ConfirmMfaRequest(ComputeTotp(secret)), ct);
        confirm.EnsureSuccessStatusCode();
        return secret;
    }

    public static string ComputeTotp(string base32Secret)
    {
        var key = Base32Decode(base32Secret);
        var timestep = (long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30L);
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timestep);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16)
                     | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(input.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (var c in input)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0) { continue; }
            value = (value << 5) | index;
            bits += 5;
            if (bits >= 8) { output.Add((byte)((value >> (bits - 8)) & 0xff)); bits -= 8; }
        }

        return [.. output];
    }
}
