using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Proves the ADR-040 backfill actually carries rows forward, with rows in the table it reads.
/// <para>
/// <see cref="MigrationReplayTests"/> cannot cover this one. It replays against the seeded demo org,
/// and the demo org has never had a statement delivery — so the backfill runs over an empty table and
/// looks healthy for exactly the reason the v0.7.0 incident looked healthy. The static
/// <c>MigrationBackfillRlsTests</c> guard proves the <c>NO FORCE</c>/<c>FORCE</c> bracket is present,
/// which is a different claim from the SQL being right.
/// </para>
/// <para>
/// Shape: roll back to the migration before ADR-040's (which recreates <c>statement_deliveries</c>),
/// insert one row per legacy state, roll forward, and read the three new tables through the <b>app</b>
/// role under org context — so a backfill that silently matched zero rows fails here.
/// </para>
/// </summary>
public sealed class StatementDeliveryBackfillTests : IAsyncLifetime
{
    /// <summary>
    /// Its own container, for the same reason <see cref="MigrationReplayTests"/> has one: this test
    /// moves the schema backwards and forwards, and a shared collection fixture would leave siblings
    /// running against whatever intermediate schema it happened to be sitting on.
    /// </summary>
    private readonly PostgresFixture fixture = new();

    public ValueTask InitializeAsync() => fixture.InitializeAsync();

    public ValueTask DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task Legacy_delivery_rows_become_artifacts_attempts_and_events()
    {
        var ct = TestContext.Current.CancellationToken;

        // The org has to exist for the FKs and the RLS probe below; the demo seed is the cheapest way
        // to get one, and it deliberately contributes no delivery rows of its own.
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        await using var db = fixture.CreateContext(fixture.MigratorConnectionString);
        var migrations = db.Database.GetMigrations().ToList();

        // Pinned by NAME, not by position. This test hand-writes the pre-ADR-040 statement_deliveries
        // shape, so it must roll to exactly the migration that consumed it — an earlier version keyed
        // off "newest" and started failing the moment an unrelated migration landed on top, which is
        // the guard working rather than a flake.
        var target = migrations.SingleOrDefault(
            m => m.EndsWith("M8_SplitStatementDeliveryIntoArtifactAttemptEvents", StringComparison.Ordinal));
        target.ShouldNotBeNull(
            "the ADR-040 migration is gone from the history; retire this test with it");

        var index = migrations.IndexOf(target!);
        index.ShouldBeGreaterThan(0, "the ADR-040 migration must have a predecessor to roll back to");
        var previous = migrations[index - 1];

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous, ct);

        // Write the legacy rows as the migrator, which FORCE row security binds exactly as it binds
        // the migration itself — so the insert needs the same lift the backfill needs.
        await using (var seedConnection = new NpgsqlConnection(fixture.MigratorConnectionString))
        {
            await seedConnection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                """
                ALTER TABLE statement_deliveries NO FORCE ROW LEVEL SECURITY;

                INSERT INTO statement_deliveries
                    (id, org_id, owner_id, period_year, period_month, to_email, state,
                     artifact_key, created_at)
                VALUES
                    (@queuedId, @orgId, @ownerId, 2026, 4, 'queued@example.com', 'queued',
                     'legacy-queued.pdf', now()),
                    (@sentId, @orgId, @ownerId, 2026, 5, 'sent@example.com', 'sent',
                     'legacy-sent.pdf', now()),
                    (@failedId, @orgId, @ownerId, 2026, 6, 'failed@example.com', 'failed',
                     'legacy-failed.pdf', now());

                ALTER TABLE statement_deliveries FORCE ROW LEVEL SECURITY;
                """,
                seedConnection);
            command.Parameters.AddWithValue("queuedId", QueuedId);
            command.Parameters.AddWithValue("sentId", SentId);
            command.Parameters.AddWithValue("failedId", FailedId);
            command.Parameters.AddWithValue("orgId", DemoSeeder.DemoOrgId);
            command.Parameters.AddWithValue("ownerId", DemoIds.O5);
            await command.ExecuteNonQueryAsync(ct);
        }

        await Should.NotThrowAsync(
            () => migrator.MigrateAsync(target!, ct),
            $"'{target}' failed to apply over existing statement_deliveries rows.");

        // ── Read back through the app role under org context ──────────────────────────────
        await using var connection = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(connection, tx, DemoSeeder.DemoOrgId, ct);

        var artifacts = await ReadAsync(
            connection, tx,
            "SELECT artifact_key, basis FROM statement_artifacts ORDER BY artifact_key",
            r => (Key: r.GetString(0), Basis: r.IsDBNull(1) ? null : r.GetString(1)),
            ct);

        artifacts.Count.ShouldBe(
            3,
            "the backfill matched zero rows — the classic symptom of a migration whose DML ran under " +
            "FORCE ROW LEVEL SECURITY with no app.org_id set.");
        artifacts.Select(a => a.Key).ShouldBe(
            ["legacy-failed.pdf", "legacy-queued.pdf", "legacy-sent.pdf"]);
        artifacts.ShouldAllBe(a => a.Basis == null,
            "statement_deliveries never recorded a basis, so the backfill must leave it null rather " +
            "than invent one (ADR-040 §6).");

        var attempts = await ReadAsync(
            connection, tx,
            "SELECT to_email FROM statement_delivery_attempts ORDER BY to_email",
            r => r.GetString(0),
            ct);

        attempts.ShouldBe(["failed@example.com", "queued@example.com", "sent@example.com"],
            "each legacy row becomes exactly one attempt, keeping its recipient.");

        // The one substantive mapping: 'sent' meant provider acceptance, so it becomes 'accepted'.
        var history = await ReadAsync(
            connection, tx,
            """
            SELECT art.artifact_key, ev.sequence, ev.kind
            FROM statement_delivery_events ev
            JOIN statement_delivery_attempts att
              ON att.org_id = ev.org_id AND att.id = ev.attempt_id
            JOIN statement_artifacts art
              ON art.org_id = att.org_id AND art.id = att.artifact_id
            ORDER BY art.artifact_key, ev.sequence
            """,
            r => (Key: r.GetString(0), Sequence: r.GetInt32(1), Kind: r.GetString(2)),
            ct);

        history.ShouldBe(
        [
            ("legacy-failed.pdf", 1, "queued"),
            ("legacy-failed.pdf", 2, "failed"),
            ("legacy-queued.pdf", 1, "queued"),
            ("legacy-sent.pdf", 1, "queued"),
            ("legacy-sent.pdf", 2, "accepted"),
        ]);

        await tx.RollbackAsync(ct);
    }

    private static readonly Guid QueuedId = new("01923000-0000-7000-8000-00000000d001");
    private static readonly Guid SentId = new("01923000-0000-7000-8000-00000000d002");
    private static readonly Guid FailedId = new("01923000-0000-7000-8000-00000000d003");

    private static async Task<List<T>> ReadAsync<T>(
        NpgsqlConnection connection,
        System.Data.Common.DbTransaction tx,
        string sql,
        Func<NpgsqlDataReader, T> project,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction)tx);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(project(reader));
        }

        return rows;
    }
}
