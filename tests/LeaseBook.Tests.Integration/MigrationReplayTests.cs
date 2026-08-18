using LeaseBook.Tests.Common;
using LeaseBook.Web.Jobs;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// CI-permanent guard: the newest migration must apply to a database that <b>already has rows</b>.
/// <para>
/// Every other container test migrates an <i>empty</i> database to head, which is the one condition
/// under which a broken data migration looks healthy. The failure this exists to catch shipped in
/// <c>M8_DeriveTenantStandingAndUnitOccupancy</c>: it renamed <c>units.status</c> to
/// <c>availability</c>, backfilled the retired values, then added a CHECK constraint over the new
/// vocabulary. Both tables carry <c>FORCE ROW LEVEL SECURITY</c>, which binds the schema owner too,
/// and a migration transaction sets no <c>app.org_id</c> — so the policy predicate was NULL, the
/// backfill silently matched zero rows, and <c>ADD CONSTRAINT</c> (DDL, so never RLS-filtered) then
/// failed on 331 real rows with <c>23514</c>. On an empty database all of that is a no-op, so the
/// whole suite and CI stayed green while <c>docker compose --profile full up</c> could not start and
/// a production migration would have aborted. The fix is to bracket a backfill in
/// <c>ALTER TABLE … NO FORCE ROW LEVEL SECURITY</c> / <c>… FORCE ROW LEVEL SECURITY</c>; this test is
/// what makes forgetting that fail a build instead of a deploy.
/// </para>
/// <para>
/// Shape: seed the demo org at head, roll the newest migration <i>back</i> with that data in place,
/// then re-apply it. Targeting the newest keeps the guard self-maintaining — whatever migration you
/// just wrote is the one under test — and means a migration must have a working <c>Down</c>, which is
/// the standing expectation anyway.
/// </para>
/// <para>
/// <b>Known gap.</b> A migration whose <c>Down</c> drops the table its <c>Up</c> created replays
/// against an empty table, so its backfill is not exercised here. Closing that needs a static scan of
/// migration source for DML on an RLS-forced table, not a container.
/// </para>
/// </summary>
public sealed class MigrationReplayTests : IAsyncLifetime
{
    /// <summary>
    /// Core tables no migration may drop, pinned so the replay cannot pass vacuously — a round trip
    /// over a database that lost its rows would satisfy every other assertion here, the invariant
    /// sweep included, because an empty org violates nothing. Deliberately not "every table": a
    /// migration that legitimately adds a table the seeder populates would drop and recreate it on
    /// the way round, and failing that is noise rather than signal.
    /// </summary>
    private static readonly string[] CoreTables =
    [
        "accounts",
        "bank_accounts",
        "journal_entries",
        "journal_lines",
        "owners",
        "properties",
        "tenants",
        "units",
    ];

    /// <summary>
    /// Its own container, deliberately not <c>DatabaseCollection</c>'s. This test rolls the schema
    /// backwards and forwards, and the shared fixture is a collection fixture — every sibling test
    /// would run against whatever intermediate schema this happened to be sitting on.
    /// </summary>
    private readonly PostgresFixture fixture = new();

    public ValueTask InitializeAsync() => fixture.InitializeAsync();

    public ValueTask DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task Newest_migration_re_applies_against_a_seeded_database()
    {
        var ct = TestContext.Current.CancellationToken;

        // Real seeded data, not a hand-built row or two: the golden demo org is what the invariant
        // sweep below has an opinion about, and it is the dataset every developer's local volume and
        // every `docker compose --profile full up` already holds when migrations land on it.
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        await using var db = fixture.CreateContext(fixture.MigratorConnectionString);
        var migrations = db.Database.GetMigrations().ToList();
        migrations.Count.ShouldBeGreaterThan(
            1,
            "the replay needs a migration to roll back to; with fewer than two there is nothing to test.");

        var newest = migrations[^1];
        var previous = migrations[^2];

        var before = await CountCoreTablesAsync(ct);
        foreach (var (table, count) in before)
        {
            count.ShouldBeGreaterThan(
                0,
                $"{table} is empty in the seeded demo org, so the replay below would prove nothing " +
                "about it. Either the seeder stopped populating it or it no longer belongs in CoreTables.");
        }

        var migrator = db.GetService<IMigrator>();

        await Should.NotThrowAsync(
            () => migrator.MigrateAsync(previous, ct),
            $"the Down of '{newest}' failed against a database with rows in it. If it backfills or " +
            "rewrites data, remember that FORCE ROW LEVEL SECURITY binds leasebook_migrator: bracket " +
            "the statements in ALTER TABLE <t> NO FORCE ROW LEVEL SECURITY / ... FORCE ROW LEVEL SECURITY.");

        await Should.NotThrowAsync(
            () => migrator.MigrateAsync(newest, ct),
            $"'{newest}' applies to an empty database but not to one that already has rows — which is " +
            "every real database it will ever run against. The usual cause is a data backfill that " +
            "silently matched zero rows because FORCE ROW LEVEL SECURITY filters the migrator role " +
            "and a migration transaction sets no app.org_id; a constraint added afterwards then " +
            "rejects the values the backfill was supposed to rewrite.");

        (await db.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty(
            $"the replay was supposed to land back on '{newest}'.");

        var after = await CountCoreTablesAsync(ct);
        foreach (var (table, expected) in before)
        {
            after[table].ShouldBe(
                expected,
                $"{table} changed row count across the '{newest}' round trip — the migration is " +
                "destroying or duplicating data that a real deployment would be carrying.");
        }

        // Structural survival is not enough: a backfill can preserve every row and still leave the
        // ledger wrong. The sweep is the repository's own correctness oracle for that (ADR-001).
        var sweep = await fixture.Api.Services
            .GetRequiredService<ISweepRunner>()
            .RunAsync([DemoSeeder.DemoOrgId], ct);

        sweep.IsClean.ShouldBeTrue(
            $"the demo org no longer satisfies the trust invariants after replaying '{newest}': " +
            string.Join("; ", sweep.Violations.Select(v => $"{v.Invariant} {v.Detail}")));
    }

    /// <summary>
    /// Counts through the <b>app role</b> under an explicit org context. The migrator role cannot be
    /// used for this: FORCE row security binds the table owner as well, so the same counts taken on
    /// the migrator connection come back as zeroes and the comparison passes for the wrong reason —
    /// the very confusion this test exists to catch.
    /// </summary>
    private async Task<Dictionary<string, long>> CountCoreTablesAsync(CancellationToken ct)
    {
        await using var connection = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(connection, tx, DemoSeeder.DemoOrgId, ct);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in CoreTables)
        {
            // Interpolated from the private const array above, never from test input.
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table};", connection, tx);
            counts[table] = (long)(await command.ExecuteScalarAsync(ct))!;
        }

        return counts;
    }
}
