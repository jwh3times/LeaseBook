using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// A migration's DML runs as <c>leasebook_migrator</c>, and <c>FORCE ROW LEVEL SECURITY</c> binds the
/// schema owner too. The migration transaction sets no <c>app.org_id</c>, so every org-isolation
/// predicate is NULL and an <c>UPDATE</c> or <c>DELETE</c> matches <b>zero rows</b> — without raising,
/// because RLS filters those commands rather than rejecting them. The failure then surfaces somewhere
/// else entirely, typically as a <c>23514</c> from a CHECK constraint added after the backfill, naming
/// the constraint and never the cause.
/// <para>
/// This shipped once, in v0.7.0's migration for issue #180, and left the release unable to migrate any
/// database that already held rows. The fix (#215) added <c>MigrationReplayTests</c>, which
/// seeds the demo org, rolls the newest migration back and re-applies it. That guard cannot see a
/// migration whose <c>Down</c> drops the table its <c>Up</c> created: the replay then runs against an
/// empty table, and a no-op backfill looks healthy against no data. Closing that tail is what this
/// scan is for — it reads source, so an empty database cannot hide anything from it.
/// </para>
/// <para>
/// The two guards are complementary, not redundant. The replay proves the SQL actually rewrites rows;
/// this proves the bracketing is present at all, including where no replay can reach.
/// </para>
/// </summary>
public sealed class MigrationBackfillRlsTests
{
    private const string MigrationsRoot = "src/LeaseBook.Web/Migrations";

    /// <summary>
    /// The migration whose backfill is the reason this guard exists. Pinned so the end-to-end scan
    /// cannot pass by finding nothing: if literal extraction or statement splitting breaks, the
    /// assertion that this file still reads as bracketed DML on a forced table goes red.
    /// </summary>
    private const string ShippedBackfill =
        "src/LeaseBook.Web/Migrations/20260817054050_M8_DeriveTenantStandingAndUnitOccupancy.cs";

    /// <summary>
    /// Deliberate exceptions, as (migration path, table) with the reason stated. Empty, and it should
    /// stay that way — a migration that means to touch only one org's rows is already suspect, since a
    /// migration has no org context to mean it with.
    /// <para>
    /// Pinned to full relative paths for the same reason <see cref="PlatformScopeCallSiteTests"/> is:
    /// an EndsWith on a file name would exempt any same-named file dropped anywhere in the tree.
    /// </para>
    /// </summary>
    private static readonly (string Migration, string Table)[] Exempt = [];

