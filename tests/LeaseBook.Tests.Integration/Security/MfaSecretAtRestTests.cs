using System.Net.Http.Json;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

[Collection(nameof(DatabaseCollection))]
public sealed class MfaSecretAtRestTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Enrolled_authenticator_key_is_ciphertext_in_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        var email = $"secret-{orgId:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Secret Org", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Secret User", Roles.PMAdmin, ct);

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        await AuthTestSupport.LoginAsync(client, email, ct);
        await client.PrimeCsrfAsync(ct);
        var secret = await AuthTestSupport.EnrollMfaAsync(client, ct); // the plaintext base32 key

        // Read the raw column directly (bypassing EF/the converter) — it must NOT be the plaintext key.
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT value FROM asp_net_user_tokens WHERE name = 'AuthenticatorKey'", conn);
        var stored = (string?)await cmd.ExecuteScalarAsync(ct);

        stored.ShouldNotBeNull();
        stored.ShouldNotBe(secret); // encrypted at rest
        stored!.Length.ShouldBeGreaterThan(secret.Length);

        // And the app can still validate a fresh code (decrypt round-trip through the host).
        await client.PrimeCsrfAsync(ct);
        var confirmAgain = await client.PostAsJsonAsync(
            "/api/auth/mfa/enroll/confirm", new ConfirmMfaRequest(AuthTestSupport.ComputeTotp(secret)), ct);
        confirmAgain.EnsureSuccessStatusCode();
    }
}
