using System.Text.Json;
using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Modules.Capabilities.Resolution;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Tests.Integration.Support;
using LeaseBook.Web.Adapters;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Capabilities;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The <c>capabilities</c> verb's write surface (ADR-028, Task 12), driven through the real host
/// services so it exercises <c>PlatformScopedExecutor</c> — the single <c>app.platform</c> escape —
/// exactly as the CLI process does.
/// <para>
/// The load-bearing property here is <b>atomicity of the audit trail</b>: every mutation writes one
/// <c>platform_audit_events</c> row in the same transaction as its state change, and a rejected write
/// leaves neither. <c>platform_audit_events</c> is append-only in BOTH planes, so these rows cannot be
/// cleaned up afterwards — assertions are therefore scoped by a per-test <c>since</c> timestamp and,
/// where possible, a freshly minted org id, rather than by counting the table.
/// </para>
/// <para>
/// <c>feature_flags</c> IS global and shared, so every test that writes one deletes it in a
/// <c>finally</c> and issues a <c>pg_notify</c>, matching the sibling helpers: a leaked row for an
/// unregistered name fails startup validation for every sibling host, and a leaked row for a
/// registered one carries flipped state into an unrelated test for up to a cache TTL.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CapabilitiesCommandTests(PostgresFixture fixture)
{
    private const string Capability = "consolidated-statements";

    // ── Flags ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Flag_enable_writes_the_row_and_exactly_one_audit_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var since = await Probe.NowAsync(ct);

        try
        {
            var (exit, output, _) = await RunAsync(["capabilities", "flag", "enable", Capability]);

            exit.ShouldBe(0);
            output.ShouldContain("ENABLED");
            (await Probe.ReadFlagAsync(Capability, ct)).ShouldBe(true);

            var audits = await Probe.ReadAuditsAsync("flag.enable", Capability, since, ct);
            audits.Count.ShouldBe(1, "one verb writes exactly one platform audit row");
            audits[0].Actor.ShouldBe(CapabilitiesCommand.Actor);
            audits[0].OrgId.ShouldBeNull("a flag is deployment-wide, so the audit row names no org");

            var detail = JsonDocument.Parse(audits[0].Detail).RootElement;
            detail.GetProperty("enabled").GetBoolean().ShouldBeTrue();
            detail.GetProperty("previous").ValueKind.ShouldBe(
                JsonValueKind.Null, "there was no row before, which is NOT the same as an explicit kill");
        }
        finally
        {
            await Probe.DeleteFlagAsync(Capability, ct);
        }
    }

    [Fact]
    public async Task Flag_clear_matching_nothing_is_refused_and_records_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Probe.DeleteFlagAsync(Capability, ct);
        var since = await Probe.NowAsync(ct);

        var (exit, _, error) = await RunAsync(["capabilities", "flag", "clear", Capability]);

        exit.ShouldBe(1);
        error.ShouldContain("no explicit flag override");
        (await Probe.ReadAuditsAsync("flag.clear", Capability, since, ct)).ShouldBeEmpty();
    }



    // ── Refusals write nothing at all ───────────────────────────────────────────────────────────

    /// <summary>
    /// W1 end to end. <c>money-path-fixture</c> is IN the registry, so a naive registry check passes
    /// it; the CLI must still refuse. See <c>CapabilitiesVerb.FixtureRefusal</c> for the blast radius.
    /// </summary>
    [Fact]
    public async Task The_money_path_fixture_is_refused_and_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureName = CapabilityCatalog.MoneyPathFixture.Name;
        var since = await Probe.NowAsync(ct);

        var (exit, _, errors) = await RunAsync(["capabilities", "flag", "enable", fixtureName]);

        exit.ShouldBe(1);
        errors.ShouldContain(fixtureName);
        errors.ShouldContain("409");

        (await Probe.ReadFlagAsync(fixtureName, ct)).ShouldBeNull("no feature_flags row may be created");
        (await Probe.ReadAuditsAsync("flag.enable", fixtureName, since, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_capability_is_refused_and_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Typo = "consolidated-statments";

        var (exit, _, errors) = await RunAsync(["capabilities", "flag", "enable", Typo]);

        exit.ShouldBe(1);
        errors.ShouldContain("unknown capability");

        // The whole point: no row is created, so CapabilityRegistryValidator never sees this name.
        (await Probe.ReadFlagAsync(Typo, ct)).ShouldBeNull();
    }

    // ── List ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_prints_every_registry_capability()
    {
        var (exit, output, _) = await RunAsync(["capabilities", "list"]);

        exit.ShouldBe(0);
        foreach (var capability in CapabilityCatalog.All)
        {
            output.ShouldContain(capability.Name);
        }

        output.ShouldContain("ENTITLED");
        output.ShouldContain("COHORTS");
    }

    /// <summary>
    /// The listing must be able to answer the question the entitlement-collision message sends an
    /// operator to ask: did the earlier grant land? A flags-only listing could not, which is why this
    /// asserts on the entitlement and cohort state of a specific org rather than on the header row.
    /// <para>
    /// It also pins that the listing runs under platform scope: <c>entitlements</c> and
    /// <c>capability_cohorts</c> are cross-org readable only under <c>app.platform</c>, and without it
    /// this would print "(no entitlement event)" for a row that exists, with no error anywhere.
    /// </para>
    /// </summary>
    [Fact]
    public async Task List_for_an_org_reports_entitlement_and_cohort_state()
    {
        var org = UuidV7.NewId();
        await Probe.SeedOrgAsync(org, TestContext.Current.CancellationToken);

        (await RunAsync(["capabilities", "grant", Capability, "--org", org.ToString()])).Exit.ShouldBe(0);
        (await RunAsync(["capabilities", "cohort", "add", Capability, "--org", org.ToString()]))
            .Exit.ShouldBe(0);

        var (exit, output, _) = await RunAsync(["capabilities", "list", "--org", org.ToString()]);

        exit.ShouldBe(0);
        output.ShouldContain(org.ToString());
        output.ShouldContain("granted");
        output.ShouldContain("org-wide");
        // The fixture has no entitlement for this org: absence is stated, never left blank.
        output.ShouldContain("(no entitlement event)");
    }

    /// <summary>
    /// The read path validates the org for the same reason the write path gets an FK: this listing is
    /// the remedy the entitlement-collision message names, and a mistyped id would print
    /// "(no entitlement event)" for every capability — indistinguishable from "the grant did not
    /// land", which is exactly the question the operator was sent here to answer. Answering it
    /// confidently and wrongly is how a grant that succeeded gets issued a second time.
    /// </summary>
    [Fact]
    public async Task List_for_an_org_that_does_not_exist_says_so_rather_than_reporting_nothing()
    {
        var ghost = UuidV7.NewId(); // never inserted into orgs

        var (exit, output, errors) = await RunAsync(["capabilities", "list", "--org", ghost.ToString()]);

        exit.ShouldBe(1);
        errors.ShouldContain(ghost.ToString());
        errors.ShouldContain("does not exist");
        output.Contains("(no entitlement event)", StringComparison.Ordinal)
            .ShouldBeFalse("an absent org must never be reported as an org with no entitlements");
    }

    // ── The actor convention ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Without <see cref="CapabilitiesCommand.OperatorVariable"/> the actor is process identity, which
    /// in the ACA job degrades to <c>cli:root@&lt;ephemeral-container-id&gt;</c> — honest, but it
    /// attributes a change to nobody. With it, the accountable party is named AND the process is still
    /// recorded, because "who decided" and "where did it run" are different questions. These rows are
    /// append-only, so the convention has to be right from the first write.
    /// </summary>
    [Fact]
    public void The_actor_names_the_operator_when_one_is_configured()
    {
        var process = CapabilitiesCommand.BuildActor(null);
        process.ShouldStartWith("cli:");

        var attributed = CapabilitiesCommand.BuildActor("  ops-jane  ");
        attributed.ShouldStartWith("operator:ops-jane");
        attributed.Contains(process, StringComparison.Ordinal)
            .ShouldBeTrue("the process identity is kept alongside the operator, never replaced by it");

        CapabilitiesCommand.BuildActor("   ").ShouldBe(process, "a blank value is not an attribution");
    }

    /// <summary>
    /// The convention above is only worth anything if something enforces it. Outside Development a
    /// mutating subcommand with no operator is REFUSED rather than recorded against process identity:
    /// <c>platform_audit_events</c> is append-only in both planes, so a row written with no accountable
    /// party stays that way forever. Refusing costs one re-run.
    /// <para>
    /// <c>list</c> is exempt in every environment, and that exemption is the load-bearing half — an
    /// operator diagnosing an incident must always be able to READ capability state. A guard that could
    /// block the read path is the one way this could make an outage worse.
    /// </para>
    /// </summary>
    [Theory]
    // Development: process identity IS a person (an engineer's shell), so nothing is required.
    [InlineData(CapabilitiesActionKind.FlagDisable, true, null, false)]
    [InlineData(CapabilitiesActionKind.Grant, true, null, false)]
    // Elsewhere: every mutating kind needs a named operator.
    [InlineData(CapabilitiesActionKind.FlagEnable, false, null, true)]
    [InlineData(CapabilitiesActionKind.FlagDisable, false, "", true)]
    [InlineData(CapabilitiesActionKind.FlagClear, false, null, true)]
    [InlineData(CapabilitiesActionKind.FlagDisable, false, "   ", true)]
    [InlineData(CapabilitiesActionKind.Grant, false, null, true)]
    [InlineData(CapabilitiesActionKind.Revoke, false, null, true)]
    [InlineData(CapabilitiesActionKind.CohortAdd, false, null, true)]
    [InlineData(CapabilitiesActionKind.CohortRemove, false, null, true)]
    [InlineData(CapabilitiesActionKind.FlagDisable, false, "ops-jane", false)]
    // Reading is never refused, in any environment, with or without an operator.
    [InlineData(CapabilitiesActionKind.List, false, null, false)]
    [InlineData(CapabilitiesActionKind.List, true, null, false)]
    public void An_unattributed_mutation_is_refused_outside_development(
        CapabilitiesActionKind kind, bool isDevelopment, string? configuredOperator, bool refused)
    {
        var refusal = CapabilitiesCommand.AttributionRefusal(kind, isDevelopment, configuredOperator);

        (refusal is not null).ShouldBe(refused, $"{kind} / dev={isDevelopment} / operator={configuredOperator ?? "(unset)"}");

        if (refusal is not null)
        {
            refusal.ShouldContain(CapabilitiesCommand.OperatorVariable);
            refusal.ShouldContain(
                "nothing was written",
                Case.Sensitive,
                "the operator has to know the state of the system, not just that the command failed");
        }
    }

    /// <summary>
    /// The refusal fires before <see cref="CapabilitiesCommand.RunAsync"/> resolves anything from the
    /// container or opens a transaction. Asserted by driving it through a provider that holds an
    /// <see cref="IHostEnvironment"/> and NOTHING else: if the guard were placed after the scope, this
    /// would throw resolving <c>DbContext</c> instead of returning the message.
    /// <para>
    /// Scoped to this method, not to the process: <c>Program.cs</c> runs <c>RoleSeeder</c> before
    /// dispatching any verb, so a real CLI process has already talked to the database by the time it
    /// gets here. What is pinned is that a refusal performs no write and opens no unit of work.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unattributed_mutation_is_refused_before_any_service_or_database_work()
    {
        var services = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new StubEnvironment { EnvironmentName = Environments.Production })
            .BuildServiceProvider();

        var originalError = Console.Error;
        var errors = new StringWriter();

        try
        {
            Console.SetError(errors);
            var exit = await CapabilitiesCommand.RunAsync(services, ["capabilities", "flag", "disable", Capability]);

            exit.ShouldBe(1);
        }
        finally
        {
            Console.SetError(originalError);
        }

        errors.ToString().ShouldContain(CapabilitiesCommand.OperatorVariable);
        (await Probe.ReadFlagAsync(Capability, TestContext.Current.CancellationToken))
            .ShouldBeNull("a refused command writes nothing at all");
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "LeaseBook.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>
    /// <c>--stale</c> appends the age report (Task 13): one row per registry capability with when it
    /// entered the registry and whether a money-path one has outlived the policy window.
    /// <para>
    /// The assertions are on the SHAPE, not on a date, because age comes from git history and this
    /// suite has to pass in an environment without it — an image build, an exported archive, a shallow
    /// clone. What is pinned is exactly what must hold in both cases: every capability is listed, and
    /// the fixture's exemption is stated on its row rather than left blank. A money-path row with no
    /// verdict at all is indistinguishable from a gate that failed to evaluate it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task List_stale_reports_capability_age_and_states_the_fixture_exemption()
    {
        var (exit, output, _) = await RunAsync(["capabilities", "list", "--stale"]);

        exit.ShouldBe(0);
        output.ShouldContain("capability age");
        output.ShouldContain("INTRODUCED");

        foreach (var capability in CapabilityCatalog.All)
        {
            output.ShouldContain(capability.Name);
        }

        // The permanent money-path fixture must never be reported as either stale or silently fine.
        output.ShouldContain("exempt (Capability.IsFixture)");

        // Whichever branch this environment took, the report must have said which: a staleness report
        // that renders unknown ages as blanks reads as "nothing is stale", the one conclusion it must
        // never invite.
        var stated = output.Contains("UNKNOWN —", StringComparison.Ordinal)
            || output.Contains("no money-path capability is past", StringComparison.Ordinal)
            || output.Contains("past the", StringComparison.Ordinal);
        stated.ShouldBeTrue("the report states either a verdict or why it has none: " + output);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the verb against the real host services, capturing the console the operator would see.
    /// Redirection is safe because every test in <c>DatabaseCollection</c> runs sequentially.
    /// </summary>
    private async Task<(int Exit, string Output, string Errors)> RunAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var errors = new StringWriter();

        try
        {
            Console.SetOut(output);
            Console.SetError(errors);
            var exit = await CapabilitiesCommand.RunAsync(fixture.Api.Services, args);
            return (exit, output.ToString(), errors.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private CapabilityStateProbe Probe => new(fixture);

    // ── Current-entitlement ordering, pinned across the two sites that share it ─────────────────

    /// <summary>
    /// <b>Two places decide which entitlement event is "current", and they must not drift apart.</b>
    /// <c>CapabilityStateReader</c> answers it for resolution — what the product actually does — and
    /// the CLI's per-org listing answers it for an operator diagnosing an incident. Their
    /// <c>WHERE</c> and <c>ORDER BY</c> are byte-identical today and nothing but this test says they
    /// have to stay that way. A divergence would be quiet and nasty: the listing an operator trusts
    /// would disagree with the state the product is enforcing.
    /// </summary>
    /// <remarks>
    /// Pins the two ordering terms that are observable. The <c>granted ASC</c> tie-break is
    /// deliberately NOT pinned — a unique index makes two rows with the same
    /// <c>(org, capability, effective_at)</c> unconstructible, so that tie cannot be built to observe.
    /// Asserting it would mean scanning source text for the clause, which pins the spelling rather
    /// than the behaviour and is the pattern this repository is trying to use less of, not more.
    /// </remarks>
    [Theory]
    // Later effective_at wins, regardless of insertion order or id ordering.
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task The_current_entitlement_is_the_same_row_for_resolution_and_for_the_listing(
        bool olderGrant, bool newerGrant)
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        await Probe.SeedOrgAsync(org, ct);

        var baseline = await Probe.NowAsync(ct);

        // Inserted newest-first so a reader relying on physical order rather than effective_at fails.
        await Probe.SeedEntitlementAsync(
            UuidV7.NewId(), org, Capability, newerGrant, baseline.AddSeconds(-1), ct);
        await Probe.SeedEntitlementAsync(
            UuidV7.NewId(), org, Capability, olderGrant, baseline.AddSeconds(-60), ct);

        var (exit, output, _) = await RunAsync(["capabilities", "list", "--org", org.ToString()]);
        exit.ShouldBe(0);

        var listingSaysGranted = output.Contains("granted", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("revoked", StringComparison.OrdinalIgnoreCase);

        listingSaysGranted.ShouldBe(
            newerGrant,
            "the per-org listing must report the latest event by effective_at as the current state");

        // The flag has to be on for resolution to be a usable oracle here, and the reason is the
        // two-source design itself: a grant does not turn a capability ON, it only clears the
        // entitlement gate at step 1 of the resolution order. This capability is RequiresGrant with
        // DefaultEnabled false, so without a flag it resolves false whether granted or not, and the
        // test would say nothing about ordering. With the flag on, step 1 is the only step that can
        // still say no — which makes IsEnabled track the current entitlement exactly.
        try
        {
            (await RunAsync(["capabilities", "flag", "enable", Capability])).Exit.ShouldBe(0);

            var resolved = await ResolveAsync(org, ct);
            resolved.ShouldBe(
                newerGrant,
                "resolution must agree with the listing — an operator diagnosing an incident reads the " +
                "listing and acts on what the product is actually enforcing");
        }
        finally
        {
            await Probe.DeleteFlagAsync(Capability, ct);
        }
    }

    /// <summary>Resolution's own answer, read through the module rather than through the CLI.</summary>
    private async Task<bool> ResolveAsync(Guid orgId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<CapabilityStateReader>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('app.org_id', {orgId.ToString()}, true)", ct);

        var set = await reader.ReadAsync(orgId, null, ct);
        await tx.RollbackAsync(ct);

        return set.IsEnabled(CapabilityCatalog.ConsolidatedStatements);
    }
}
