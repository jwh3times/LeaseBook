using LeaseBook.Web.Seeding;

namespace LeaseBook.Web.Cli;

/// <summary>
/// The one CLI vocabulary for fixture org aliases. Verbs may additionally accept an arbitrary GUID,
/// but an alias always resolves to the same org and seed target everywhere.
/// </summary>
internal static class CliOrg
{
    public const string FixtureNames = "'demo', 'cutover', 'load', or 'scenario'";

    public static bool TryResolveFixture(string value, out FixtureOrg org)
    {
        org = value.ToLowerInvariant() switch
        {
            "demo" => new FixtureOrg(SeedTarget.Demo, DemoSeeder.DemoOrgId),
            "cutover" => new FixtureOrg(SeedTarget.Cutover, CutoverSeeder.CutoverOrgId),
            "load" => new FixtureOrg(SeedTarget.Load, LoadSeeder.LoadOrgId),
            "scenario" => new FixtureOrg(SeedTarget.Scenario, ScenarioSeeder.ScenarioOrgId),
            _ => default,
        };

        return org.OrgId != Guid.Empty;
    }

    public static bool TryResolveId(string value, out Guid orgId)
    {
        if (TryResolveFixture(value, out var fixture))
        {
            orgId = fixture.OrgId;
            return true;
        }

        return Guid.TryParse(value, out orgId);
    }
}

internal readonly record struct FixtureOrg(SeedTarget SeedTarget, Guid OrgId);
