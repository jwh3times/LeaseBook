using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The permanent tenant-isolation pack (§C.4 / WP-05). Every assertion runs through the RLS-subject
/// <b>app role</b> — never the migrator — so it proves the Postgres policy is the boundary, not the
/// EF query filter (pitfall E2). Each test uses fresh org ids, so the shared container's rows from
/// other tests are simply invisible (which is itself the property under test).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class TenantIsolationTests(PostgresFixture fixture)
{
    // T1 — reads are scoped to the active org context.
    [Fact]
    public async Task Reads_under_an_org_context_see_only_that_orgs_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var orgB = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedAsync(conn, orgA, count: 2, ct);
        await SeedAsync(conn, orgB, count: 1, ct);

        (await CountUnderContextAsync(conn, orgA, ct)).ShouldBe(2);
        (await CountUnderContextAsync(conn, orgB, ct)).ShouldBe(1);
    }

    // T2 — WITH CHECK blocks planting a row in another org while scoped to mine.
    [Fact]
    public async Task Insert_carrying_a_foreign_org_id_is_rejected_by_with_check()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var orgB = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgA, ct);

        var ex = await Should.ThrowAsync<PostgresException>(
            async () => await RlsProbe.InsertEventAsync(conn, tx, orgB, ct));

        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        ex.MessageText.ShouldContain("row-level security");
    }

    // T3 — SET LOCAL is transaction-scoped: it does not leak onto the pooled connection after commit.
    [Fact]
    public async Task Set_local_org_context_does_not_leak_past_the_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);

        // A complete unit of work for org A, committed.
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await RlsProbe.SetOrgAsync(conn, tx, orgA, ct);
            await RlsProbe.InsertEventAsync(conn, tx, orgA, ct);
            await tx.CommitAsync(ct);
        }

        // Same physical connection, no transaction, no context: the setting is gone (no leak) and
        // A's committed row is invisible.
        var leaked = await RlsProbe.CurrentOrgSettingAsync(conn, ct);
        string.IsNullOrEmpty(leaked).ShouldBeTrue($"app.org_id leaked as '{leaked}' after commit");
        (await RlsProbe.CountEventsAsync(conn, tx: null, ct)).ShouldBe(0);
    }

    // T4 — no context → reads return nothing and writes are rejected (fail closed).
    [Fact]
    public async Task With_no_context_reads_are_empty_and_writes_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await fixture.OpenAppConnectionAsync(ct);

        (await RlsProbe.CountEventsAsync(conn, tx: null, ct)).ShouldBe(0);

        var ex = await Should.ThrowAsync<PostgresException>(
            async () => await RlsProbe.InsertEventAsync(conn, tx: null, UuidV7.NewId(), ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        ex.MessageText.ShouldContain("row-level security");
    }

    // T5 — the job/seeder entry point refuses an empty org before any database access.
    [Fact]
    public async Task OrgScopedExecutor_throws_on_empty_org_before_touching_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        var executed = false;
        await Should.ThrowAsync<ArgumentException>(async () =>
            await executor.RunAsync(Guid.Empty, () =>
            {
                executed = true;
                return Task.CompletedTask;
            }, ct));

        executed.ShouldBeFalse();
        tenant.OrgId.ShouldBeNull();
    }

    // Append-only: the app role cannot UPDATE or DELETE audit rows even with a valid context.
    [Fact]
    public async Task App_role_cannot_update_or_delete_audit_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedAsync(conn, orgA, count: 1, ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgA, ct);

        var update = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("UPDATE audit_events SET action = 'tamper'", conn, tx);
            await cmd.ExecuteNonQueryAsync(ct);
        });
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        update.MessageText.ShouldContain("permission denied");
    }

    // The executor end-to-end: opens the unit of work, EF stamps org_id, the row round-trips.
    [Fact]
    public async Task OrgScopedExecutor_sets_context_so_ef_stamps_and_reads_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var tenant = new TenantContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        var id = UuidV7.NewId();
        await executor.RunAsync(orgA, async () =>
        {
            // OrgId left default → stamped to orgA by AppDbContext; WITH CHECK then passes.
            db.AuditEvents.Add(new AuditEvent
            {
                Id = id,
                EntityType = "probe",
                EntityId = UuidV7.NewId(),
                Action = "insert",
                OccurredAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }, ct);

        tenant.OrgId.ShouldBeNull(); // context restored after the scope

        AuditEvent? row = null;
        await executor.RunAsync(orgA, async () =>
        {
            row = await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        }, ct);

        row.ShouldNotBeNull();
        row.OrgId.ShouldBe(orgA);
    }

    private static async Task SeedAsync(NpgsqlConnection conn, Guid orgId, int count, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);
        for (var i = 0; i < count; i++)
        {
            await RlsProbe.InsertEventAsync(conn, tx, orgId, ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<long> CountUnderContextAsync(NpgsqlConnection conn, Guid orgId, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);
        var count = await RlsProbe.CountEventsAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
        return count;
    }
}
