using LeaseBook.Web.Jobs;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Web.Cli;

/// <summary>
/// The <c>check-invariants</c> CLI verb (P33). Owns only the operator-facing surface — argument
/// parsing, the console report, and the exit code. The checks themselves live in
/// <see cref="ISweepRunner"/>, shared with the nightly Hangfire job (WP-11) so the verb an engineer
/// runs by hand and the job that runs in production can never check different things.
/// </summary>
public static class InvariantSweep
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var orgIds = ResolveOrgIds(args);

        var runner = services.GetRequiredService<ISweepRunner>();
        var result = await runner.RunAsync(orgIds, CancellationToken.None);

        if (result.OrgsChecked == 0)
        {
            Console.WriteLine("check-invariants: no orgs to check.");
            return 0;
        }

        if (!result.IsClean)
        {
            Console.Error.WriteLine(
                $"check-invariants: {result.Violations.Count} violation(s) across {result.OrgsChecked} org(s):");
            foreach (var violation in result.Violations)
            {
                Console.Error.WriteLine($"  [{violation.OrgId:N}] {violation.Invariant}: {violation.Detail}");
            }

            return 1;
        }

        Console.WriteLine($"check-invariants: all clean across {result.OrgsChecked} org(s).");
        return 0;
    }

    // --org demo | --org cutover | --org load | --org scenario | --org <guid> | --all (default).
    // Null means "every org" — the same mode the nightly job runs in.
    private static IReadOnlyList<Guid>? ResolveOrgIds(string[] args)
    {
        var orgFlag = Array.IndexOf(args, "--org");
        if (orgFlag < 0 || orgFlag + 1 >= args.Length)
        {
            return null;
        }

        var value = args[orgFlag + 1];
        if (string.Equals(value, "demo", StringComparison.OrdinalIgnoreCase))
        {
            return [DemoSeeder.DemoOrgId];
        }

        if (string.Equals(value, "cutover", StringComparison.OrdinalIgnoreCase))
        {
            return [CutoverSeeder.CutoverOrgId];
        }

        if (string.Equals(value, "load", StringComparison.OrdinalIgnoreCase))
        {
            return [LoadSeeder.LoadOrgId];
        }

        if (string.Equals(value, "scenario", StringComparison.OrdinalIgnoreCase))
        {
            return [ScenarioSeeder.ScenarioOrgId];
        }

        return Guid.TryParse(value, out var id)
            ? [id]
            : throw new ArgumentException(
                $"--org expects 'demo', 'cutover', 'load', 'scenario', or a GUID, got '{value}'.");
    }
}
