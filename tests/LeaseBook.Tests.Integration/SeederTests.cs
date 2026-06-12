using System.Net;
using System.Net.Http.Json;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The demo seeder (WP-09): idempotent provisioning of the org + admin, an org-scoped audit write
/// through <c>OrgScopedExecutor</c>, and the seeded admin authenticating through the real auth API.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SeederTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Seeding_twice_is_idempotent_and_the_seeded_admin_can_log_in()
    {
        var ct = TestContext.Current.CancellationToken;

        // Re-running must not duplicate anything.
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        await using (var db = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            (await db.Orgs.CountAsync(o => o.Id == DemoSeeder.DemoOrgId, ct)).ShouldBe(1);
        }

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var admin = await userManager.FindByEmailAsync(DemoSeeder.AdminEmail);
            admin.ShouldNotBeNull();
            admin.OrgId.ShouldBe(DemoSeeder.DemoOrgId);
            (await userManager.IsInRoleAsync(admin, Roles.PMAdmin)).ShouldBeTrue();
        }

        // Exactly one provisioning audit row exists under the demo org (read as app role + context).
        (await DemoOrgAuditCountAsync(ct)).ShouldBe(1);

        // The seeded admin authenticates through the real API with the documented dev password.
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(DemoSeeder.AdminEmail, DemoSeeder.AdminPassword), ct);

        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<LoginResponse>(ct))!.Status.ShouldBe(LoginStatus.Ok);
    }

    private async Task<long> DemoOrgAuditCountAsync(CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, DemoSeeder.DemoOrgId, ct);
        var count = await RlsProbe.CountEventsAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
        return count;
    }
}
