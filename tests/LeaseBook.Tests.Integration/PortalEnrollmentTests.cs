using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class PortalEnrollmentTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Non_development_hosts_refuse_enrollment_management_and_local_delivery(string environment)
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, developmentManager, target) = await Setup("tenant", ct);
        using (developmentManager)
        {
            var email = $"nondev-{UuidV7.NewId():N}@example.test";
            var created = await developmentManager.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct);
            created.EnsureSuccessStatusCode();
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
            var token = await Token(developmentManager, email, ct);
            await using var host = fixture.Api.WithWebHostBuilder(builder => builder.UseEnvironment(environment)
                .UseSetting("AllowedHosts", "localhost").UseSetting("Jobs:Enabled", "false"));
            host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.ShouldBe(environment);
            host.Services.GetServices<IHostedService>().ShouldNotContain(service => service is PortalInvitationWorker);
            using var manager = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            await manager.PrimeCsrfAsync(ct);
            (await AuthTestSupport.LoginAsync(manager, $"admin-{orgId:N}@isolated.test", ct)).Status.ShouldBe("ok");
            await manager.PrimeCsrfAsync(ct);
            var access = await manager.GetFromJsonAsync<JsonElement>($"/api/portal-access/tenant/{target}", ct);
            access.GetProperty("canManage").GetBoolean().ShouldBeFalse();
            var responses = new List<HttpResponseMessage>
            {
                await manager.GetAsync("/api/portal-access/test-inbox", ct),
                await manager.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct),
                await manager.PostAsync($"/api/portal-access/invitations/{id}/replace", null, ct),
                await manager.PostAsync($"/api/portal-access/invitations/{id}/cancel", null, ct),
                await manager.PostAsync($"/api/portal-access/invitations/{id}/retry", null, ct),
                await manager.PostAsync($"/api/portal-access/tenant/{target}/users/{UuidV7.NewId()}/revoke", null, ct),
            };
            foreach (var response in responses)
            {
                using (response)
                {
                    response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                    var content = await response.Content.ReadAsStringAsync(ct);
                    content.ShouldNotContain(token);
                    content.ShouldNotContain(email);
                    content.ShouldNotContain("acceptUrl");
                }
            }
            using var recipient = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            await recipient.PrimeCsrfAsync(ct);
            foreach (var endpoint in new[] { "inspect", "accept" })
            {
                using var response = await recipient.PostAsJsonAsync("/api/portal-enrollment/" + endpoint, new { token }, ct);
                response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                response.Headers.CacheControl!.NoStore.ShouldBeTrue();
                var content = await response.Content.ReadAsStringAsync(ct);
                content.ShouldNotContain(token);
                content.ShouldNotContain(email);
                JsonDocument.Parse(content).RootElement.GetProperty("code").GetString().ShouldBe("invalid_invitation");
            }
            var local = host.Services.GetRequiredService<IPortalInvitationDelivery>();
            await Should.ThrowAsync<InvalidOperationException>(() => local.DeliverAsync(orgId, new(id, email, "https://localhost/portal/enroll#token=" + token), ct));
            await Should.ThrowAsync<InvalidOperationException>(() => local.ReadAsync(orgId, id, ct));
            var preserved = await developmentManager.GetFromJsonAsync<JsonElement>($"/api/portal-access/tenant/{target}", ct);
            preserved.GetProperty("invitations")[0].GetProperty("status").GetString().ShouldBe("pending");
        }
    }

    [Fact]
    public async Task Admin_policy_is_rechecked_on_every_management_mutation_and_inbox_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, staff, target) = await Setup("tenant", ct);
        using (staff)
        using (var admin = fixture.Api.CreateClient())
        {
            var adminEmail = $"policy-admin-{UuidV7.NewId():N}@example.test";
            await AuthTestSupport.CreateUserAsync(fixture, orgId, adminEmail, "Admin", Roles.PMAdmin, ct);
            await admin.PrimeCsrfAsync(ct);
            await AuthTestSupport.LoginAsync(admin, adminEmail, ct);
            await admin.PrimeCsrfAsync(ct);
            var email = $"policy-recipient-{UuidV7.NewId():N}@example.test";
            var invite = await staff.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct);
            var id = (await invite.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
            (await admin.PutAsJsonAsync("/api/settings/org", new { staffCanManagePortalAccess = false }, ct)).EnsureSuccessStatusCode();
            var access = await staff.GetFromJsonAsync<JsonElement>($"/api/portal-access/tenant/{target}", ct);
            access.GetProperty("canManage").GetBoolean().ShouldBeFalse();
            (await staff.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            foreach (var operation in new[] { "cancel", "replace", "retry" })
            { (await staff.PostAsync($"/api/portal-access/invitations/{id}/{operation}", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); }
            (await staff.PostAsync($"/api/portal-access/tenant/{target}/users/{UuidV7.NewId()}/revoke", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await staff.GetAsync("/api/portal-access/test-inbox", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await admin.PostAsync($"/api/portal-access/invitations/{id}/cancel", null, ct)).EnsureSuccessStatusCode();
            (await admin.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Foreign_and_system_targets_and_portal_managers_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, manager, target) = await Setup("tenant", ct);
        var (_, foreign, _) = await Setup("tenant", ct);
        using (manager)
        using (foreign)
        {
            var body = new { email = $"blocked-{UuidV7.NewId():N}@example.test" };
            (await foreign.PostAsJsonAsync($"/api/portal-access/tenant/{target}", body, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            await using var scope = fixture.Api.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "test:system-target", async () =>
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (await db.Set<Tenant>().SingleAsync(t => t.Id == target, ct)).IsSystem = true;
                await db.SaveChangesAsync(ct);
            }, ct);
            (await manager.PostAsJsonAsync($"/api/portal-access/tenant/{target}", body, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            foreach (var role in new[] { Roles.Tenant, Roles.Owner })
            {
                var (_, portal) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, role, ct);
                using (portal)
                { (await portal.PostAsJsonAsync($"/api/portal-access/tenant/{target}", body, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); }
            }
        }
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("opposite")]
    [InlineData("mixed")]
    [InlineData("foreign")]
    [InlineData("history")]
    public async Task Incompatible_existing_accounts_cannot_be_converted_or_rebound(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, manager, target) = await Setup("tenant", ct);
        using (manager)
        using (var recipient = fixture.Api.CreateClient())
        {
            var email = $"conflict-{UuidV7.NewId():N}@example.test";
            var accountOrg = kind == "foreign" ? UuidV7.NewId() : orgId;
            if (accountOrg != orgId) { await AuthTestSupport.CreateOrgAsync(fixture, accountOrg, "Other org", ct); }
            await AuthTestSupport.CreateUserAsync(fixture, accountOrg, email, "Existing account",
                kind == "staff" ? Roles.PMStaff : kind == "opposite" ? Roles.Owner : Roles.Tenant, ct);
            await using var scope = fixture.Api.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            if (kind == "mixed") { (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.ShouldBeTrue(); }
            if (kind == "history")
            {
                await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "test:history", async () =>
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var original = new Tenant { Id = UuidV7.NewId(), DisplayName = "Original tenant" };
                    db.Add(original);
                    await db.SaveChangesAsync(ct);
                    await scope.ServiceProvider.GetRequiredService<ResidentAccessService>().GrantAsync(user.Id, original.Id, ct);
                    await scope.ServiceProvider.GetRequiredService<ResidentAccessService>().RevokeAsync(user.Id, ct);
                }, ct);
            }
            (await manager.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct)).EnsureSuccessStatusCode();
            var token = await Token(manager, email, ct);
            await recipient.PrimeCsrfAsync(ct);
            foreach (var endpoint in new[] { "inspect", "accept" })
            {
                var response = await recipient.PostAsJsonAsync("/api/portal-enrollment/" + endpoint, new { token }, ct);
                response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                error.GetProperty("code").GetString().ShouldBe("invalid_invitation");
                error.GetProperty("detail").GetString().ShouldBe("This invitation is unavailable or has expired.");
            }
        }
    }

    [Fact]
    public async Task Existing_account_must_sign_in_and_keeps_its_password_and_MFA()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, manager, target) = await Setup("tenant", ct);
        using (manager)
        using (var recipient = fixture.Api.CreateClient())
        {
            var email = $"existing-{UuidV7.NewId():N}@example.test";
            await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Original name", Roles.Tenant, ct);
            await recipient.PrimeCsrfAsync(ct);
            (await AuthTestSupport.LoginAsync(recipient, email, ct)).Status.ShouldBe("ok");
            await recipient.PrimeCsrfAsync(ct);
            var secret = await AuthTestSupport.EnrollMfaAsync(recipient, ct);
            await recipient.PrimeCsrfAsync(ct);
            (await recipient.PostAsync("/api/auth/logout", null, ct)).EnsureSuccessStatusCode();
            await recipient.PrimeCsrfAsync(ct);
            (await manager.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct)).EnsureSuccessStatusCode();
            var token = await Token(manager, email, ct);
            var inspection = await recipient.PostAsJsonAsync("/api/portal-enrollment/inspect", new { token }, ct);
            (await inspection.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("requiresSignIn").GetBoolean().ShouldBeTrue();
            (await recipient.PostAsJsonAsync("/api/portal-enrollment/accept", new { token }, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var challenge = await AuthTestSupport.LoginAsync(recipient, email, ct);
            challenge.Status.ShouldBe("mfa-required");
            (await recipient.PostAsJsonAsync("/api/portal-enrollment/accept", new { token }, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await recipient.PostAsJsonAsync("/api/auth/mfa", new MfaRequest(challenge.MfaToken!, AuthTestSupport.ComputeTotp(secret)), ct)).EnsureSuccessStatusCode();
            await recipient.PrimeCsrfAsync(ct);
            (await recipient.PostAsJsonAsync("/api/portal-enrollment/accept", new { token, password = "Ignored-Password-999!", displayName = "Ignored name" }, ct)).EnsureSuccessStatusCode();
            (await recipient.GetAsync("/api/portal/tenant/ledger", ct)).EnsureSuccessStatusCode();
            await using var scope = fixture.Api.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            (await users.CheckPasswordAsync(user, AuthTestSupport.DefaultPassword)).ShouldBeTrue();
            user.TwoFactorEnabled.ShouldBeTrue();
            user.DisplayName.ShouldBe("Original name");
        }
    }

    [Fact]
    public async Task Concurrent_acceptance_has_exactly_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, manager, target) = await Setup("tenant", ct);
        using (manager)
        using (var a = fixture.Api.CreateClient())
        using (var b = fixture.Api.CreateClient())
        {
            var email = $"race-{UuidV7.NewId():N}@example.test";
            (await manager.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct)).EnsureSuccessStatusCode();
            var token = await Token(manager, email, ct);
            await a.PrimeCsrfAsync(ct);
            await b.PrimeCsrfAsync(ct);
            var body = new { token, displayName = "Recipient", password = AuthTestSupport.DefaultPassword };
            var results = await Task.WhenAll(a.PostAsJsonAsync("/api/portal-enrollment/accept", body, ct), b.PostAsJsonAsync("/api/portal-enrollment/accept", body, ct));
            results.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_link_failure_rolls_back_credentials_and_leaves_the_invitation_redeemable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, manager, target) = await Setup("tenant", ct);
        using (manager)
        {
            var email = $"rollback-{UuidV7.NewId():N}@example.test";
            (await manager.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct)).EnsureSuccessStatusCode();
            var token = await Token(manager, email, ct);
            await using var failing = fixture.Api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new RejectLink(target)))));
            using var recipient = failing.CreateClient();
            await recipient.PrimeCsrfAsync(ct);
            var body = new { token, displayName = "Recipient", password = AuthTestSupport.DefaultPassword };
            (await recipient.PostAsJsonAsync("/api/portal-enrollment/accept", body, ct)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            using var retry = fixture.Api.CreateClient();
            await retry.PrimeCsrfAsync(ct);
            var inspection = await retry.PostAsJsonAsync("/api/portal-enrollment/inspect", new { token }, ct);
            (await inspection.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("requiresPassword").GetBoolean().ShouldBeTrue();
            (await retry.PostAsJsonAsync("/api/portal-enrollment/accept", body, ct)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Expired_tampered_and_cancelled_proof_have_the_same_generic_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, manager, target) = await Setup("tenant", ct);
        using (manager)
        {
            var email = $"expiry-{UuidV7.NewId():N}@example.test";
            var invite = await manager.PostAsJsonAsync($"/api/portal-access/tenant/{target}", new { email }, ct);
            var id = (await invite.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
            var token = await Token(manager, email, ct);
            await using var expired = fixture.Api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new FutureClock()); }));
            using var expiredClient = expired.CreateClient();
            await expiredClient.PrimeCsrfAsync(ct);
            var expiry = await expiredClient.PostAsJsonAsync("/api/portal-enrollment/inspect", new { token }, ct);
            expiry.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await expiry.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("invalid_invitation");
            using var anonymous = fixture.Api.CreateClient();
            await anonymous.PrimeCsrfAsync(ct);
            foreach (var bad in new[] { token + "tamper", "not-a-proof" })
            {
                var invalid = await anonymous.PostAsJsonAsync("/api/portal-enrollment/inspect", new { token = bad }, ct);
                invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                (await invalid.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("invalid_invitation");
            }
            (await manager.PostAsync($"/api/portal-access/invitations/{id}/cancel", null, ct)).EnsureSuccessStatusCode();
            var cancelled = await anonymous.PostAsJsonAsync("/api/portal-enrollment/inspect", new { token }, ct);
            (await cancelled.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("invalid_invitation");
        }
    }

    private sealed class FutureClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddHours(73); }

    private sealed class RejectLink(Guid target) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ResidentAccess>().Any(e => e.State == EntityState.Added && e.Entity.TenantId == target))
            { throw new InvalidOperationException("Deliberate link-storage failure."); }
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("tenant", "/api/portal/tenant/ledger")]
    [InlineData("owner", "/api/portal/owner/summary")]
    public async Task Replacement_cancellation_and_revocation_invalidate_proof_and_live_access(string persona, string portal)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, manager, target) = await Setup(persona, ct);
        using (manager)
        using (var recipient = fixture.Api.CreateClient())
        {
            var email = $"lifecycle-{UuidV7.NewId():N}@example.test";
            var response = await manager.PostAsJsonAsync($"/api/portal-access/{persona}/{target}", new { email }, ct);
            var first = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
            var oldToken = await Token(manager, email, ct);
            var replaced = await manager.PostAsync($"/api/portal-access/invitations/{first}/replace", null, ct);
            replaced.StatusCode.ShouldBe(HttpStatusCode.OK);
            var second = (await replaced.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
            await recipient.PrimeCsrfAsync(ct);
            (await recipient.PostAsJsonAsync("/api/portal-enrollment/inspect", new { token = oldToken }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var token = await Token(manager, email, ct);
            (await recipient.PostAsJsonAsync("/api/portal-enrollment/accept", new { token, displayName = "Recipient", password = AuthTestSupport.DefaultPassword }, ct)).EnsureSuccessStatusCode();
            (await AuthTestSupport.LoginAsync(recipient, email, ct)).Status.ShouldBe("ok");
            (await recipient.GetAsync(portal, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var pending = await manager.PostAsJsonAsync($"/api/portal-access/{persona}/{target}", new { email }, ct);
            pending.EnsureSuccessStatusCode();
            var pendingToken = await Token(manager, email, ct);
            var access = await manager.GetFromJsonAsync<JsonElement>($"/api/portal-access/{persona}/{target}", ct);
            var userId = access.GetProperty("users")[0].GetProperty("userId").GetGuid();
            (await manager.PostAsync($"/api/portal-access/{persona}/{target}/users/{userId}/revoke", null, ct)).EnsureSuccessStatusCode();
            (await recipient.GetAsync(portal, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            await recipient.PrimeCsrfAsync(ct);
            (await recipient.PostAsJsonAsync("/api/portal-enrollment/inspect", new { token = pendingToken }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var cancelled = await manager.PostAsJsonAsync($"/api/portal-access/{persona}/{target}", new { email }, ct);
            var cancelledId = (await cancelled.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
            var cancelledToken = await Token(manager, email, ct);
            (await manager.PostAsync($"/api/portal-access/invitations/{cancelledId}/cancel", null, ct)).EnsureSuccessStatusCode();
            (await recipient.PostAsJsonAsync("/api/portal-enrollment/inspect", new { token = cancelledToken }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Theory]
    [InlineData("tenant", "/api/portal/tenant/ledger")]
    [InlineData("owner", "/api/portal/owner/summary")]
    public async Task Invited_recipient_accepts_once_and_signs_in_normally(string persona, string portal)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, manager, target) = await Setup(persona, ct);
        using (manager)
        using (var recipient = fixture.Api.CreateClient())
        {
            var email = $"recipient-{UuidV7.NewId():N}@example.test";
            (await manager.PostAsJsonAsync($"/api/portal-access/{persona}/{target}", new { email }, ct)).EnsureSuccessStatusCode();
            var token = await Token(manager, email, ct);
            await recipient.PrimeCsrfAsync(ct);
            var inspection = await recipient.PostAsJsonAsync("/api/portal-enrollment/inspect", new { token }, ct);
            inspection.EnsureSuccessStatusCode();
            (await inspection.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("requiresPassword").GetBoolean().ShouldBeTrue();
            var accepted = await recipient.PostAsJsonAsync("/api/portal-enrollment/accept", new { token, displayName = "Portal recipient", password = AuthTestSupport.DefaultPassword }, ct);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await recipient.PostAsJsonAsync("/api/portal-enrollment/accept", new { token, password = AuthTestSupport.DefaultPassword }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await recipient.GetAsync(portal, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AuthTestSupport.LoginAsync(recipient, email, ct)).Status.ShouldBe("ok");
            (await recipient.GetAsync(portal, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static async Task<string> Token(HttpClient manager, string email, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var response = await manager.GetAsync("/api/portal-access/test-inbox", ct);
            response.EnsureSuccessStatusCode();
            var messages = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var message = messages.EnumerateArray().FirstOrDefault(m => m.GetProperty("email").GetString() == email);
            if (message.ValueKind != JsonValueKind.Undefined)
            { return new Uri(message.GetProperty("acceptUrl").GetString()!).Fragment[7..]; }
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("Local invitation was not delivered.");
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("owner")]
    public async Task Staff_can_invite_an_explicit_target_without_exposing_the_proof(string persona)
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, client, target) = await Setup(persona, ct);
        using (client)
        {
            var email = $"invite-{UuidV7.NewId():N}@example.test";
            var response = await client.PostAsJsonAsync($"/api/portal-access/{persona}/{target}", new { email }, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            json.GetProperty("email").GetString().ShouldBe(email);
            json.GetProperty("status").GetString().ShouldBe("pending");
            json.TryGetProperty("token", out _).ShouldBeFalse();
            var access = await client.GetFromJsonAsync<JsonElement>($"/api/portal-access/{persona}/{target}", ct);
            access.GetProperty("canManage").GetBoolean().ShouldBeTrue();
            access.GetProperty("invitations").GetArrayLength().ShouldBe(1);
        }
    }

    private async Task<(Guid OrgId, HttpClient Client, Guid Target)> Setup(string persona, CancellationToken ct)
    {
        var setup = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMStaff, ct);
        var target = UuidV7.NewId();
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(setup.OrgId, "test:enrollment", async () =>
        {
            if (persona == "tenant") { db.Add(new Tenant { Id = target, DisplayName = "Invited resident" }); }
            else { db.Add(new Owner { Id = target, Name = "Invited owner" }); }
            await db.SaveChangesAsync(ct);
        }, ct);
        return (setup.OrgId, setup.Client, target);
    }
}

