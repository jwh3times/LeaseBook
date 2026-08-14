using Npgsql;

namespace LeaseBook.Tests.Common;

/// <summary>
/// Raw-SQL context setters and operations against <c>audit_events</c>, used by the organization-isolation
/// pack and by every test that has to reach the database below EF. Deliberately bypasses EF (no
/// global query filter, no stamping) so every assertion is about what the <b>database</b> enforces
/// through RLS — not the ergonomic layer (pitfall E2). All writes go through the app role connection
/// the fixture hands out.
/// <para>
/// <b>These are SQL emitters, not executors.</b> They set a GUC on the connection and transaction the
/// caller already owns, and do nothing else — no transaction of their own, no commit, no
/// <c>NOTIFY</c>. That is what makes them safe for the tests that are deliberately raw: a test that
/// must plant a row with no notification, run a statement it expects to raise, write as the migrator
/// role, or compose a context production cannot (platform scope with no org) keeps all of that. What
/// it stops doing is spelling the GUC out itself.
/// </para>
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

    /// <summary>
    /// Opens the platform plane transaction-locally on the caller's transaction (ADR-028).
    /// <para>
    /// This is the ONLY place in <c>tests/</c> allowed to set <c>app.platform</c>, and
    /// <c>PlatformScopeCallSiteTests</c> enforces that — the same rule the production side has, where
    /// <c>PlatformScopedExecutor</c> is the sole setter. Before this helper existed the guard scanned
    /// only <c>src/</c> and <c>infra/</c>, and thirteen independent copies of this one statement
    /// accumulated across seven test files without ever failing a build.
    /// </para>
    /// <para>
    /// Prefer <c>PlatformScopedExecutor</c> (or <c>IPlatformScope</c>) whenever the test does not
    /// need to be raw. Reach for this only when it does: no <c>NOTIFY</c> may be emitted, the
    /// statement under test must be allowed to raise, the write must go as the migrator role, or the
    /// context is one production cannot produce.
    /// </para>
    /// </summary>
    public static async Task SetPlatformAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.platform', 'on', true)", conn, tx);
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
