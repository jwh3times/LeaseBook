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
    public static async Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<Guid>? orgIds,
        CancellationToken ct)
    {
        var runner = services.GetRequiredService<ISweepRunner>();
        var result = await runner.RunAsync(orgIds, ct);

        if (result.OrgsChecked == 0)
        {
            Console.WriteLine("check-invariants: no orgs to check.");
            return CliExitCodes.Success;
        }

        if (!result.IsClean)
        {
            Console.Error.WriteLine(
                $"check-invariants: {result.Violations.Count} violation(s) across {result.OrgsChecked} org(s):");
            foreach (var violation in result.Violations)
            {
                Console.Error.WriteLine($"  [{violation.OrgId:N}] {violation.Invariant}: {violation.Detail}");
            }

            return CliExitCodes.Failure;
        }

        Console.WriteLine($"check-invariants: all clean across {result.OrgsChecked} org(s).");
        return CliExitCodes.Success;
    }

}

/// <summary>Strict grammar for <c>check-invariants [--org &lt;alias|guid&gt; | --all]</c>.</summary>
internal sealed class InvariantSweepVerb : ICliVerb
{
    public string Name => "check-invariants";

    public bool TryCreateInvocation(string[] args, out CliInvocation invocation, out string error)
    {
        if (!TryResolve(args, out var orgIds, out error))
        {
            invocation = null!;
            return false;
        }

        invocation = new CliInvocation(
            Name,
            (services, ct) => InvariantSweep.RunAsync(services, orgIds, ct));
        return true;
    }

    internal static bool TryResolve(
        string[] args,
        out IReadOnlyList<Guid>? orgIds,
        out string error)
    {
        orgIds = null;
        error = string.Empty;

        if (args.Length == 1)
        {
            return true;
        }

        if (args.Length == 2 && string.Equals(args[1], "--all", StringComparison.Ordinal))
        {
            return true;
        }

        if (args.Length != 3 || !string.Equals(args[1], "--org", StringComparison.Ordinal))
        {
            error =
                "check-invariants: expected `--all` or `--org <id|demo|cutover|load|scenario>`.";
            return false;
        }

        var value = args[2];
        if (!CliOrg.TryResolveId(value, out var orgId))
        {
            error =
                $"check-invariants: --org expects {CliOrg.FixtureNames}, or a GUID, got '{value}'.";
            return false;
        }

        orgIds = [orgId];
        return true;
    }
}