    [Fact]
    public void Every_migration_backfill_on_a_forced_table_brackets_itself_with_no_force()
    {
        var migrations = Migrations();
        var forced = MigrationRlsGuard.ForcedTables(migrations);
        var offenders = new List<string>();

        foreach (var migration in migrations)
        {
            foreach (var block in MigrationRlsGuard.SqlBlocks(migration))
            {
                offenders.AddRange(
                    MigrationRlsGuard.FindUnbracketedDml(block, forced)
                        .Where(violation => !IsExempt(migration.RelativePath, violation))
                        .Select(violation => $"{migration.RelativePath}: {violation}"));
            }
        }

        offenders.ShouldBeEmpty(
            "bracket the backfill with ALTER TABLE <t> NO FORCE ROW LEVEL SECURITY / ... FORCE ROW LEVEL " +
            "SECURITY in the same Sql block — FORCE binds the migrator, so an unbracketed UPDATE/DELETE " +
            "silently rewrites nothing and the failure surfaces later as someone else's constraint" +
            (offenders.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }

    /// <summary>
    /// The scan reads raw-string and single-line <c>Sql</c> literals. A backfill written any other way
    /// — verbatim string, concatenation, or EF's own <c>UpdateData</c>/<c>DeleteData</c> builders —
    /// would be invisible to it, and an invisible violation reads exactly like no violation. This is
    /// the only assertion that can tell those apart.
    /// </summary>
    [Fact]
    public void No_migration_carries_dml_the_scan_cannot_see()
    {
        var unreadable = Migrations()
            .Where(MigrationRlsGuard.HasDmlOutsideExtractedBlocks)
            .Select(migration => migration.RelativePath)
            .ToList();

        unreadable.ShouldBeEmpty(
            "write migration DML as a raw-string literal passed to migrationBuilder.Sql, never as a " +
            "verbatim literal or an InsertData/UpdateData/DeleteData builder — those emit a statement " +
            "the FORCE-RLS bracketing cannot enclose, and this scan would pass over the file having " +
            "checked nothing" +
            (unreadable.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, unreadable)));
    }

    /// <summary>
    /// Vacuity control for the table set. Every <c>Rls</c> helper forces RLS, so all four shapes belong
    /// here: a platform table's DML is filtered by a missing <c>app.platform</c> exactly as an
    /// org-scoped table's is by a missing <c>app.org_id</c>.
    /// </summary>
    [Fact]
    public void The_forced_table_set_covers_every_rls_shape()
    {
        var forced = MigrationRlsGuard.ForcedTables(Migrations());

        forced.ShouldContain("journal_entries");           // EnableOrgRls
        forced.ShouldContain("entitlements");              // EnableOrgRlsWithPlatformEscape
        forced.ShouldContain("feature_flags");             // EnableGlobalReadPlatformWriteRls
        forced.ShouldContain("platform_audit_events");     // EnablePlatformOnlyRls
        forced.ShouldNotContain("orgs");                   // no RLS: the tenancy root itself
    }

    /// <summary>
    /// End-to-end vacuity control. The scan above is only meaningful if it actually reaches a real
    /// backfill and classifies it, rather than extracting nothing from every file.
    /// </summary>
    [Fact]
    public void The_scan_reaches_the_shipped_backfill_and_reads_it_as_compliant()
    {
        var migration = RepositorySource.Current.File(ShippedBackfill);
        var forced = MigrationRlsGuard.ForcedTables(Migrations());

        var blocks = MigrationRlsGuard.SqlBlocks(migration);
        blocks.ShouldNotBeEmpty();

        // Strip the bracketing and the same file must go red — proof the compliant reading below is
        // the guard working, not the guard shrugging.
        var withoutBrackets = blocks
            .Select(block => block.Replace("NO FORCE ROW LEVEL SECURITY", "ROW LEVEL SECURITY", StringComparison.Ordinal))
            .SelectMany(block => MigrationRlsGuard.FindUnbracketedDml(block, forced))
            .ToList();
        withoutBrackets.ShouldNotBeEmpty("the shipped backfill must be DML the scan recognizes on a forced table");

        blocks.SelectMany(block => MigrationRlsGuard.FindUnbracketedDml(block, forced)).ShouldBeEmpty();
    }

    /// <summary>
    /// The bite tests. The first case is the v0.7.0 shape verbatim — the migration as it shipped before
    /// #215 fixed it.
    /// </summary>
    [Theory]
    [InlineData("UPDATE units SET availability = 'available' WHERE availability IN ('occupied', 'vacant');", true)]
    [InlineData("DELETE FROM journal_lines WHERE amount = 0;", true)]
    [InlineData("INSERT INTO orgs (id) SELECT org_id FROM units;", true)]
    [InlineData("UPDATE orgs SET name = 'x' FROM units WHERE units.org_id = orgs.id;", true)]
    [InlineData("UPDATE orgs SET name = 'x';", false)]
    [InlineData("INSERT INTO units (id, org_id) VALUES (gen_random_uuid(), gen_random_uuid());", false)]
    [InlineData("ALTER TABLE units ADD COLUMN note text;", false)]
    public void The_guard_bites_only_on_statements_rls_filters_silently(string sql, bool expectViolation)
    {
        var violations = MigrationRlsGuard.FindUnbracketedDml(sql, ForcedFixture);

        violations.Any().ShouldBe(expectViolation, $"unexpected reading of: {sql}");
    }

    [Fact]
    public void A_bracketed_backfill_is_accepted()
    {
        const string sql = """
            ALTER TABLE units NO FORCE ROW LEVEL SECURITY;

            UPDATE units SET availability = 'available' WHERE availability = 'vacant';

            ALTER TABLE units FORCE ROW LEVEL SECURITY;
            """;

        MigrationRlsGuard.FindUnbracketedDml(sql, ForcedFixture).ShouldBeEmpty();
    }

    /// <summary>
    /// A lift with no restore leaves the table permanently unforced against its own owner — worse than
    /// the silent no-op it was meant to fix, and invisible to any test that only checks the data.
    /// </summary>
    [Fact]
    public void A_lift_that_is_never_restored_is_a_violation()
    {
        const string sql = """
            ALTER TABLE units NO FORCE ROW LEVEL SECURITY;

            UPDATE units SET availability = 'available' WHERE availability = 'vacant';
            """;

        MigrationRlsGuard.FindUnbracketedDml(sql, ForcedFixture)
            .ShouldHaveSingleItem()
            .ShouldContain("FORCE never restored");
    }

    [Fact]
    public void A_lift_that_comes_after_the_write_is_a_violation()
    {
        const string sql = """
            UPDATE units SET availability = 'available' WHERE availability = 'vacant';

            ALTER TABLE units NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE units FORCE ROW LEVEL SECURITY;
            """;

        MigrationRlsGuard.FindUnbracketedDml(sql, ForcedFixture)
            .ShouldHaveSingleItem()
            .ShouldContain("NO FORCE ROW LEVEL SECURITY` before it");
    }

    /// <summary>
    /// Bracketing one table does not cover another named in the same statement: a filtered join source
    /// zeroes the result exactly as a filtered target does.
    /// </summary>
    [Fact]
    public void Bracketing_covers_only_the_tables_it_names()
    {
        const string sql = """
            ALTER TABLE units NO FORCE ROW LEVEL SECURITY;

            UPDATE units SET availability = 'unavailable'
            FROM tenants WHERE tenants.unit_id = units.id;

            ALTER TABLE units FORCE ROW LEVEL SECURITY;
            """;

        MigrationRlsGuard.FindUnbracketedDml(sql, ForcedFixture)
            .ShouldHaveSingleItem()
            .ShouldContain("tenants");
    }

    private static IReadOnlySet<string> ForcedFixture =>
        new HashSet<string>(StringComparer.Ordinal) { "units", "tenants", "journal_lines" };

    private static bool IsExempt(string migration, string violation) =>
        Exempt.Any(entry =>
            string.Equals(entry.Migration, migration.Replace('\\', '/'), StringComparison.Ordinal) &&
            violation.Contains(entry.Table, StringComparison.Ordinal));

    /// <summary>
    /// Hand-written migrations only. EF's <c>.Designer.cs</c> files and the model snapshot are
    /// generated model state and carry no SQL.
    /// </summary>
    private static IReadOnlyList<RepositoryFile> Migrations() =>
        [
            .. RepositorySource.Current.CodeFilesUnder(MigrationsRoot)
                .Where(file => !file.RelativePath.EndsWith(".Designer.cs", StringComparison.Ordinal))
                .Where(file => !file.RelativePath.EndsWith("AppDbContextModelSnapshot.cs", StringComparison.Ordinal)),
        ];
}
