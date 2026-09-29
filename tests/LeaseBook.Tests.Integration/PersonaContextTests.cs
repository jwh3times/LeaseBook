using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// How the persona reaches the database (#314, ADR-048): the executor sets it with the org in one
/// transaction-local statement, refuses the combinations that would widen or silently empty a unit
/// of work, and the request pipeline resolves it from roles — a mixed role set reads nothing, and a
/// portal read never writes.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PersonaContextTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_executor_sets_org_persona_and_user_transaction_locally()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        var userId = UuidV7.NewId();
        var org = new OrgContext();
        var actor = new ActorContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, org, actor);
        var executor = new OrgScopedExecutor(db, org, actor);

        var seen = await executor.RunAsync(orgId, Actor.User(userId), Persona.Tenant, () => ReadContextAsync(db, ct), ct);
        seen.ShouldBe((orgId.ToString(), "tenant", userId.ToString()));

        var system = await executor.RunAsSystemAsync(orgId, "test-harness", () => ReadContextAsync(db, ct), ct);
        system.ShouldBe((orgId.ToString(), "system", ""), "a system actor carries no user id — '' maps to NULL in every grant");

        // Same pooled connection, after commit: nothing survives the transaction that set it.
        await db.Database.OpenConnectionAsync(ct);
        var leaked = await ReadContextAsync(db, ct);
        await db.Database.CloseConnectionAsync();
        string.IsNullOrEmpty(leaked.Org).ShouldBeTrue();
        string.IsNullOrEmpty(leaked.Persona).ShouldBeTrue($"app.persona leaked as '{leaked.Persona}'");
        string.IsNullOrEmpty(leaked.User).ShouldBeTrue();
    }

    [Theory]
    [InlineData(true, Persona.Tenant)]  // a portal persona is granted rows through a user's link
    [InlineData(true, Persona.Owner)]
    [InlineData(false, Persona.System)] // the org-wide system persona is never a user's
    [InlineData(false, (Persona)42)]    // not a persona at all
    public async Task The_executor_refuses_an_incoherent_actor_and_persona_before_touching_the_database(
        bool systemActor, Persona persona)
    {
        var ct = TestContext.Current.CancellationToken;
        var org = new OrgContext();
        var actor = new ActorContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, org, actor);
        var executor = new OrgScopedExecutor(db, org, actor);
        var who = systemActor ? Actor.System("test-harness") : Actor.User(UuidV7.NewId());

        var executed = false;
        await Should.ThrowAsync<ArgumentException>(async () =>
            await executor.RunAsync(UuidV7.NewId(), who, persona, () =>
            {
                executed = true;
                return Task.CompletedTask;
            }, ct));

        executed.ShouldBeFalse();
        org.OrgId.ShouldBeNull();
    }

    [Theory]
    [InlineData(Roles.PMStaff, Roles.Owner)]
    [InlineData(Roles.PMStaff, Roles.Tenant)]
    [InlineData(Roles.Owner, Roles.Tenant)]
    public async Task A_mixed_role_principal_reads_nothing_even_on_a_staff_endpoint(string first, string second)
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Mixed persona", ct);
        await InOrgAsync(orgId, sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(new Owner { Id = UuidV7.NewId(), Name = "Visible to staff" });
            return db.SaveChangesAsync(ct);
        }, ct);

        // Positive control: a staff-only principal in the same org sees the owner.
        using var staff = await LoginAsync(orgId, [Roles.PMStaff], ct);
        (await ListOwnersAsync(staff, ct)).Total.ShouldBe(1);

        using var mixed = await LoginAsync(orgId, [first, second], ct);
        var response = await mixed.GetAsync("/api/directory/owners", ct);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            // The staff role got it past authorization; the database still admits nothing for 'none'.
            (await response.Content.ReadFromJsonAsync<PagedResponse<OwnerListRow>>(ct))!.Total.ShouldBe(0);
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task An_owner_portal_read_resolves_missing_settings_without_writing_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Settings-less org", ct);
        var email = $"owner-{orgId:N}@persona.test";
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Owner", Roles.Owner, ct);
        await InOrgAsync(orgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(new Owner { Id = ownerId, Name = "Settings-less owner" });
            await db.SaveChangesAsync(ct);
            var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(ct);
            await sp.GetRequiredService<OwnerAccessService>().GrantAsync(userId, ownerId, ct);
        }, ct);
        (await SettingsRowsAsync(orgId, ct)).ShouldBe(0);

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthTestSupport.DefaultPassword), ct))
            .EnsureSuccessStatusCode();
        var response = await client.GetAsync("/api/portal/owner/summary", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<OwnerPortalSummary>(ct))!.Basis.ShouldBe("cash");
        (await SettingsRowsAsync(orgId, ct)).ShouldBe(0, "a portal read must not create the org's settings row");
    }

    private static async Task<(string? Org, string? Persona, string? User)> ReadContextAsync(AppDbContext db, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand(
            "SELECT current_setting('app.org_id', true), current_setting('app.persona', true), current_setting('app.user_id', true)", conn);
        if (db.Database.CurrentTransaction?.GetDbTransaction() is NpgsqlTransaction tx)
        {
            cmd.Transaction = tx;
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private async Task<long> SettingsRowsAsync(Guid orgId, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM org_settings", conn, tx);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<HttpClient> LoginAsync(Guid orgId, string[] roles, CancellationToken ct)
    {
        var email = $"{string.Join('-', roles).ToLowerInvariant()}-{UuidV7.NewId():N}@persona.test";
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Persona user", roles[0], ct);
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            foreach (var role in roles.Skip(1))
            {
                AccountSecurityAudit.RequireSuccess(await users.AddToRoleAsync(user, role));
            }
        }

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await AuthTestSupport.LoginAsync(client, email, ct);
        login.Status.ShouldBe("ok");
        await client.PrimeCsrfAsync(ct);
        return client;
    }

    private static async Task<PagedResponse<OwnerListRow>> ListOwnersAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync("/api/directory/owners", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PagedResponse<OwnerListRow>>(ct))!;
    }

    private async Task InOrgAsync(Guid orgId, Func<IServiceProvider, Task> action, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>()
            .RunAsSystemAsync(orgId, "test:persona", () => action(scope.ServiceProvider), ct);
    }
}
