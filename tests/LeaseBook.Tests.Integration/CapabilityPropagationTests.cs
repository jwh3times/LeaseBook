using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Adapters;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Propagation of a capability change across replicas (ADR-028). Two properties are under test, and
/// they are deliberately separate mechanisms: <c>LISTEN/NOTIFY</c> is a <b>latency optimization</b>,
/// and the 30-second TTL is the <b>correctness floor</b>. A missed notification must still self-heal.
/// <para>
/// <b>Both tests prime the cache before the change.</b> Without a primed, non-stale entry a cold load
/// would read the new state directly and the test would pass against a completely dead listener — a
/// self-healing cache is otherwise indistinguishable from a healthy one. Each test also asserts
/// <i>which</i> counter moved, so the mechanism itself is pinned rather than the outcome alone.
/// </para>
/// <para>
/// <b>Test-isolation hazard.</b> <c>feature_flags</c> is global — no <c>org_id</c> — and this whole
/// assembly shares one <see cref="PostgresFixture"/> through <see cref="DatabaseCollection"/>. A flag
/// left flipped would be visible to every sibling test, so every mutation here is undone in a
/// <c>finally</c>. The org-scoped half (the entitlement) needs no such care: each test mints its own
/// org. Host configuration is NOT the mechanism for this — flags are database state, so
/// <c>ApiFactory</c>'s settings dictionary would be the wrong lever and would pass for the wrong reason.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CapabilityPropagationTests(PostgresFixture fixture)
{
    private static readonly string Capability = CapabilityCatalog.ConsolidatedStatements.Name;

    /// <summary>
    /// The replica-fan-out property: a flip written by one host reaches a host that had already
    /// resolved and cached the old answer, well inside the TTL.
    /// </summary>
    [Fact]
    public async Task A_flip_in_one_host_reaches_a_second_host()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var writer = new ApiFactory(fixture.AppConnectionString);
        await using var reader = new ApiFactory(fixture.AppConnectionString);

        _ = writer.CreateClient();
        _ = reader.CreateClient();

        var org = await SeedOrgAsync(ct);
        await GrantEntitlementAsync(writer, org, ct);

        var readerCache = reader.Services.GetRequiredService<CapabilityCache>();
        var readerListener = reader.Services.GetRequiredService<CapabilityNotificationListener>();

        try
        {
            // The listener issues LISTEN asynchronously at startup. Flipping before it is subscribed
            // would lose the notification and make this test flaky rather than wrong.
            (await EventuallyAsync(() => Task.FromResult(readerListener.IsListening),
                TimeSpan.FromSeconds(15), ct))
                .ShouldBeTrue("the reader host must be subscribed before the flip");

            // Prime, and assert the negative control: no flag row exists, so the registry default
            // (DefaultEnabled: false) applies even though the org holds the entitlement.
            (await readerCache.GetAsync(org, null, ct))
                .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                .ShouldBeFalse("negative control — the reader must hold a resolved 'off' set first");

            var ttlRefreshesBefore = readerCache.TtlRefreshes;

            await FlipFlagAsync(writer, enabled: true, ct);

            var enabled = await EventuallyAsync(
                async () => (await readerCache.GetAsync(org, null, ct))
                    .IsEnabled(CapabilityCatalog.ConsolidatedStatements),
                TimeSpan.FromSeconds(5), ct);

            enabled.ShouldBeTrue("a flip must propagate to other replicas");

            // The poll window is 5s and the TTL is 30s, so a TTL refresh could not have explained it —
            // but assert the mechanism directly instead of trusting that arithmetic.
            readerCache.NotificationRefreshes.ShouldBeGreaterThan(
                0, "the refresh must have been notification-driven");
            readerCache.TtlRefreshes.ShouldBe(
                ttlRefreshesBefore, "no TTL expiry can have fired inside a 5s window against a 30s TTL");
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// The correctness floor: with the notification path dead, the TTL alone still converges. The
    /// write here goes in on a raw connection that never issues <c>NOTIFY</c>, which is exactly what
    /// a dropped listener looks like from the cache's side.
    /// </summary>
    [Fact]
    public async Task A_missed_notification_still_self_heals_within_the_ttl()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        var org = await SeedOrgAsync(ct);
        await GrantEntitlementAsync(host, org, ct);

        var cache = host.Services.GetRequiredService<CapabilityCache>();

        try
        {
            (await cache.GetAsync(org, null, ct))
                .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                .ShouldBeFalse("negative control — prime the cache with a resolved 'off' set");

            var notificationsBefore = cache.NotificationRefreshes;

            await WriteFlagRowWithoutNotifyAsync(ct);

            // Control: nothing has expired and nothing was notified, so the stale set must still be
            // served. Without this the assertion below would also pass on a cache with no TTL at all.
            (await cache.GetAsync(org, null, ct))
                .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                .ShouldBeFalse("an unexpired, unnotified entry must keep serving its cached answer");

            cache.ForceExpireForTesting();

            var refreshed = await cache.GetAsync(org, null, ct);

            refreshed.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeTrue(
                "the TTL is the correctness floor — notify is only latency");
            cache.TtlRefreshes.ShouldBeGreaterThan(0, "expiry, not a notification, must have driven this");
            cache.NotificationRefreshes.ShouldBe(
                notificationsBefore, "no NOTIFY was issued, so the notify counter must not move");
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// The version token carries the preview/confirm concurrency check (Task 10), which compares it
    /// <i>across hosts</i>. A timestamp- or counter-derived version would differ between two replicas
    /// that resolved identical sets, spuriously rejecting every cross-instance confirm.
    /// </summary>
    [Fact]
    public async Task Two_hosts_resolving_the_same_state_agree_on_the_version()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var first = new ApiFactory(fixture.AppConnectionString);
        await using var second = new ApiFactory(fixture.AppConnectionString);

        _ = first.CreateClient();
        _ = second.CreateClient();

        var org = await SeedOrgAsync(ct);
        await GrantEntitlementAsync(first, org, ct);

        var here = await first.Services.GetRequiredService<CapabilityCache>().GetAsync(org, null, ct);
        var there = await second.Services.GetRequiredService<CapabilityCache>().GetAsync(org, null, ct);

        there.Version.ShouldBe(here.Version);
    }

    private static async Task<bool> EventuallyAsync(
        Func<Task<bool>> probe, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return true;
            }

            await Task.Delay(100, ct);
        }

        return false;
    }

    /// <summary>A fresh org per test, so the entitlement half needs no cleanup at all.</summary>
    private async Task<Guid> SeedOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO orgs (id, name, created_at) VALUES (@id, 'capability-propagation', now())", conn);
        cmd.Parameters.AddWithValue("id", orgId);
        await cmd.ExecuteNonQueryAsync(ct);

        return orgId;
    }

    /// <summary>
    /// ConsolidatedStatements is <c>RequiresGrant: true</c>, so without this the resolver short-circuits
    /// to false and no flag flip could ever be observed — the tests would pass vacuously as "off".
    /// Written through the host's real <see cref="PlatformScopedExecutor"/>, the only platform escape.
    /// </summary>
    private static async Task GrantEntitlementAsync(ApiFactory host, Guid orgId, CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<PlatformScopedExecutor>();
        var id = UuidV7.NewId();

        await executor.RunAsync(
            async () => await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor)
                 VALUES ({id}, {orgId}, {Capability}, true, now(), 'propagation-test')
                 """, ct),
            ct);
    }

    /// <summary>
    /// The writer side, shaped the way Task 12's CLI must write: the <c>NOTIFY</c> is issued INSIDE
    /// the same transaction as the row change. Postgres queues notifications and delivers them after
    /// commit, so a reader can never be woken before the row it has to read is visible. Issued outside
    /// the transaction, that ordering guarantee is gone and the race is back.
    /// </summary>
    private static async Task FlipFlagAsync(ApiFactory host, bool enabled, CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<PlatformScopedExecutor>();

        await executor.RunAsync(
            async () =>
            {
                await db.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO feature_flags (name, enabled, updated_at, updated_by)
                     VALUES ({Capability}, {enabled}, now(), 'propagation-test')
                     ON CONFLICT (name) DO UPDATE SET
                       enabled = EXCLUDED.enabled,
                       updated_at = EXCLUDED.updated_at,
                       updated_by = EXCLUDED.updated_by
                     """, ct);

                await db.Database.ExecuteSqlAsync(
                    $"SELECT pg_notify({CapabilityNotificationListener.Channel}, {Capability})", ct);
            },
            ct);
    }

    /// <summary>
    /// Simulates a dropped listener: the row lands, but on a connection that emits no notification at
    /// all. Raw SQL rather than the host's executor precisely so no code path can slip a NOTIFY in.
    /// </summary>
    private async Task WriteFlagRowWithoutNotifyAsync(CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await ExecAsync(conn, tx, "SELECT set_config('app.platform', 'on', true)", ct);
        await ExecAsync(
            conn, tx,
            """
            INSERT INTO feature_flags (name, enabled, updated_at, updated_by)
            VALUES (@name, true, now(), 'propagation-test')
            ON CONFLICT (name) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = EXCLUDED.updated_at
            """, ct, ("name", Capability));

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Restores the shared, global flag state. The delete DOES notify, so any other host in this run
    /// that happens to hold a cached set drops it immediately rather than carrying a flipped flag for
    /// up to a TTL into a sibling test.
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
