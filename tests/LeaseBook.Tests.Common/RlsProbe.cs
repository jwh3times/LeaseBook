using Microsoft.EntityFrameworkCore;
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
    /// <summary>
    /// Sets <c>app.org_id</c> transaction-locally — the parameterized <c>SET LOCAL</c> — under the
    /// organization-wide <c>staff</c> persona.
    /// <para>
    /// The persona is part of this on purpose (#314, ADR-048). Every raw test written before the
    /// persona gate existed means "the app, org-wide" by setting an org, and the gate now admits no
    /// rows at all without a persona; setting it here keeps that meaning in one place rather than in
    /// every caller. Tests about the persona boundary itself use
    /// <see cref="SetOrgContextAsync"/>, which states the persona and user explicitly.
    /// </para>
    /// </summary>
    public static Task SetOrgAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid orgId, CancellationToken ct) =>
        SetOrgContextAsync(conn, tx, orgId, "staff", userId: null, ct);

    /// <summary>
    /// The raw mirror of what <c>OrgScopedExecutor</c> sets: <c>app.org_id</c>, <c>app.persona</c> and
    /// <c>app.user_id</c>, transaction-locally. A null <paramref name="persona"/> or
    /// <paramref name="userId"/> leaves that GUC untouched, so a test can compose the unset case —
    /// and <paramref name="persona"/> is a string, not the enum, so it can compose an unknown value.
    /// <para>
    /// This is the ONLY place in <c>tests/</c> allowed to set these three, and
    /// <c>OrgContextCallSiteTests</c> enforces it — the same rule <see cref="SetPlatformAsync"/> follows.
    /// </para>
    /// </summary>
    public static async Task SetOrgContextAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid orgId, string? persona, Guid? userId, CancellationToken ct)
    {
        await using (var cmd = new NpgsqlCommand("SELECT set_config('app.org_id', @org, true)", conn, tx))
        {
            cmd.Parameters.AddWithValue("org", orgId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (persona is not null)
        {
            await using var cmd = new NpgsqlCommand("SELECT set_config('app.persona', @persona, true)", conn, tx);
            cmd.Parameters.AddWithValue("persona", persona);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (userId is { } user)
        {
            await using var cmd = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", conn, tx);
            cmd.Parameters.AddWithValue("user", user.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// The EF-context form of <see cref="SetOrgAsync(NpgsqlConnection, NpgsqlTransaction, Guid, CancellationToken)"/>,
    /// on a transaction the caller already opened on <paramref name="db"/>. For the tests that read
    /// through a module's own <c>DbContext</c> without going through the executor.
    /// </summary>
    public static async Task SetOrgAsync(DbContext db, Guid orgId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Open a transaction first: the context is set transaction-locally and would die with " +
                "an implicit one before the next statement ran.");
        }

        await db.Database.ExecuteSqlAsync(
            $"""
             SELECT set_config('app.org_id', {orgId.ToString()}, true),
                    set_config('app.persona', 'staff', true)
             """,
            ct);
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
