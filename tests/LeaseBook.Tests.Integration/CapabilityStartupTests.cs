using System.Net;
using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Tests.Integration.Observability;
using LeaseBook.Web.Adapters;
using LeaseBook.Web.Capabilities;
using LeaseBook.Web.Endpoints;
using LeaseBook.Web.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The two startup-time halves of the capability seam (ADR-028): the registry validator, which refuses
/// to boot on <c>feature_flags</c> drift, and the readiness gate, which refuses to take traffic before
/// the seam has been proven reachable.
/// <para>
/// <b>Test-isolation hazard.</b> <c>feature_flags</c> is global — no <c>org_id</c> — and this assembly
/// shares one <see cref="PostgresFixture"/> through <see cref="DatabaseCollection"/>. A ghost row left
/// behind would make every host booted by a sibling test throw at startup, so every insert here is
/// undone in a <c>finally</c>. Rows are written under platform scope because
/// <c>feature_flags_platform_write</c> rejects a tenant-plane INSERT with 42501; a plain connection
/// would fail the arrange step, not the assertion. Host configuration is not the lever for any of this
/// — these are database rows, so <c>ApiFactory</c>'s settings dictionary would be the wrong mechanism.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CapabilityStartupTests(PostgresFixture fixture)
{
    /// <summary>
    /// Two ghosts, not one. A rename lands as a pair — new row inserted, old row stranded — and a
    /// validator that reported only the first would turn one fix into two boot-fix-boot cycles.
    /// </summary>
    [Fact]
    public async Task Validation_reports_every_row_that_names_no_registered_capability()
    {
        var ct = TestContext.Current.CancellationToken;

        // The host boots BEFORE the ghosts land: startup validation is wired (see
        // A_host_refuses_to_boot_while_a_ghost_row_exists), so arranging first would fail the arrange
        // step instead of exercising the validator directly.
        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        try
        {
            // Inside the try: if the SECOND insert throws, the first row still has to be cleaned up.
            // feature_flags is global, so a leaked row fails startup for every sibling test that boots
            // a host. Deleting a row that was never inserted is a no-op.
            await WriteFlagAsync("ghost-capability", ct);
            await WriteFlagAsync("consolidated-statments", ct); // the operator typo this exists to catch

            var error = await Should.ThrowAsync<InvalidOperationException>(
                async () => await CapabilityRegistryValidator.ValidateAsync(
                    host.Services, Environment(Environments.Development), ct));

            error.Message.ShouldContain("ghost-capability");

            // Every unknown name, not just the first — one boot failure, one complete fix list.
            error.Message.ShouldContain("consolidated-statments");
        }
        finally
        {
            await DeleteFlagAsync("ghost-capability", ct);
            await DeleteFlagAsync("consolidated-statments", ct);
        }
    }

    /// <summary>
    /// <b>Production logs and boots.</b> An unregistered row is inert — resolution iterates
    /// <c>Capabilities.All</c> and never reads a row the registry does not name — so drift is a signal,
    /// not a correctness hazard. Throwing here would make rollback impossible: deploy N adds a
    /// capability, an operator flips it and creates the row, an unrelated regression forces a rollback
    /// to N-1, and every N-1 replica would refuse to start against a registry that predates the row.
    /// Recovery would be a manual DELETE against production Postgres.
    /// </summary>
    [Fact]
    public async Task Production_logs_the_drift_and_boots_anyway()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        var captured = new CapturingLoggerProvider();
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(captured);

        try
        {
            await WriteFlagAsync("ghost-in-production", ct);

            await Should.NotThrowAsync(async () => await CapabilityRegistryValidator.ValidateAsync(
                host.Services, Environment(Environments.Production), ct));

            // Silent tolerance would be the worst of both worlds: no boot failure AND no signal.
            captured.Entries.ShouldContain(
                entry => entry.Level == LogLevel.Error
                    && entry.Message.Contains("ghost-in-production", StringComparison.Ordinal),
                "tolerating the row must still leave an Error-level record naming it");
        }
        finally
        {
            await DeleteFlagAsync("ghost-in-production", ct);
        }
    }

    /// <summary>
    /// The positive control. Without a real row present this would pass against a validator that
    /// rejected everything, or against an empty table — neither of which proves anything.
    /// </summary>
    [Fact]
    public async Task Validation_accepts_a_row_naming_a_registered_capability()
    {
        var ct = TestContext.Current.CancellationToken;
        var registered = CapabilityCatalog.ConsolidatedStatements.Name;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        try
        {
            await WriteFlagAsync(registered, ct);

            await Should.NotThrowAsync(async () => await CapabilityRegistryValidator.ValidateAsync(
                host.Services, Environment(Environments.Development), ct));
        }
        finally
        {
            await DeleteFlagAsync(registered, ct);
        }
    }

    /// <summary>
    /// The wiring, not the validator: <c>Program.cs</c> must actually call it, or drift resolves to a
    /// silent default exactly as before. A booting host is the only place that can be observed from,
    /// and <see cref="ApiFactory"/> boots as Development — the environment that throws.
    /// </summary>
    [Fact]
    public async Task A_host_refuses_to_boot_while_a_ghost_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;

        try
        {
            await WriteFlagAsync("ghost-at-boot", ct);

            await using var host = new ApiFactory(fixture.AppConnectionString);

            var failure = Should.Throw<Exception>(() => host.CreateClient());

            Flatten(failure).ShouldContain(
                message => message.Contains("ghost-at-boot", StringComparison.Ordinal),
                "startup must fail naming the drifted row — a boot loop with an opaque message is " +
                "barely better than the silent default it replaces");
        }
        finally
        {
            await DeleteFlagAsync("ghost-at-boot", ct);
        }
    }

    /// <summary>
    /// The check in isolation, over a cache instance no probe has touched. Deliberately NOT the
    /// registered singleton: <c>CapabilityReadinessProbe</c> populates that within a second of boot, so
    /// a test that raced it would assert Healthy → Healthy and pass vacuously against a check hard-coded
    /// to return Healthy.
    /// </summary>
    [Fact]
    public async Task Readiness_is_unhealthy_until_the_seam_is_proven_reachable()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        _ = host.CreateClient();

        var cache = new CapabilityCache(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<CapabilityCache>.Instance);
        var check = new CapabilityReadinessCheck(cache);

        cache.IsPopulated.ShouldBeFalse("a cache no probe has run against has proven nothing");

        var before = await check.CheckHealthAsync(new HealthCheckContext(), ct);
        before.Status.ShouldBe(
            HealthStatus.Unhealthy,
            "never serve traffic from an unpopulated seam — a replica booting while Postgres is " +
            "degraded would otherwise silently serve 'everything off' while its siblings served " +
            "correctly, non-deterministically");

        (await cache.ProbeAsync(ct)).ShouldBeTrue();

        var after = await check.CheckHealthAsync(new HealthCheckContext(), ct);
        after.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    /// End to end over HTTP: the registered singleton, the tag filter, and the route. Both states are
    /// observed on one host — 503 first — because a probe endpoint that can only ever answer 200 is not
    /// a gate. <c>ResetForTesting</c> is safe to use here without racing the background probe: that
    /// probe stops at its first success, so once <c>IsPopulated</c> is true nothing will set it again.
    /// </summary>
    [Fact]
    public async Task The_readiness_endpoint_reports_both_states()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        var client = host.CreateClient();
        var cache = host.Services.GetRequiredService<CapabilityCache>();

        var ready = await EventuallyAsync(() => cache.IsPopulated, TimeSpan.FromSeconds(15), ct);
        ready.ShouldBeTrue("the startup probe must reach the seam against a live container");

        (await client.GetAsync(MetaEndpoints.ReadinessPath, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Back to the cold-start state the probe found at boot. The probe has already exited.
        cache.ResetForTesting();

        var coldStart = await client.GetAsync(MetaEndpoints.ReadinessPath, ct);
        coldStart.StatusCode.ShouldBe(
            HttpStatusCode.ServiceUnavailable,
            "the endpoint must be able to fail — 503 is what keeps a cold replica out of rotation");

        (await cache.ProbeAsync(ct)).ShouldBeTrue();

        (await client.GetAsync(MetaEndpoints.ReadinessPath, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<bool> EventuallyAsync(Func<bool> probe, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe())
            {
                return true;
            }

            await Task.Delay(100, ct);
        }

        return probe();
    }

    /// <summary>
    /// The host harness wraps a startup failure (and <c>AggregateException</c> flattening differs by
    /// path), so assert against the whole chain rather than the top frame's message.
    /// </summary>
    private static IEnumerable<string> Flatten(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;

            if (current is AggregateException aggregate)
            {
                foreach (var message in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return message;
                }
            }
        }
    }

    /// <summary>
    /// Platform scope, because <c>feature_flags_platform_write</c> rejects a tenant-plane INSERT with
    /// 42501. Raw SQL rather than the host's executor so the arrange step cannot depend on the host
    /// under test having booted.
    /// </summary>
    private async Task WriteFlagAsync(string name, CancellationToken ct) =>
        await UnderPlatformScopeAsync(
            """
            INSERT INTO feature_flags (name, enabled, updated_at, updated_by)
            VALUES (@name, false, now(), 'startup-test')
            ON CONFLICT (name) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = EXCLUDED.updated_at
            """,
            name,
            notify: false,
            ct);

    /// <summary>
    /// Restores the shared, global flag state. The delete DOES notify, matching
    /// <c>CapabilityGateTests</c> and <c>CapabilityPropagationTests</c>: any host still running in this
    /// collection drops its cached set immediately rather than carrying stale state for up to a TTL
    /// into a sibling test. The names planted here are unregistered and therefore inert, so the blast
    /// radius is nil today — but diverging silently from a deliberately documented cleanup pattern is
    /// how the next helper, planted with a REGISTERED name, ends up missing it.
    /// </summary>
    private async Task DeleteFlagAsync(string name, CancellationToken ct) =>
        await UnderPlatformScopeAsync(
            "DELETE FROM feature_flags WHERE name = @name", name, notify: true, ct);

    private async Task UnderPlatformScopeAsync(
        string sql, string name, bool notify, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var scope = new NpgsqlCommand("SELECT set_config('app.platform', 'on', true)", conn, tx))
        {
            await scope.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (notify)
        {
            // Inside the same transaction: Postgres queues notifications and delivers them after
            // commit, so no listener can be woken before the change it must observe is visible.
            await using var signal = new NpgsqlCommand(
                $"SELECT pg_notify('{CapabilityNotificationListener.Channel}', @name)", conn, tx);
            signal.Parameters.AddWithValue("name", name);
            await signal.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// A minimal <see cref="IHostEnvironment"/>, because the validator branches on the environment and
    /// <see cref="ApiFactory"/> always boots as Development. Booting a real Production host is not an
    /// option: <c>ProductionSecurityGuards.Validate</c> would reject the test configuration first, so
    /// the test would fail for an unrelated reason.
    /// </summary>
    private static IHostEnvironment Environment(string environmentName) =>
        new StubEnvironment { EnvironmentName = environmentName };

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "LeaseBook.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
