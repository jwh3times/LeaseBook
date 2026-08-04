using System.Net;
using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Tests.Integration.Observability;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Endpoints;
using LeaseBook.Web.Health;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The role-seeding half of the readiness gate (ADR-028 §9).
/// <para>
/// <b>What was wrong, and why half a fix was worse than none.</b> Role seeding is this process's first
/// database call and it ran unguarded, so a replica booting against an <i>unreachable</i> Postgres died
/// there — before Kestrel bound, before any probe could observe it, and ACA crash-looped the revision.
/// Guarding it alone would have been worse: the four fixed roles are a precondition for sign-in and for
/// every role-based policy, and the only readiness signal was <c>capability-seam</c> reachability, which
/// says nothing about them. A replica that swallowed the seeding failure would have gone healthy the
/// moment the seam came back and entered rotation unable to authenticate anyone — a silent partial
/// outage in place of a loud crash loop. So the guard and the gate ship together, and the tests below
/// pin both halves plus the direction that actually does the work: readiness going <b>healthy</b> once
/// the roles are genuinely seeded. A gate that fails unconditionally is a gate someone disables.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RoleSeedingReadinessTests(PostgresFixture fixture)
{
    /// <summary>
    /// Direction 1 — <b>condition present, environment absent.</b> Port 1 refuses immediately rather
    /// than hanging; a container that never binds is the failure under test, so the arrangement must
    /// not itself be slow.
    /// </summary>
    private const string Unreachable =
        "Host=127.0.0.1;Port=1;Database=leasebook;Username=leasebook_app;Password=nope;Timeout=1";

    [Fact]
    public async Task An_unreachable_database_binds_the_host_but_holds_it_out_of_rotation()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(Unreachable);

        // The whole premise: this must not throw. Before the guard it did, inside RoleSeeder, one
        // function call before the registry validator that already tolerated the same outage.
        var client = Should.NotThrow(host.CreateClient);

        // Bound and serving liveness — the process is alive, which is what makes "not ready" reachable
        // as a state at all. Liveness deliberately touches no dependency.
        (await client.GetAsync("/api/health", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var readiness = await client.GetAsync(MetaEndpoints.ReadinessPath, ct);
        readiness.StatusCode.ShouldBe(
            HttpStatusCode.ServiceUnavailable,
            "a replica with no roles must take no traffic");

        // The body says WHICH precondition failed. Two independent reasons behind one status code is
        // an operator's problem during a rolling deploy, not a design detail.
        (await readiness.Content.ReadAsStringAsync(ct)).ShouldContain(RoleSeedingReadinessCheck.Name);

        // Which precondition is missing, not just that one is. If role seeding were left out of the
        // readiness predicate this assertion is the one that fails, and it fails on the name.
        var report = await ReadinessReportAsync(host, ct);
        report.Entries[RoleSeedingReadinessCheck.Name].Status.ShouldBe(HealthStatus.Unhealthy);

        host.Services.GetRequiredService<RoleSeedingState>().IsSeeded.ShouldBeFalse(
            "nothing was seeded — the database was never reached");
    }

    /// <summary>
    /// Direction 2 — <b>the condition clears while the environment stays.</b> The retry is the reason
    /// the guard is not just a slower way to be broken: without it a replica that came up during an
    /// outage would stay role-less and not-ready for its whole life, and a crash loop would have been
    /// converted into a permanently useless replica.
    /// </summary>
    [Fact]
    public async Task The_probe_keeps_retrying_and_the_replica_becomes_ready_once_the_database_returns()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new SwitchableApiFactory(Unreachable);
        var client = host.CreateClient();

        var state = host.Services.GetRequiredService<RoleSeedingState>();

        (await client.GetAsync(MetaEndpoints.ReadinessPath, ct)).StatusCode.ShouldBe(
            HttpStatusCode.ServiceUnavailable);
        state.IsSeeded.ShouldBeFalse();

        // At least one background attempt before the database returns, so what follows is a RETRY and
        // not the synchronous boot call succeeding late.
        (await EventuallyAsync(() => state.Attempts >= 1, TimeSpan.FromSeconds(10), ct)).ShouldBeTrue(
            "RoleSeedingProbe must be retrying while the database is unreachable");

        // The database comes back. AddDbContext resolves its options per scope, so the next attempt
        // picks this up without restarting the host — which is the point: no restart is what
        // distinguishes this from the crash loop.
        host.ConnectionString = fixture.AppConnectionString;

        (await EventuallyAsync(() => state.IsSeeded, TimeSpan.FromSeconds(60), ct)).ShouldBeTrue(
            "the probe must finish the seeding the boot could not");

        state.Attempts.ShouldBeGreaterThanOrEqualTo(2, "at least one failure and then the success");

        // The capability seam has to come back too, or readiness stays 503 for the other reason.
        var cache = host.Services.GetRequiredService<CapabilityCache>();
        (await EventuallyAsync(() => cache.IsPopulated, TimeSpan.FromSeconds(60), ct)).ShouldBeTrue();

        var recovered = await EventuallyAsync(
            async () => (await client.GetAsync(MetaEndpoints.ReadinessPath, ct)).StatusCode
                == HttpStatusCode.OK,
            TimeSpan.FromSeconds(30),
            ct);
        recovered.ShouldBeTrue("a replica that has finished seeding must be allowed back into rotation");

        // Not just a flag: the roles this host would authorize against are really there.
        await using var scope = host.Services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in Roles.All)
        {
            (await roleManager.RoleExistsAsync(role)).ShouldBeTrue(role);
        }
    }

    /// <summary>
    /// Direction 3 — <b>the ordinary boot, unchanged.</b> A gate that fails against a healthy
    /// environment is a gate someone disables, and this is also the regression check for the CLI verbs
    /// and the test host: <c>Attempts == 0</c> proves the synchronous call in <c>Program.cs</c> still
    /// does the seeding before <c>app.Run()</c>, so nothing downstream became dependent on a
    /// background race.
    /// </summary>
    [Fact]
    public async Task A_normal_boot_seeds_synchronously_and_reports_ready()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        var client = host.CreateClient();

        var state = host.Services.GetRequiredService<RoleSeedingState>();
        state.IsSeeded.ShouldBeTrue("Program.cs seeds before the host binds when the database is there");
        state.Attempts.ShouldBe(0, "the background probe must find the work done and exit immediately");

        var cache = host.Services.GetRequiredService<CapabilityCache>();
        (await EventuallyAsync(() => cache.IsPopulated, TimeSpan.FromSeconds(15), ct)).ShouldBeTrue();

        (await client.GetAsync(MetaEndpoints.ReadinessPath, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var report = await ReadinessReportAsync(host, ct);
        report.Entries[RoleSeedingReadinessCheck.Name].Status.ShouldBe(HealthStatus.Healthy);
        report.Entries[CapabilityReadinessCheck.Name].Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    /// The trap, stated as a test: a <b>reachable seam is not evidence of seeded roles</b>. On a fully
    /// healthy host, putting only the role state back to cold must still produce 503. If role seeding
    /// were folded into the capability check — or simply left out — this host would report 200 with no
    /// roles, which is the exact silent partial outage the design exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_reachable_seam_does_not_by_itself_make_a_role_less_replica_ready()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(fixture.AppConnectionString);
        var client = host.CreateClient();

        var cache = host.Services.GetRequiredService<CapabilityCache>();
        (await EventuallyAsync(() => cache.IsPopulated, TimeSpan.FromSeconds(15), ct)).ShouldBeTrue();

        var state = host.Services.GetRequiredService<RoleSeedingState>();

        // Safe against the background probe: it exited at boot, having found the roles already seeded.
        state.ResetForTesting();

        (await client.GetAsync(MetaEndpoints.ReadinessPath, ct)).StatusCode.ShouldBe(
            HttpStatusCode.ServiceUnavailable,
            "the capability seam is demonstrably reachable here, and that must not be enough");

        var report = await ReadinessReportAsync(host, ct);
        report.Entries[CapabilityReadinessCheck.Name].Status.ShouldBe(
            HealthStatus.Healthy, "the seam half is fine — only the roles are missing");
        report.Entries[RoleSeedingReadinessCheck.Name].Status.ShouldBe(HealthStatus.Unhealthy);

        state.MarkSeeded();

        (await client.GetAsync(MetaEndpoints.ReadinessPath, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The CLI contract (HH1). The four seeders each call <see cref="RoleSeeder.EnsureRolesAsync"/>
    /// themselves, so <c>seed</c> does not inherit the host's tolerance: an operator pointed at a
    /// database they cannot reach is told so, loudly, instead of being handed a half-seeded org.
    /// </summary>
    [Fact]
    public async Task The_throwing_entry_point_still_throws_on_an_unreachable_database()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var host = new ApiFactory(Unreachable);
        _ = host.CreateClient();

        await Should.ThrowAsync<Exception>(
            async () => await RoleSeeder.EnsureRolesAsync(host.Services, ct));
    }

    /// <summary>
    /// The guard is narrow, proven against a <b>real server-side rejection</b> rather than a
    /// hand-thrown double: a database name that does not exist means the connection reached Postgres
    /// and Postgres said no (SQLSTATE 3D000). That is a deployment defect — the missing-table and
    /// revoked-grant class — and it must still kill the boot, because the answer to it is a fix and a
    /// redeploy, not a replica waiting forever for a recovery that will never come.
    /// <para>
    /// This is the assertion that fails if anyone "simplifies"
    /// <see cref="DatabaseReachability.IsUnreachable"/> by dropping the <c>PostgresException</c>-first
    /// case, at which point every deployment defect turns into a silently not-ready replica.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_server_side_rejection_is_not_mistaken_for_an_outage()
    {
        var ct = TestContext.Current.CancellationToken;

        var missingDatabase = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString)
        {
            Database = "leasebook_no_such_database",
        }.ConnectionString;

        await using var host = new ApiFactory(missingDatabase);

        var captured = new CapturingLoggerProvider();

        // CreateClient is what runs Program's synchronous TryEnsureRolesAsync. It must NOT be
        // swallowed, so the boot fails — exactly as it did before this change.
        var failure = Should.Throw<Exception>(() =>
        {
            var built = host.Services;
            built.GetRequiredService<ILoggerFactory>().AddProvider(captured);
            _ = host.CreateClient();
        });

        Flatten(failure).ShouldContain(
            message => message.Contains("3D000", StringComparison.Ordinal)
                || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase),
            "the Postgres rejection must reach the operator rather than being ridden out");

        // And the same distinction at the unit the two startup paths share.
        var rejection = new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.",
            new DbUpdateException(
                "An error occurred while saving the entity changes.",
                new PostgresException("relation \"asp_net_roles\" does not exist", "ERROR", "ERROR", "42P01")));

        DatabaseReachability.IsUnreachable(rejection).ShouldBeFalse(
            "PostgresException must be checked before its NpgsqlException base, however deeply wrapped");

        await Task.CompletedTask;
    }

    [Fact]
    public void An_outer_connectivity_exception_cannot_hide_an_inner_server_rejection()
    {
        var rejection = new NpgsqlException(
            "The connection failed after Postgres returned an error.",
            new PostgresException(
                "permission denied for table asp_net_roles",
                "ERROR",
                "ERROR",
                "42501"));

        DatabaseReachability.IsUnreachable(rejection).ShouldBeFalse(
            "a server-side rejection anywhere in the chain proves Postgres was reachable and must win " +
            "over an outer NpgsqlException");
    }

    private static async Task<HealthReport> ReadinessReportAsync(
        WebApplicationFactory<Program> host, CancellationToken ct) =>
        await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains(CapabilityReadinessCheck.ReadyTag), ct);

    private static Task<bool> EventuallyAsync(Func<bool> probe, TimeSpan timeout, CancellationToken ct) =>
        EventuallyAsync(() => Task.FromResult(probe()), timeout, ct);

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

            await Task.Delay(200, ct);
        }

        return await probe();
    }

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
    /// <see cref="ApiFactory"/> with a connection string that can be changed after the host has
    /// started — the only way to observe recovery without restarting the process, and restarting would
    /// destroy the very thing under test.
    /// </summary>
    private sealed class SwitchableApiFactory(string initialConnectionString)
        : WebApplicationFactory<Program>
    {
        private volatile string _connectionString = initialConnectionString;

        public string ConnectionString
        {
            get => _connectionString;
            set => _connectionString = value;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<DbContextOptions>();

                // Scoped options (the AddDbContext default), so the lambda runs per scope and reads
                // the current value rather than one captured at startup.
                services.AddDbContext<AppDbContext>(options => options
                    .UseNpgsql(_connectionString, npgsql => npgsql.SetPostgresVersion(18, 0))
                    .UseSnakeCaseNamingConvention());
            });
        }
    }
}
