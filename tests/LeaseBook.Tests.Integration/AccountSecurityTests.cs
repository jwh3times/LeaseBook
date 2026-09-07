using System.Net;
using System.Net.Http.Json;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class AccountSecurityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Enrollment_issues_single_use_recovery_codes_and_refuses_reconfirmation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, email, client) = await CreateAsync(ct);
        var enroll = await client.PostAsync("/api/auth/mfa/enroll", null, ct);
        var secret = (await enroll.Content.ReadFromJsonAsync<EnrollResponse>(ct))!.Secret;
        var confirmation = await client.PostAsJsonAsync("/api/auth/mfa/enroll/confirm", new ConfirmMfaRequest(AuthTestSupport.ComputeTotp(secret)), ct);
        confirmation.StatusCode.ShouldBe(HttpStatusCode.OK);
        confirmation.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var codes = (await confirmation.Content.ReadFromJsonAsync<RecoveryCodesResponse>(ct))!.Codes;
        codes.Count.ShouldBe(10);
        codes.Distinct().Count().ShouldBe(10);
        var profile = (await client.GetFromJsonAsync<MeResponse>("/api/auth/me", ct))!;
        using var anonymous = fixture.Api.CreateClient();
        await anonymous.PrimeCsrfAsync(ct);
        (await anonymous.PostAsJsonAsync("/api/auth/mfa/recovery", new RecoveryLoginRequest(profile.UserId.ToString(), codes[0]), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/auth/mfa/enroll/confirm", new ConfirmMfaRequest(AuthTestSupport.ComputeTotp(secret)), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await client.PostAsync("/api/auth/logout", null, ct);
        await client.PrimeCsrfAsync(ct);
        var login = await AuthTestSupport.LoginAsync(client, email, ct);
        login.Status.ShouldBe(LoginStatus.MfaRequired);
        (await client.PostAsJsonAsync("/api/auth/mfa/recovery", new RecoveryLoginRequest(login.MfaToken!, codes[0]), ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct);
        await client.PostAsync("/api/auth/logout", null, ct);
        await client.PrimeCsrfAsync(ct);
        login = await AuthTestSupport.LoginAsync(client, email, ct);
        (await client.PostAsJsonAsync("/api/auth/mfa/recovery", new RecoveryLoginRequest(login.MfaToken!, codes[0]), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await AssertAuditAsync(orgId, "mfa-enrolled", "user", null, ct);
    }

    [Fact]
    public async Task Password_change_requires_current_password_and_revokes_other_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, email, client) = await CreateAsync(ct);
        using var other = fixture.Api.CreateClient();
        await other.PrimeCsrfAsync(ct);
        await AuthTestSupport.LoginAsync(other, email, ct);
        const string nextPassword = "Changed-Password-2026!";
        (await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("wrong", nextPassword), ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await other.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(AuthTestSupport.DefaultPassword, nextPassword), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await other.PrimeCsrfAsync(ct);
        (await other.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthTestSupport.DefaultPassword), ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await other.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, nextPassword), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await AssertAuditAsync(orgId, "password-changed", "user", null, ct);
    }

    [Fact]
    public async Task Operator_reset_is_org_scoped_audited_and_revokes_sessions_and_codes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, email, client) = await CreateAsync(ct);
        var originalKey = await AuthTestSupport.EnrollMfaAsync(client, ct);
        await using (var wrong = fixture.Api.Services.CreateAsyncScope())
        {
            await Should.ThrowAsync<InvalidOperationException>(() => wrong.ServiceProvider.GetRequiredService<AccountAdministration>()
                .ResetMfaAsync(UuidV7.NewId(), email, ct));
        }
        (await client.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AccountAdministration>().ResetMfaAsync(orgId, email, ct);
        }
        (await client.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var check = fixture.Api.Services.CreateAsyncScope();
        var users = check.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        user.TwoFactorEnabled.ShouldBeFalse();
        (await users.GetAuthenticatorKeyAsync(user)).ShouldNotBe(originalKey);
        (await users.CountRecoveryCodesAsync(user)).ShouldBe(0);
        await AssertAuditAsync(orgId, "mfa-reset", "system", "accounts:reset-mfa", ct);
    }

    [Fact]
    public async Task Operator_provisioning_creates_real_admin_and_rolls_back_invalid_password()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"provision-{UuidV7.NewId():N}@example.com";
        Guid orgId;
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            orgId = await scope.ServiceProvider.GetRequiredService<AccountAdministration>()
                .CreateAdminAsync("New organization", email, "Administrator", AuthTestSupport.DefaultPassword, ct);
        }
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            user.OrgId.ShouldBe(orgId);
            user.TwoFactorEnabled.ShouldBeFalse();
            (await users.IsInRoleAsync(user, Roles.PMAdmin)).ShouldBeTrue();
        }
        var rejectedOrgName = $"rejected-{UuidV7.NewId()}";
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            await Should.ThrowAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<AccountAdministration>()
                .CreateAdminAsync(rejectedOrgName, $"new-{email}", "Admin", "weak", ct));
        }
        await using var db = fixture.CreateContext(fixture.MigratorConnectionString);
        (await db.Orgs.AnyAsync(o => o.Name == rejectedOrgName, ct)).ShouldBeFalse();
        await AssertAuditAsync(orgId, "admin-created", "system", "accounts:create-admin", ct);
    }

    [Fact]
    public async Task Invalid_recovery_codes_lock_out_the_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, email, client) = await CreateAsync(ct);
        await AuthTestSupport.EnrollMfaAsync(client, ct);
        await client.PrimeCsrfAsync(ct);
        await client.PostAsync("/api/auth/logout", null, ct);
        await client.PrimeCsrfAsync(ct);
        var login = await AuthTestSupport.LoginAsync(client, email, ct);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await client.PostAsJsonAsync("/api/auth/mfa/recovery", new RecoveryLoginRequest(login.MfaToken!, "invalid"), ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        (await users.IsLockedOutAsync((await users.FindByEmailAsync(email))!)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("accounts", "reset-mfa", "--org", "not-an-id", "--email", "a@example.com", "--identity-verified", "yes")]
    [InlineData("accounts", "reset-mfa", "--org", "11111111-1111-1111-1111-111111111111", "--email", "a@example.com", "--identity-verified", "no")]
    [InlineData("accounts", "create-admin", "--org-name", "Org", "--email", "a@example.com", "--password", "must-not-be-an-argument")]
    public void Operator_command_rejects_invalid_scope_or_password_arguments(params string[] args)
    {
        var resolution = LeaseBook.Web.Cli.CliApplication.Resolve(args);
        resolution.Error.ShouldNotBeNull();
        resolution.Invocation.ShouldBeNull();
    }

    private async Task<(Guid OrgId, string Email, HttpClient Client)> CreateAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        var email = $"security-{orgId:N}@example.com";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Account security", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Administrator", Roles.PMAdmin, ct);
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        await AuthTestSupport.LoginAsync(client, email, ct);
        await client.PrimeCsrfAsync(ct);
        return (orgId, email, client);
    }

    private async Task AssertAuditAsync(Guid orgId, string action, string kind, string? process, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
        {
            var row = await db.AuditEvents.SingleAsync(a => a.EntityType == "account-security" && a.Action == action, ct);
            row.ActorKind.ShouldBe(kind); row.ActorProcess.ShouldBe(process);
            row.OccurredAt.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-5));
            row.Before.ShouldBeNull(); row.After.ShouldBeNull();
        }, ct);
    }
}
