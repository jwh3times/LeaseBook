using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class ResidentPortalTests(PostgresFixture fixture)
{
    private const string LedgerPath = "/api/portal/tenant/ledger";

    [Fact]
    public async Task Fixture_residents_see_only_their_real_ledger_with_an_allowlisted_projection()
    {
        var ct = TestContext.Current.CancellationToken;
        await PortalSeeder.SeedAsync(fixture.Api.Services, ct);
        await PortalSeeder.SeedAsync(fixture.Api.Services, ct); // idempotent
        var fixtureTenants = new List<Guid>();
        foreach (var fixtureOrg in new[] { PortalSeeder.PortalOrgId, PortalSeeder.OtherOrgId })
        {
            await InOrg(fixtureOrg, async sp => fixtureTenants.AddRange(
                await sp.GetRequiredService<AppDbContext>().Set<ResidentAccess>().Select(l => l.TenantId).ToListAsync(ct)), ct);
        }
        foreach (var (email, name, orgId, balance) in new[]
        {
            (PortalSeeder.ResidentAEmail, "Resident A", PortalSeeder.PortalOrgId, 700m),
            (PortalSeeder.ResidentBEmail, "Resident B", PortalSeeder.PortalOrgId, 900m),
            (PortalSeeder.ResidentCEmail, "Resident C", PortalSeeder.OtherOrgId, 700m),
        })
        {
            using var client = await Login(email, PortalSeeder.Password, ct);
            using var scope = fixture.Api.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var userId = await db.Users.Where(u => u.OrgId == orgId && u.Email == email).Select(u => u.Id).SingleAsync(ct);
            TenantLedgerResponse? accounting = null;
            await sp.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "test:portal", async () =>
            {
                var link = await db.Set<ResidentAccess>().SingleAsync(l => l.UserId == userId, ct);
                accounting = await sp.GetRequiredService<ISender>().Query(new GetTenantLedger(link.TenantId), ct);
            }, ct);
            // Selectors in query strings and headers must not influence this endpoint's scope.
            client.DefaultRequestHeaders.Add("X-Org-Id", PortalSeeder.OtherOrgId.ToString());
            var response = await client.GetAsync(LedgerPath + $"?tenantId={UuidV7.NewId()}&orgId={PortalSeeder.OtherOrgId}", ct);
            response.EnsureSuccessStatusCode();
            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
            var ledger = (await response.Content.ReadFromJsonAsync<ResidentLedgerResponse>(ct))!;
            ledger.ResidentName.ShouldBe(name);
            ledger.Balance.ShouldBe(balance);
            ledger.Balance.ShouldBe(accounting!.Balance);
            ledger.Rows.Select(r => r.Balance).ShouldBe(accounting.Rows.Select(r => r.Balance));
            ledger.Rows.Count.ShouldBe(4);
            ledger.Rows.Count(r => r.IsVoided).ShouldBe(1);
            ledger.Rows.Count(r => r.IsReversal).ShouldBe(1);
            foreach (var forgedTenant in fixtureTenants)
            {
                var forged = await client.GetFromJsonAsync<ResidentLedgerResponse>(
                    LedgerPath + $"?tenantId={forgedTenant}&orgId={PortalSeeder.OtherOrgId}", ct);
                forged!.ResidentName.ShouldBe(name);
                forged.Balance.ShouldBe(balance);
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            json.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(new[] { "balance", "residentName", "rows" });
            foreach (var row in json.RootElement.GetProperty("rows").EnumerateArray())
            {
                row.EnumerateObject().Select(p => p.Name).Order().ShouldBe(
                    new[] { "balance", "category", "charge", "date", "isReversal", "isVoided", "payment" });
            }
            foreach (var path in new[] { "/api/directory/tenants", "/api/accounting/tenants/" + UuidV7.NewId() + "/ledger", "/api/dashboard" })
            {
                (await client.GetAsync(path, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }
        }
    }

    [Theory]
    [InlineData(Roles.PMAdmin)]
    [InlineData(Roles.PMStaff)]
    [InlineData(Roles.Owner)]
    public async Task Other_personas_cannot_use_the_portal_even_with_a_valid_link(string role)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(role, ct);
        using var client = await Login(setup.Email, AuthTestSupport.DefaultPassword, ct);
        (await client.GetAsync(LedgerPath, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Missing_revoked_replaced_and_system_links_are_resolved_on_each_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(Roles.Tenant, ct);
        using var client = await Login(setup.Email, AuthTestSupport.DefaultPassword, ct);
        (await Read(client, ct)).ResidentName.ShouldBe("First resident");
        await InOrg(setup.OrgId, async sp => await sp.GetRequiredService<ResidentAccessService>().RevokeAsync(setup.UserId, ct), ct);
        (await client.GetAsync(LedgerPath, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await InOrg(setup.OrgId, async sp => await sp.GetRequiredService<ResidentAccessService>().GrantAsync(setup.UserId, setup.SecondTenant, ct), ct);
        (await Read(client, ct)).ResidentName.ShouldBe("Second resident");
        await InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var tenant = await db.Set<Tenant>().SingleAsync(t => t.Id == setup.SecondTenant, ct);
            tenant.IsSystem = true;
            await db.SaveChangesAsync(ct);
        }, ct);
        (await client.GetAsync(LedgerPath, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var audits = await db.AuditEvents.Where(a => a.EntityType == "resident_access").ToListAsync(ct);
            audits.Count.ShouldBe(3);
            audits.ShouldAllBe(a => a.ActorKind == "system" && a.ActorProcess == "test:portal");
            audits.ShouldAllBe(a => a.After == null || !a.After.Contains("Password"));
        }, ct);

        var unlinked = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.Tenant, ct);
        using var noLink = unlinked.Client;
        (await noLink.GetAsync(LedgerPath, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var anonymous = fixture.Api.CreateClient();
        (await anonymous.GetAsync(LedgerPath, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Provision_and_resolution_explicitly_reject_foreign_identity_and_system_tenants()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateLinkedUser(Roles.Tenant, ct);
        var b = await CreateLinkedUser(Roles.Tenant, ct);
        await InOrg(a.OrgId, async sp =>
        {
            var access = sp.GetRequiredService<ResidentAccessService>();
            await Should.ThrowAsync<InvalidOperationException>(() => access.GrantAsync(b.UserId, a.SecondTenant, ct));
            await Should.ThrowAsync<InvalidOperationException>(() => access.GrantAsync(a.UserId, b.SecondTenant, ct));
            await Should.ThrowAsync<InvalidOperationException>(() => access.RevokeAsync(b.UserId, ct));
            // Identity rows have no RLS: this principal names B while the ambient transaction is A.
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, b.UserId.ToString())], "test"));
            (await access.ResolveAsync(principal, ct)).ShouldBeNull();
            var db = sp.GetRequiredService<AppDbContext>();
            var system = new Tenant { Id = UuidV7.NewId(), DisplayName = "System", IsSystem = true };
            db.Add(system);
            await db.SaveChangesAsync(ct);
            await Should.ThrowAsync<InvalidOperationException>(() => access.GrantAsync(a.UserId, system.Id, ct));
            await Should.ThrowAsync<InvalidOperationException>(() => access.GrantAsync(a.UserId, a.SecondTenant, ct));
        }, ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Database_rejects_cross_org_references_even_when_bypassing_provision_helper(bool foreignUser)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateLinkedUser(Roles.Tenant, ct);
        var b = await CreateLinkedUser(Roles.Tenant, ct);
        var error = await Should.ThrowAsync<DbUpdateException>(() => InOrg(a.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(new ResidentAccess
            {
                Id = UuidV7.NewId(),
                UserId = foreignUser ? b.UserId : a.UserId,
                TenantId = foreignUser ? a.SecondTenant : b.SecondTenant,
                RevokedAt = DateTime.UtcNow, // avoid the active-user unique index masking the FK
            });
            await db.SaveChangesAsync(ct);
        }, ct));
        ((PostgresException)error.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Multiple_users_can_share_a_tenant_but_each_user_has_only_one_active_link()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(Roles.Tenant, ct);
        var secondEmail = $"second-{setup.UserId:N}@fixture.test";
        await AuthTestSupport.CreateUserAsync(fixture, setup.OrgId, secondEmail, "Second user", Roles.Tenant, ct);
        await InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var link = await db.Set<ResidentAccess>().SingleAsync(l => l.UserId == setup.UserId, ct);
            var secondUser = await db.Users.SingleAsync(u => u.OrgId == setup.OrgId && u.Email == secondEmail, ct);
            await sp.GetRequiredService<ResidentAccessService>().GrantAsync(secondUser.Id, link.TenantId, ct);
        }, ct);
        using var secondClient = await Login(secondEmail, AuthTestSupport.DefaultPassword, ct);
        (await Read(secondClient, ct)).ResidentName.ShouldBe("First resident");
        var error = await Should.ThrowAsync<DbUpdateException>(() => InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(new ResidentAccess { Id = UuidV7.NewId(), UserId = setup.UserId, TenantId = setup.SecondTenant });
            await db.SaveChangesAsync(ct);
        }, ct));
        ((PostgresException)error.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Link_rows_fail_closed_without_org_context_and_are_filtered_by_rls()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateLinkedUser(Roles.Tenant, ct);
        var b = await CreateLinkedUser(Roles.Tenant, ct);
        await using var unscoped = fixture.CreateContext(fixture.AppConnectionString);
        (await unscoped.Set<ResidentAccess>().IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
        await InOrg(a.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<ResidentAccess>().IgnoreQueryFilters().AnyAsync(l => l.UserId == b.UserId, ct)).ShouldBeFalse();
        }, ct);
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ResidentAccessService>();
        await Should.ThrowAsync<InvalidOperationException>(() => service.GrantAsync(a.UserId, a.SecondTenant, ct));
    }

    [Fact]
    public async Task Portal_fixture_refuses_production_before_resolving_database_services()
    {
        using var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
                new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" })
            .BuildServiceProvider();
        await Should.ThrowAsync<InvalidOperationException>(() => PortalSeeder.SeedAsync(services, TestContext.Current.CancellationToken));
    }

    private async Task<(Guid OrgId, Guid UserId, Guid SecondTenant, string Email)> CreateLinkedUser(string role, CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        var email = $"portal-{orgId:N}@fixture.test";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Portal test", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "User", role, ct);
        var userId = Guid.Empty;
        var second = UuidV7.NewId();
        await InOrg(orgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            userId = await db.Users.Where(u => u.OrgId == orgId && u.Email == email).Select(u => u.Id).SingleAsync(ct);
            var first = new Tenant { Id = UuidV7.NewId(), DisplayName = "First resident" };
            db.AddRange(first, new Tenant { Id = second, DisplayName = "Second resident" });
            await db.SaveChangesAsync(ct);
            await sp.GetRequiredService<ResidentAccessService>().GrantAsync(userId, first.Id, ct);
        }, ct);
        return (orgId, userId, second, email);
    }

    private async Task InOrg(Guid orgId, Func<IServiceProvider, Task> action, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>()
            .RunAsSystemAsync(orgId, "test:portal", () => action(scope.ServiceProvider), ct);
    }

    private async Task<HttpClient> Login(string email, string password, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<ResidentLedgerResponse> Read(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<ResidentLedgerResponse>(LedgerPath, ct))!;
}
