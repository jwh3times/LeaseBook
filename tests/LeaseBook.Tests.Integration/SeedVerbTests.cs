using LeaseBook.Web.Seeding;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-13 step 0: the seed dispatcher is strict — an unknown or missing --org fails instead of
/// silently seeding the demo org (the old `default:` fallback caught typos like `--org laod`).
/// </summary>
public sealed class SeedVerbTests
{
    [Theory]
    [InlineData("demo", SeedTarget.Demo)]
    [InlineData("cutover", SeedTarget.Cutover)]
    [InlineData("load", SeedTarget.Load)]
    [InlineData("DEMO", SeedTarget.Demo)] // case-insensitive, matching the old dispatcher
    public void Known_org_values_resolve(string value, SeedTarget expected)
    {
        SeedVerb.TryResolve(["seed", "--org", value], out var target, out _).ShouldBeTrue();
        target.ShouldBe(expected);
    }

    [Fact]
    public void Unknown_org_value_is_rejected_naming_the_valid_set()
    {
        SeedVerb.TryResolve(["seed", "--org", "laod"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("laod");
        error.ShouldContain("'demo', 'cutover', or 'load'");
    }

    [Fact]
    public void Missing_org_flag_is_rejected_with_usage()
    {
        SeedVerb.TryResolve(["seed"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("--org is required");
    }

    [Fact]
    public void Dangling_org_flag_is_rejected_with_usage()
    {
        SeedVerb.TryResolve(["seed", "--org"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("--org is required");
    }
}
