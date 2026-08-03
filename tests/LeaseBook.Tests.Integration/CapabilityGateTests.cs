using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The seam every caller uses (ADR-028). Two paths with deliberately different guarantees:
/// <c>ResolveDurableAsync</c> reads inside the <b>ambient</b> transaction for money paths, and
/// <c>Snapshot</c>/<c>IsEnabled</c> are served from the 30-second cache for UI and non-money paths.
/// <para>
/// <b>Every test here runs the resolve inside an already-open transaction</b>, opened by
/// <see cref="OrgScopedExecutor"/> exactly as <c>OrgContextMiddleware</c> opens one for every
/// authenticated request. That is not incidental setup: it is the constraint. A gate that reached for
/// <c>IPlatformScope</c> would call <c>BeginTransactionAsync</c> with one already open and throw, so
/// the nesting is under test in every case below rather than in a separate one.
/// </para>
/// <para>
/// <b>Test-isolation hazard.</b> <c>feature_flags</c> is global — no <c>org_id</c> — and this assembly
/// shares one <see cref="PostgresFixture"/> through <see cref="DatabaseCollection"/>, so every flag
/// mutation here is undone in a <c>finally</c>. The org-scoped half needs no cleanup: each test mints
/// its own org. Host configuration is not the lever for this; flags are database state.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CapabilityGateTests(PostgresFixture fixture)
{
    private static readonly string Capability = CapabilityCatalog.ConsolidatedStatements.Name;

    /// <summary>
    /// <b>The reason this seam has two paths at all.</b> The cache is primed with the old answer and
    /// the flip is written on a connection that issues no <c>NOTIFY</c> — the exact shape of a dead
    /// listener — so nothing can refresh the cached set inside this test. A cache-served resolve
    /// therefore answers "off" here, and only a read on the ambient transaction answers "on".
    /// <para>
    /// The mid-test control assertion is what makes that airtight: it pins that the cache really is
    /// still stale at the moment the durable read succeeds, so the test cannot pass because a refresh
    /// happened to land.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Durable_resolution_sees_a_write_the_cache_has_not_picked_up()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        var org = await SeedOrgAsync(ct);
        await GrantEntitlementAsync(host, org, ct);

        var cache = host.Services.GetRequiredService<CapabilityCache>();

        try
        {
            // Prime with the capability OFF: the entitlement is live but no flag row exists, so the
            // registry default (DefaultEnabled: false) applies. This is the negative control.
            (await cache.GetAsync(org, null, ct))
                .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                .ShouldBeFalse("the cache must hold a resolved 'off' set before the flip");

            await WriteFlagRowWithoutNotifyAsync(enabled: true, ct);

            using var scope = host.Services.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var gate = scope.ServiceProvider.GetRequiredService<ICapabilityGate>();

            await executor.RunAsync(
                org,
                async () =>
                {
                    // Control: the cached entry is neither expired nor invalidated, so the seam's
                    // cache-served path still answers with the old value. Without this the assertion
                    // below would also pass against a gate that simply read through the cache.
                    (await cache.GetAsync(org, null, ct))
                        .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                        .ShouldBeFalse("the cache must still be stale at the moment of the durable read");

                    var durable = await gate.ResolveDurableAsync(ct);

                    durable.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeTrue(
                        "money-path resolution must not be cache-served — a kill switch that waits " +
                        "out a 30s TTL does not work during the incident you flipped it for");
                },
                ct);
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// The inverse pin, and the reason it matters: making the cheap path durable would put a database
    /// round trip on every UI read. <c>Snapshot</c> and <c>IsEnabled</c> must answer from cache — here
    /// with the old value — while <c>ResolveDurableAsync</c>, in the same scope and the same
    /// transaction, answers with the new one.
    /// </summary>
    [Fact]
    public async Task Snapshot_and_IsEnabled_are_cache_served_while_the_durable_read_is_not()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        var org = await SeedOrgAsync(ct);
        await GrantEntitlementAsync(host, org, ct);

        var cache = host.Services.GetRequiredService<CapabilityCache>();

        try
        {
            _ = await cache.GetAsync(org, null, ct);
            await WriteFlagRowWithoutNotifyAsync(enabled: true, ct);

            using var scope = host.Services.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var gate = scope.ServiceProvider.GetRequiredService<ICapabilityGate>();

            await executor.RunAsync(
                org,
                async () =>
                {
                    gate.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeFalse(
                        "IsEnabled is the 30s cache-served path — it must not query the database");
                    gate.Snapshot().IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeFalse(
                        "Snapshot is the same cache-served set, handed out to be frozen by the caller");

                    (await gate.ResolveDurableAsync(ct))
                        .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                        .ShouldBeTrue("the durable path in the same scope must disagree — that is the point");
                },
                ct);
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// Entitlement gates before flag, on the durable path. An ops rollout — a global
    /// <c>feature_flags</c> row — must never hand a paid capability to an org that holds no grant.
    /// </summary>
    [Fact]
    public async Task A_grant_requiring_capability_stays_off_without_a_grant_even_when_the_flag_is_on()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        // Deliberately no entitlement for this org.
        var org = await SeedOrgAsync(ct);

        try
        {
            await WriteFlagRowWithoutNotifyAsync(enabled: true, ct);

            using var scope = host.Services.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var gate = scope.ServiceProvider.GetRequiredService<ICapabilityGate>();

            await executor.RunAsync(
                org,
                async () => (await gate.ResolveDurableAsync(ct))
                    .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                    .ShouldBeFalse("entitlement gates before flag — a rollout must not hand out a paid feature"),
                ct);
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// Makes the nesting constraint an explicit assertion rather than an implicit consequence: the
    /// durable read happens with a transaction already open on the ambient context, which is where
    /// every authenticated request lives. Also pins that the read is strictly consistent with
    /// uncommitted work in that transaction — a grant written here, not yet committed, is visible to
    /// the resolve, which is the property a posting loop depends on.
    /// </summary>
    [Fact]
    public async Task Durable_resolution_runs_on_the_ambient_transaction()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        var org = await SeedOrgAsync(ct);
        await GrantEntitlementAsync(host, org, ct);

        try
        {
            await WriteFlagRowWithoutNotifyAsync(enabled: true, ct);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DbContext>();
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var gate = scope.ServiceProvider.GetRequiredService<ICapabilityGate>();

            await executor.RunAsync(
                org,
                async () =>
                {
                    db.Database.CurrentTransaction.ShouldNotBeNull(
                        "the ambient transaction is the precondition under test");

                    var before = db.Database.CurrentTransaction;
                    var resolved = await gate.ResolveDurableAsync(ct);

                    resolved.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeTrue();
                    db.Database.CurrentTransaction.ShouldBe(
                        before, "the durable read must join the ambient transaction, not open its own");
                },
                ct);
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// Missing org context throws rather than resolving everything to "off". The silent answer is the
    /// dangerous one: with no context RLS filters entitlements to zero rows, every paid capability
    /// reads as unavailable, and it is indistinguishable from a deliberate revoke — the same rule
    /// background jobs follow.
    /// </summary>
    [Fact]
    public async Task Resolving_with_no_org_context_throws_rather_than_answering_off()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        using var scope = host.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<ICapabilityGate>();

        var durable = await Should.ThrowAsync<InvalidOperationException>(
            async () => await gate.ResolveDurableAsync(ct));
        durable.Message.ShouldContain("org context");

        Should.Throw<InvalidOperationException>(() => gate.Snapshot())
            .Message.ShouldContain("org context");
    }

    /// <summary>A fresh org per test, so the entitlement half needs no cleanup at all.</summary>
    private async Task<Guid> SeedOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO orgs (id, name, created_at) VALUES (@id, 'capability-gate', now())", conn);
        cmd.Parameters.AddWithValue("id", orgId);
        await cmd.ExecuteNonQueryAsync(ct);

        return orgId;
    }

    /// <summary>
    /// ConsolidatedStatements is <c>RequiresGrant: true</c>, so without this the resolver
    /// short-circuits to false and no flag flip could ever be observed — the tests would pass
    /// vacuously as "off". Written through the host's real <c>PlatformScopedExecutor</c> via the
    /// module's port, the only platform escape.
    /// </summary>
    private static async Task GrantEntitlementAsync(ApiFactory host, Guid orgId, CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var platform = scope.ServiceProvider.GetRequiredService<IPlatformScope>();
        var id = UuidV7.NewId();

        await platform.RunAsync(
            async () => await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor)
                 VALUES ({id}, {orgId}, {Capability}, true, now(), 'gate-test')
                 """, ct),
            ct);
    }

    /// <summary>
    /// The flip, on a raw connection that emits no notification at all — which is what a dropped
    /// listener looks like from the cache's side, and what keeps the cached set provably stale for the
    /// duration of the test.
    /// </summary>
    private async Task WriteFlagRowWithoutNotifyAsync(bool enabled, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await ExecAsync(conn, tx, "SELECT set_config('app.platform', 'on', true)", ct);
        await ExecAsync(
            conn, tx,
            """
            INSERT INTO feature_flags (name, enabled, updated_at, updated_by)
            VALUES (@name, @enabled, now(), 'gate-test')
            ON CONFLICT (name) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = EXCLUDED.updated_at
            """, ct, ("name", Capability), ("enabled", enabled));

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Restores the shared, global flag state. The delete DOES notify, so any host still running in
    /// this collection drops its cached set immediately rather than carrying a flipped flag for up to
    /// a TTL into a sibling test.
    /// </summary>
    private async Task RemoveFlagAsync(CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await ExecAsync(conn, tx, "SELECT set_config('app.platform', 'on', true)", ct);
        await ExecAsync(conn, tx, "DELETE FROM feature_flags WHERE name = @name", ct, ("name", Capability));
        await ExecAsync(
            conn, tx, $"SELECT pg_notify('{CapabilityNotificationListener.Channel}', @name)", ct,
            ("name", Capability));

        await tx.CommitAsync(ct);
    }

    private static async Task<int> ExecAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
