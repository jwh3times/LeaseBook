using Npgsql;

namespace LeaseBook.Tests.Common;

/// <summary>
/// Raw-SQL operations against <c>audit_events</c> used by the tenant-isolation pack. Deliberately
/// bypasses EF (no global query filter, no stamping) so every assertion is about what the
/// <b>database</b> enforces through RLS — not the ergonomic layer (pitfall E2). All writes go
/// through the app role connection the fixture hands out.
/// </summary>
public static class RlsProbe
{
    /// <summary>Sets <c>app.org_id</c> transaction-locally — the parameterized <c>SET LOCAL</c>.</summary>
    public static async Task SetOrgAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid orgId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.org_id', @org, true)", conn, tx);
        cmd.Parameters.AddWithValue("org", orgId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task InsertEventAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid orgId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO audit_events (id, org_id, entity_type, entity_id, action, occurred_at) " +
            "VALUES (@id, @org, 'probe', @eid, 'insert', now())", conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("eid", Guid.CreateVersion7());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<long> CountEventsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_events", conn, tx);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task<string?> CurrentOrgSettingAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_setting('app.org_id', true)", conn);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
