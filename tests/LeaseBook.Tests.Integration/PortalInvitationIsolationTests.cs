using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class PortalInvitationIsolationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Invitation_storage_denies_unscoped_reads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await fixture.OpenAppConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM portal_invitations", connection);
        ((long)(await command.ExecuteScalarAsync(ct))!).ShouldBe(0);
    }

    [Fact]
    public async Task Staff_reads_only_own_invitations_and_portal_personas_cannot_read_or_insert_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(ct);
        foreach (var (org, persona, expected) in new[]
        {
            (seeded.OrgId, "staff", 1L), (UuidV7.NewId(), "staff", 0L),
            (seeded.OrgId, "tenant", 0L), (seeded.OrgId, "owner", 0L),
            (seeded.OrgId, "none", 0L), (seeded.OrgId, (string?)null, 0L),
        })
        {
            await using var connection = await fixture.OpenAppConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            await RlsProbe.SetOrgContextAsync(connection, tx, org, persona, seeded.UserId, ct);
            await using var count = new NpgsqlCommand("SELECT count(*) FROM portal_invitations", connection, tx);
            ((long)(await count.ExecuteScalarAsync(ct))!).ShouldBe(expected);
        }

        await using var deniedConnection = await fixture.OpenAppConnectionAsync(ct);
        await using var deniedTx = await deniedConnection.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgContextAsync(deniedConnection, deniedTx, seeded.OrgId, "owner", seeded.UserId, ct);
        await using var insert = Insert(deniedConnection, deniedTx, seeded.OrgId, seeded.OwnerId, seeded.UserId);
        var error = await Should.ThrowAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(ct));
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Invitation_cannot_reference_another_organizations_target_or_creator()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAsync(ct);
        var second = await SeedAsync(ct);
        foreach (var (owner, user) in new[] { (second.OwnerId, first.UserId), (first.OwnerId, second.UserId) })
        {
            await using var connection = await fixture.OpenAppConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            await RlsProbe.SetOrgAsync(connection, tx, first.OrgId, ct);
            await using var insert = Insert(connection, tx, first.OrgId, owner, user);
            var error = await Should.ThrowAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(ct));
            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }
    }

    private static NpgsqlCommand Insert(NpgsqlConnection connection, NpgsqlTransaction tx, Guid org, Guid owner, Guid user)
    {
        var command = new NpgsqlCommand("""
            INSERT INTO portal_invitations
                (id, org_id, persona, owner_id, email, normalized_email, created_at, expires_at,
                 created_by_user_id, status, delivery_status, delivery_attempts)
            VALUES (@id, @org, 'owner', @owner, 'probe@example.test', 'PROBE@EXAMPLE.TEST', now(),
                    now() + interval '72 hours', @user, 'pending', 'pending', 0)
            """, connection, tx);
        command.Parameters.AddWithValue("id", UuidV7.NewId());
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("user", user);
        return command;
    }

    private async Task<(Guid OrgId, Guid OwnerId, Guid UserId)> SeedAsync(CancellationToken ct)
    {
        var org = UuidV7.NewId();
        await AuthTestSupport.CreateOrgAsync(fixture, org, "Invitation isolation", ct);
        await AuthTestSupport.CreateUserAsync(fixture, org, $"manager-{org:N}@example.test", "Manager", Roles.PMAdmin, ct);
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.Where(x => x.OrgId == org).Select(x => x.Id).SingleAsync(ct);
        var owner = UuidV7.NewId();
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(org, "invitation-isolation-test", async () =>
        {
            db.Add(new Owner { Id = owner, Name = "Invited owner" });
            db.Add(new PortalInvitation
            {
                Id = UuidV7.NewId(),
                Persona = "owner",
                OwnerId = owner,
                Email = "recipient@example.test",
                NormalizedEmail = "RECIPIENT@EXAMPLE.TEST",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(72),
                CreatedByUserId = user,
            });
            await db.SaveChangesAsync(ct);
        }, ct);
        return (org, owner, user);
    }
}
