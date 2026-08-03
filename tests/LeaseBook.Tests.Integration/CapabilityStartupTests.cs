using System.Net;
using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Capabilities;
using LeaseBook.Web.Endpoints;
using LeaseBook.Web.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
    /// validator that threw on the first would turn one fix into two boot-fix-boot cycles.
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

        await WriteFlagAsync("ghost-capability", ct);
        await WriteFlagAsync("consolidated-statments", ct); // the operator typo this exists to catch

        try
        {
            var error = await Should.ThrowAsync<InvalidOperationException>(
                async () => await CapabilityRegistryValidator.ValidateAsync(host.Services, ct));

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

        await WriteFlagAsync(registered, ct);

        try
        {
            await Should.NotThrowAsync(
                async () => await CapabilityRegistryValidator.ValidateAsync(host.Services, ct));
        }
        finally
        {
            await DeleteFlagAsync(registered, ct);
        }
    }

    /// <summary>
    /// The wiring, not the validator: <c>Program.cs</c> must actually call it, or drift resolves to a
    /// silent default exactly as before. A booting host is the only place that can be observed from.
    /// </summary>
    [Fact]
    public async Task A_host_refuses_to_boot_while_a_ghost_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteFlagAsync("ghost-at-boot", ct);

        try
        {
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
            ct);

    private async Task DeleteFlagAsync(string name, CancellationToken ct) =>
        await UnderPlatformScopeAsync("DELETE FROM feature_flags WHERE name = @name", name, ct);

    private async Task UnderPlatformScopeAsync(string sql, string name, CancellationToken ct)
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

        await tx.CommitAsync(ct);
    }
}
