using LeaseBook.Web.Cli;

namespace LeaseBook.Web.Seeding;

/// <summary>
/// Parses the `seed` CLI verb's <c>--org</c> argument. Strict by design (WP-13 step 0): an unknown
/// or missing value is an error, never a silent fall-through to the demo org — a typo'd fixture
/// name must fail loudly, not seed the wrong org. Wording mirrors InvariantSweep's --org errors.
/// </summary>
internal sealed class SeedVerb : ICliVerb
{
    public string Name => "seed";

    public const string Usage =
        $"seed: --org is required and expects {CliOrg.FixtureNames} " +
        "(e.g. `dotnet run --project src/LeaseBook.Web -- seed --org demo`).";

    public bool TryCreateInvocation(string[] args, out CliInvocation invocation, out string error)
    {
        if (!TryResolve(args, out var target, out error))
        {
            invocation = null!;
            return false;
        }

        invocation = new CliInvocation(Name, (services, ct) => SeedAsync(services, target, ct));
        return true;
    }

    internal static bool TryResolve(string[] args, out SeedTarget target, out string error)
    {
        target = default;
        error = string.Empty;

        if (args.Length != 3 || !string.Equals(args[1], "--org", StringComparison.Ordinal))
        {
            error = Usage;
            return false;
        }

        var value = args[2];
        if (CliOrg.TryResolveFixture(value, out var org))
        {
            target = org.SeedTarget;
            return true;
        }

        error = $"seed: unknown --org '{value}' — expected {CliOrg.FixtureNames}.";
        return false;
    }

    private static Task<int> SeedAsync(IServiceProvider services, SeedTarget target, CancellationToken ct)
    {
        var seed = target switch
        {
            SeedTarget.Cutover => CutoverSeeder.SeedAsync(services, ct),
            SeedTarget.Load => LoadSeeder.SeedAsync(services, ct),
            SeedTarget.Scenario => ScenarioSeeder.SeedAsync(services, ct),
            _ => DemoSeeder.SeedAsync(services, ct),
        };

        return ExitAfterAsync(seed);
    }

    private static async Task<int> ExitAfterAsync(Task seed)
    {
        await seed;
        return CliExitCodes.Success;
    }
}

/// <summary>The fixture org a `seed` invocation targets.</summary>
public enum SeedTarget
{
    Demo,
    Cutover,
    Load,
    Scenario,
}
