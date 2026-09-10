using Hangfire;
using Hangfire.Storage;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Tests.Integration.Observability;
using LeaseBook.Web.Jobs;
using LeaseBook.Web.Observability;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-11: the nightly trust-invariant sweep. Covers the shared core (<see cref="ISweepRunner"/>) on
/// both a clean org and a deliberately corrupted one, and the Hangfire registration the job rides on.
/// <para>
/// Every sweep here names its orgs explicitly rather than running in all-orgs mode. The corrupted
/// fixture below has to be <b>committed</b> to be visible — the sweep reads on its own connection and
/// transaction, so an uncommitted corruption would simply be invisible to it — and a committed
/// violation would make an all-orgs sweep in a sibling test fail for reasons that have nothing to do
/// with that test. Scoping by org id keeps the corruption contained to the one test that wants it.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class InvariantSweepTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Sweep_of_the_seeded_demo_org_is_clean()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var runner = fixture.Api.Services.GetRequiredService<ISweepRunner>();
        var result = await runner.RunAsync([DemoSeeder.DemoOrgId], ct);

        result.OrgsChecked.ShouldBe(1);
        result.IsClean.ShouldBeTrue(
            "the golden demo dataset must satisfy every §C.7 invariant: " +
            string.Join("; ", result.Violations.Select(v => $"{v.Invariant} {v.Detail}")));
    }

    [Fact]
    public async Task Unbalanced_entry_is_reported_and_logged_under_the_alert_event_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await CreateCorruptedOrgAsync(ct);

        // The real class, with a capturing logger in place of the host's — the log entry IS the
        // contract here (Track B's alert rule keys on the event id, not on the return value).
        var logger = new CapturingLogger<InvariantSweepRunner>();
        var runner = new InvariantSweepRunner(fixture.Api.Services, logger);

        var result = await runner.RunAsync([orgId], ct);

        result.IsClean.ShouldBeFalse();
        result.Violations.ShouldAllBe(v => v.OrgId == orgId && v.Invariant == "I1");

        // Twice, not once: the line is basis 'both', so it counts toward the cash and accrual sums
        // alike and breaks each one. Asserting both proves the sweep evaluates the invariant per
        // basis rather than collapsing the two.
        result.Violations.Count.ShouldBe(2);
        result.Violations.ShouldContain(v => v.Detail.Contains("cash"));
        result.Violations.ShouldContain(v => v.Detail.Contains("accrual"));

        logger.Entries.Count.ShouldBe(2);
        logger.Entries.ShouldAllBe(e =>
            e.Level == LogLevel.Error && e.EventId == LogEvents.InvariantViolation);
        logger.Entries.ShouldAllBe(e => e.Message.Contains("I1"));
        logger.Entries.ShouldAllBe(e => e.Message.Contains(orgId.ToString()));
    }

    [Fact]
    public async Task Owner_equity_event_with_no_statement_section_is_reported_as_I8()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, ownerId) = await CreateUncategorizedOwnerEventOrgAsync(ct);

        var logger = new CapturingLogger<InvariantSweepRunner>();
        var runner = new InvariantSweepRunner(fixture.Api.Services, logger);

        var result = await runner.RunAsync([orgId], ct);

        // The entry balances, and its counterpart is neither a trust bank nor a deposit liability, so
        // I1/I2/I4/I7 stay quiet and this asserts I8 alone rather than "something went wrong".
        result.IsClean.ShouldBeFalse();
        var violation = result.Violations.ShouldHaveSingleItem();
        violation.OrgId.ShouldBe(orgId);
        violation.Invariant.ShouldBe("I8");
        violation.Detail.ShouldContain("InterestEarned");

        logger.Entries.ShouldHaveSingleItem();
        logger.Entries[0].EventId.ShouldBe(LogEvents.InvariantViolation);
        logger.Entries[0].Level.ShouldBe(LogLevel.Error);
        logger.Entries[0].Message.ShouldContain("I8");

        // The owner is not named in the violation on purpose — the check is a set difference over
        // event types, so it reports the type once however many owners it touches.
        violation.Detail.ShouldNotContain(ownerId.ToString());
    }

    [Fact]
    public async Task Every_event_type_the_posting_catalog_can_credit_an_owner_with_has_a_section()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        // The scenario org exists to exercise every template and workflow, so if any owner-crediting
        // event were missing from the map this is the fixture that would show it.
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        var runner = fixture.Api.Services.GetRequiredService<ISweepRunner>();
        var result = await runner.RunAsync([DemoSeeder.DemoOrgId, ScenarioSeeder.ScenarioOrgId], ct);

        result.Violations.ShouldNotContain(v => v.Invariant == "I8",
            "an owner-equity event type has no StatementSectionMap entry: "
            + string.Join("; ", result.Violations.Where(v => v.Invariant == "I8").Select(v => v.Detail)));
    }

    [Fact]
    public async Task A_clean_org_logs_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var logger = new CapturingLogger<InvariantSweepRunner>();
        var runner = new InvariantSweepRunner(fixture.Api.Services, logger);

        await runner.RunAsync([DemoSeeder.DemoOrgId], ct);

        // A quiet sweep must stay quiet: an alert rule keyed on InvariantViolation is only
        // trustworthy if the clean path never emits it.
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recurring_job_is_registered_on_the_nightly_cron_when_jobs_are_enabled()
    {
        var ct = TestContext.Current.CancellationToken;

        // A second host, because Jobs:Enabled is read at startup. This also exercises the step-1
        // grants for real: Hangfire installs its own schema here as leasebook_app, which only works
        // because bootstrap.sql pre-creates `hangfire` with that role as owner.
        await using var jobsHost = new ApiFactory(
            fixture.AppConnectionString,
            new Dictionary<string, string?>
            {
                ["Jobs:Enabled"] = "true",
                ["ConnectionStrings:Default"] = fixture.AppConnectionString,
            });

        var storage = jobsHost.Services.GetRequiredService<JobStorage>();
        using var connection = storage.GetConnection();
        var recurring = connection.GetRecurringJobs();

        var sweep = recurring.ShouldHaveSingleItem();
        sweep.Id.ShouldBe(InvariantSweepJob.JobId);
        sweep.Cron.ShouldBe(InvariantSweepJob.CronUtc);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Commits one single-sided journal entry into a throwaway org. Written as raw SQL through the
    /// <b>migrator</b> role on purpose: there is no way to produce this through the domain — the
    /// posting service balances every entry before it persists, and the app role has no UPDATE/DELETE
    /// on the journal. Corrupting the data directly is the only way to prove the sweep would actually
    /// catch a breach rather than always returning clean.
    /// </summary>
    private async Task<Guid> CreateCorruptedOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        var entryId = UuidV7.NewId();

        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Corrupted {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);

        // FORCE ROW LEVEL SECURITY means the journal's policies bind the schema owner too, so even
        // this deliberate corruption has to declare its organization context — there is no role that can write
        // an org-scoped row without one. Transaction-local, exactly as OrgScopedExecutor does it.
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);

        // The line's dimension FK is composite and org-scoped (ADR-013), so the account has to exist
        // in this org before the line can reference it.
        var accountId = UuidV7.NewId();
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO accounts (id, org_id, code, class, name, created_at)
            VALUES (@id, @org, '1200', 'tenant_receivable', 'Tenant Receivable', now())
            """, conn, tx))
        {
            account.Parameters.AddWithValue("id", accountId);
            account.Parameters.AddWithValue("org", orgId);
            await account.ExecuteNonQueryAsync(ct);
        }

        await using (var entry = new NpgsqlCommand(
            """
            INSERT INTO journal_entries
                (id, org_id, entry_date, event_type, posted_at, created_at)
            VALUES (@id, @org, CURRENT_DATE, 'RentCharged', now(), now())
            """, conn, tx))
        {
            entry.Parameters.AddWithValue("id", entryId);
            entry.Parameters.AddWithValue("org", orgId);
            await entry.ExecuteNonQueryAsync(ct);
        }

        // One debit, no matching credit — legal per line (the CHECK wants exactly one side), illegal
        // per entry. tenant_receivable keeps the breach to I1: it is neither a trust bank (I2) nor a
        // deposit liability (I4/I7), so exactly one invariant fires and the assertion stays sharp.
        await using (var line = new NpgsqlCommand(
            """
            INSERT INTO journal_lines
                (id, org_id, entry_id, account_id, account_class, debit, basis, created_at)
            VALUES (@id, @org, @entry, @account, 'tenant_receivable', 100.00, 'both', now())
            """, conn, tx))
        {
            line.Parameters.AddWithValue("id", UuidV7.NewId());
            line.Parameters.AddWithValue("org", orgId);
            line.Parameters.AddWithValue("entry", entryId);
            line.Parameters.AddWithValue("account", accountId);
            await line.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return orgId;
    }

    /// <summary>
    /// Commits a <b>balanced</b> entry whose event_type has no statement section, under an
    /// owner-attributed owner_equity line. Raw SQL through the migrator role because the domain
    /// cannot produce it: <c>AccountingEventService</c> only emits the mapped types, so the only way
    /// to prove I8 can go red is to write the row the catalog will not.
    /// <para>
    /// <c>InterestEarned</c> is the realistic instance rather than a nonsense string: it posts no
    /// owner_equity line today, which is exactly why it is absent from the map, and the interest
    /// entitlement policy that would give it one is deferred under ADR-014. This test is what fires
    /// the day that lands without a map entry.
    /// </para>
    /// </summary>
    private async Task<(Guid OrgId, Guid OwnerId)> CreateUncategorizedOwnerEventOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        var entryId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();

        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Uncategorized {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);

        // journal_lines.owner_id carries a real FK, so the dimension has to exist before the line can
        // reference it — the corruption is the event_type, not a dangling owner.
        await using (var owner = new NpgsqlCommand(
            """
            INSERT INTO owners (id, org_id, name, reserve_amount, is_system, created_at)
            VALUES (@id, @org, 'Uncategorized Owner', 0, false, now())
            """, conn, tx))
        {
            owner.Parameters.AddWithValue("id", ownerId);
            owner.Parameters.AddWithValue("org", orgId);
            await owner.ExecuteNonQueryAsync(ct);
        }

        var equityAccountId = UuidV7.NewId();
        var receivableAccountId = UuidV7.NewId();
        await using (var accounts = new NpgsqlCommand(
            """
            INSERT INTO accounts (id, org_id, code, class, name, created_at) VALUES
                (@equity, @org, 'owner_equity', 'owner_equity', 'Owner Equity', now()),
                (@receivable, @org, '1200', 'tenant_receivable', 'Tenant Receivable', now())
            """, conn, tx))
        {
            accounts.Parameters.AddWithValue("equity", equityAccountId);
            accounts.Parameters.AddWithValue("receivable", receivableAccountId);
            accounts.Parameters.AddWithValue("org", orgId);
            await accounts.ExecuteNonQueryAsync(ct);
        }

        await using (var entry = new NpgsqlCommand(
            """
            INSERT INTO journal_entries (id, org_id, entry_date, event_type, posted_at, created_at)
            VALUES (@id, @org, CURRENT_DATE, 'InterestEarned', now(), now())
            """, conn, tx))
        {
            entry.Parameters.AddWithValue("id", entryId);
            entry.Parameters.AddWithValue("org", orgId);
            await entry.ExecuteNonQueryAsync(ct);
        }

        // Credit owner equity, debit a receivable for the same amount and basis: the entry balances,
        // so I1 has nothing to say and the only thing wrong with this journal is that no statement
        // can categorize it.
        await using (var lines = new NpgsqlCommand(
            """
            INSERT INTO journal_lines
                (id, org_id, entry_id, account_id, account_class, debit, credit, basis, owner_id, created_at)
            VALUES
                (@equityLine, @org, @entry, @equityAccount, 'owner_equity', NULL, 100.00, 'both', @owner, now()),
                (@receivableLine, @org, @entry, @receivableAccount, 'tenant_receivable', 100.00, NULL, 'both', NULL, now())
            """, conn, tx))
        {
            lines.Parameters.AddWithValue("equityLine", UuidV7.NewId());
            lines.Parameters.AddWithValue("receivableLine", UuidV7.NewId());
            lines.Parameters.AddWithValue("org", orgId);
            lines.Parameters.AddWithValue("entry", entryId);
            lines.Parameters.AddWithValue("equityAccount", equityAccountId);
            lines.Parameters.AddWithValue("receivableAccount", receivableAccountId);
            lines.Parameters.AddWithValue("owner", ownerId);
            await lines.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (orgId, ownerId);
    }
}
