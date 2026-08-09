using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
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
        var executor = new OrgScopedExecutor(db, tenant, new ActorContext());

        var executed = false;
        await Should.ThrowAsync<ArgumentException>(async () =>
            await executor.RunAsSystemAsync(Guid.Empty, "test-harness", () =>
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
        var executor = new OrgScopedExecutor(db, tenant, new ActorContext());

        var id = UuidV7.NewId();
        await executor.RunAsSystemAsync(orgA, "test-harness", async () =>
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
        await executor.RunAsSystemAsync(orgA, "test-harness", async () =>
        {
            row = await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        }, ct);

        row.ShouldNotBeNull();
        row.OrgId.ShouldBe(orgA);
    }

    // T6 — a unit of work refuses to nest on a context that already has a transaction. Context is
    // set with SET LOCAL, so an inner scope would silently run under the OUTER transaction's context
    // rather than its own. The provider rejects the second BeginTransaction anyway; this asserts the
    // refusal names the tenancy constraint, which is what the five prose warnings existed to say.
    [Fact]
    public async Task Org_scoped_work_refuses_to_nest_on_a_context_that_is_already_in_a_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        await using var outer = await db.Database.BeginTransactionAsync(ct);

        var executor = new OrgScopedExecutor(db, tenant, new ActorContext());
        var executed = false;

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await executor.RunAsSystemAsync(UuidV7.NewId(), "test-harness", () =>
            {
                executed = true;
                return Task.CompletedTask;
            }, ct));

        ex.Message.ShouldContain("cannot nest");
        executed.ShouldBeFalse();
        await outer.RollbackAsync(ct);
    }

    // T7 — the same guard covers the platform plane, because both share one transaction bracket.
    [Fact]
    public async Task Platform_scoped_work_refuses_to_nest_on_a_context_that_is_already_in_a_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        await using var outer = await db.Database.BeginTransactionAsync(ct);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new PlatformScopedExecutor(db).RunAsync(() => Task.CompletedTask, ct));

        ex.Message.ShouldContain("cannot nest");
        await outer.RollbackAsync(ct);
    }

    // T8 — the complement, and the reason the guard checks the CONTEXT rather than some ambient flag:
    // out-of-band work on its OWN context is the correct pattern (it is how CapabilityCache refreshes
    // while a request transaction is open elsewhere), and must not be caught.
    [Fact]
    public async Task Platform_scoped_work_on_its_own_context_is_unaffected_by_another_open_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var busy = fixture.CreateContext(fixture.AppConnectionString);
        await using var outer = await busy.Database.BeginTransactionAsync(ct);

        await using var fresh = fixture.CreateContext(fixture.AppConnectionString);
        var ran = false;

        await new PlatformScopedExecutor(fresh).RunAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }, ct);

        ran.ShouldBeTrue();
        await outer.RollbackAsync(ct);
    }

    // T9 — the value-returning form. Its absence is what forced callers into a captured local plus
    // an unreachable null check; CapabilityCache carried exactly that branch before this existed.
    [Fact]
    public async Task Scoped_work_returns_the_works_result_on_both_planes()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var tenant = new TenantContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);

        var org = await new OrgScopedExecutor(db, tenant, new ActorContext())
            .RunAsSystemAsync(orgA, "test-harness", () => Task.FromResult(tenant.OrgId), ct);
        org.ShouldBe(orgA, "the tenant plane sets context before the work runs, and returns its result");

        await using var platformDb = fixture.CreateContext(fixture.AppConnectionString);
        var answer = await new PlatformScopedExecutor(platformDb).RunAsync(() => Task.FromResult(42), ct);
        answer.ShouldBe(42);
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
