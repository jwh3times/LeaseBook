namespace LeaseBook.Web.Seeding;

/// <summary>
/// Parses the `seed` CLI verb's <c>--org</c> argument. Strict by design (WP-13 step 0): an unknown
/// or missing value is an error, never a silent fall-through to the demo org — a typo'd fixture
/// name must fail loudly, not seed the wrong org. Wording mirrors InvariantSweep's --org errors.
/// </summary>
public static class SeedVerb
{
    public const string Usage =
        "seed: --org is required and expects 'demo', 'cutover', 'load', or 'scenario' " +
        "(e.g. `dotnet run --project src/LeaseBook.Web -- seed --org demo`).";

    public static bool TryResolve(string[] args, out SeedTarget target, out string error)
    {
        target = default;
        error = string.Empty;

        var orgFlag = Array.IndexOf(args, "--org");
        if (orgFlag < 0 || orgFlag + 1 >= args.Length)
        {
            error = Usage;
            return false;
        }

        var value = args[orgFlag + 1];
        switch (value.ToLowerInvariant())
        {
            case "demo":
                target = SeedTarget.Demo;
                return true;
            case "cutover":
                target = SeedTarget.Cutover;
                return true;
            case "load":
                target = SeedTarget.Load;
                return true;
            case "scenario":
                target = SeedTarget.Scenario;
                return true;
            default:
                error = $"seed: unknown --org '{value}' — expected 'demo', 'cutover', 'load', or 'scenario'.";
                return false;
        }
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
