using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration.Support;

/// <summary>
/// Reads platform capability state out of band, for tests asserting what a write actually left behind.
/// <para>
/// Deliberately raw, and deliberately NOT through <c>ICapabilityAdmin</c> or
/// <c>CapabilityStateReader</c>: an oracle that shares a seam with the code under test cannot show
/// that seam is wrong. Every read opens platform scope through <see cref="RlsProbe.SetPlatformAsync"/>,
/// because <c>platform_audit_events</c> is hidden from every organization session whatever its organization context.
/// </para>
/// <para>
/// Shared by the module-level write tests and the CLI verb tests. They assert different things — what
/// the write did, versus what the operator saw — over the same state, and a second copy of these
/// readers would be the drift this file exists to avoid.
/// </para>
/// </summary>
public sealed class CapabilityStateProbe(PostgresFixture fixture)
{
    public async Task<DateTime> NowAsync(CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT now()", conn);
        return (DateTime)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task SeedOrgAsync(Guid orgId, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO orgs (id, name, created_at) VALUES (@id, 'capabilities-test', now())", conn);
        cmd.Parameters.AddWithValue("id", orgId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SeedUserAsync(Guid userId, Guid orgId, CancellationToken ct)
    {
        await using var db = fixture.CreateContext(fixture.MigratorConnectionString);
        db.Add(new AppUser
        {
            Id = userId,
            OrgId = orgId,
            UserName = $"{userId}@capability.test",
            NormalizedUserName = $"{userId}@CAPABILITY.TEST",
            Email = $"{userId}@capability.test",
            NormalizedEmail = $"{userId}@CAPABILITY.TEST",
            SecurityStamp = userId.ToString(),
            ConcurrencyStamp = userId.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Null when no row exists — which resolves as the registry default, not as a kill.</summary>
    public async Task<bool?> ReadFlagAsync(string name, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT enabled FROM feature_flags WHERE name = @name", conn);
        cmd.Parameters.AddWithValue("name", name);
        return await cmd.ExecuteScalarAsync(ct) as bool?;
    }

    /// <summary>
    /// Restores the shared, global flag state. The delete DOES notify: any host still running in this
    /// collection drops its cached set immediately rather than carrying flipped state for up to a TTL
    /// into an unrelated test.
    /// </summary>
    public async Task DeleteFlagAsync(string name, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetPlatformAsync(conn, tx, ct);

        await using (var cmd = new NpgsqlCommand("DELETE FROM feature_flags WHERE name = @name", conn, tx))
        {
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var signal = new NpgsqlCommand(
            $"SELECT pg_notify('{CapabilityNotifications.Channel}', @name)", conn, tx))
        {
            signal.Parameters.AddWithValue("name", name);
            await signal.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public Task<List<AuditRow>> ReadAuditsAsync(
        string action, string capability, DateTime since, CancellationToken ct) =>
        ReadAuditsAsync(
            "SELECT action, capability, org_id, actor, detail_json FROM platform_audit_events " +
            "WHERE action = @action AND capability = @capability AND occurred_at >= @since " +
            "ORDER BY occurred_at, id",
            ct,
            ("action", action), ("capability", capability), ("since", since));

    public Task<List<AuditRow>> ReadAuditsForOrgAsync(Guid orgId, CancellationToken ct) =>
        ReadAuditsAsync(
            "SELECT action, capability, org_id, actor, detail_json FROM platform_audit_events " +
            "WHERE org_id = @org ORDER BY occurred_at, id",
            ct,
            ("org", orgId));

    private async Task<List<AuditRow>> ReadAuditsAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var rows = new List<AuditRow>();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetPlatformAsync(conn, tx, ct);

        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new AuditRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// Reads a row's inserting (sub)transaction id and its timestamp. <c>xmin</c> is a system column
    /// every heap row carries, and rows written by two different transactions can never share it —
    /// which is what makes the atomicity assertion an observation rather than a proxy.
    /// </summary>
    public async Task<(string Xmin, DateTime At)> ReadRowIdentityAsync(
        string sql, Guid orgId, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetPlatformAsync(conn, tx, ct);

        (string Xmin, DateTime At) row;
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddWithValue("org", orgId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            (await reader.ReadAsync(ct)).ShouldBeTrue($"expected exactly one row from: {sql}");
            row = (reader.GetString(0), reader.GetDateTime(1));
            (await reader.ReadAsync(ct)).ShouldBeFalse($"expected exactly one row from: {sql}");
        }

        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<List<(bool Granted, string Actor)>> ReadEntitlementsAsync(
        Guid orgId, CancellationToken ct)
    {
        var rows = new List<(bool, string)>();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetPlatformAsync(conn, tx, ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT granted, actor FROM entitlements WHERE org_id = @org ORDER BY effective_at, granted DESC",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("org", orgId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetBoolean(0), reader.GetString(1)));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    public async Task<List<(Guid? UserId, string AddedBy)>> ReadCohortsAsync(Guid orgId, CancellationToken ct)
    {
        var rows = new List<(Guid?, string)>();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetPlatformAsync(conn, tx, ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT user_id, added_by FROM capability_cohorts WHERE org_id = @org", conn, tx))
        {
            cmd.Parameters.AddWithValue("org", orgId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1)));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// Plants one entitlement event with a chosen id and <c>effective_at</c>, so a test can construct
    /// orderings the write path cannot produce on demand — two events a millisecond apart, or two
    /// sharing a timestamp and separable only by id.
    /// </summary>
    public async Task SeedEntitlementAsync(
        Guid id, Guid orgId, string capability, bool granted, DateTime effectiveAt, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetPlatformAsync(conn, tx, ct);

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor) " +
            "VALUES (@id, @org, @cap, @granted, @at, 'tie-break-test')", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("org", orgId);
            cmd.Parameters.AddWithValue("cap", capability);
            cmd.Parameters.AddWithValue("granted", granted);
            cmd.Parameters.AddWithValue("at", effectiveAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public sealed record AuditRow(string Action, string? Capability, Guid? OrgId, string Actor, string Detail);
}
