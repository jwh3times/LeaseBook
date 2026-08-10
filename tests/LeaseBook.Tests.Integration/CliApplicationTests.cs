using LeaseBook.Web.Cli;
using LeaseBook.Web.Seeding;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// A4: the CLI registry is the process-level interface. These tests pin selection, strict failures,
/// and the shared org vocabulary without booting the web host or touching Postgres.
/// </summary>
public sealed class CliApplicationTests
{
    [Fact]
    public void Exit_codes_have_one_process_wide_vocabulary()
    {
        CliExitCodes.Success.ShouldBe(0);
        CliExitCodes.Failure.ShouldBe(1);
        CliExitCodes.Unavailable.ShouldBe(2);
    }

    [Theory]
    [InlineData("seed", "--org", "demo")]
    [InlineData("check-invariants")]
    [InlineData("capabilities", "list")]
    [InlineData("perf-probe")]
    public void Every_registered_verb_resolves_through_the_one_registry(params string[] args)
    {
        var result = CliApplication.Resolve(args);

        result.IsCli.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Invocation.ShouldNotBeNull();
        result.Invocation.Verb.ShouldBe(args[0]);
    }

    [Fact]
    public void A_non_cli_argument_is_left_to_the_web_host()
    {
        var result = CliApplication.Resolve(["--environment", "Development"]);

        result.IsCli.ShouldBeFalse();
        result.Invocation.ShouldBeNull();
        result.Error.ShouldBeNull();
    }

    [Theory]
    [InlineData("seed", "--org", "laod")]
    [InlineData("check-invariants", "--org", "laod")]
    [InlineData("capabilities", "list", "--org", "laod")]
    public void An_unknown_org_is_a_friendly_parse_failure_on_every_org_aware_verb(params string[] args)
    {
        var result = CliApplication.Resolve(args);

        result.IsCli.ShouldBeTrue();
        result.Invocation.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("laod");
    }

    [Theory]
    [InlineData("demo", SeedTarget.Demo)]
    [InlineData("cutover", SeedTarget.Cutover)]
    [InlineData("load", SeedTarget.Load)]
    [InlineData("scenario", SeedTarget.Scenario)]
    [InlineData("DEMO", SeedTarget.Demo)]
    public void Fixture_aliases_have_one_case_insensitive_vocabulary(string alias, SeedTarget target)
    {
        CliOrg.TryResolveFixture(alias, out var fixture).ShouldBeTrue();

        fixture.SeedTarget.ShouldBe(target);
        CliOrg.TryResolveId(alias, out var orgId).ShouldBeTrue();
        orgId.ShouldBe(fixture.OrgId);
    }

    [Fact]
    public void Check_invariants_resolves_a_guid_or_all_without_throwing()
    {
        var orgId = Guid.NewGuid();

        InvariantSweepVerb.TryResolve(
            ["check-invariants", "--org", orgId.ToString()],
            out var oneOrg,
            out _).ShouldBeTrue();
        oneOrg.ShouldBe([orgId]);

        InvariantSweepVerb.TryResolve(
            ["check-invariants", "--all"],
            out var allOrgs,
            out _).ShouldBeTrue();
        allOrgs.ShouldBeNull();
    }

    [Fact]
    public void Perf_probe_parses_every_typed_option()
    {
        PerfProbeVerb.TryResolve(
            [
                "perf-probe",
                "--base-url", "https://leasebook.test/",
                "--n", "25",
                "--warmup", "0",
                "--budget-ms", "150",
            ],
            out var options,
            out _).ShouldBeTrue();

        options.ShouldBe(new PerfProbeOptions("https://leasebook.test", 25, 0, 150));
    }

    [Theory]
    [InlineData("check-invariants", "--org")]
    [InlineData("perf-probe", "--n")]
    [InlineData("perf-probe", "--budget-ms", "fast")]
    public void Dangling_or_malformed_flags_do_not_silently_default(params string[] args)
    {
        var result = CliApplication.Resolve(args);

        result.IsCli.ShouldBeTrue();
        result.Invocation.ShouldBeNull();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }
}
