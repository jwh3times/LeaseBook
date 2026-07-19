using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Web.Cli;

/// <summary>
/// The <c>check-invariants</c> CLI verb body (P33): runs the core correctness invariants (I1/I2/I3/I4)
/// per org inside <see cref="OrgScopedExecutor"/>, printing a violation report and returning a non-zero
/// exit code on any failure. This is the future nightly sweep — the first scheduled job wires Hangfire
/// to call it (ADR-006).
/// </summary>
public static class InvariantSweep
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var orgIds = await ResolveOrgIdsAsync(services, args);
        if (orgIds.Count == 0)
        {
            Console.WriteLine("check-invariants: no orgs to check.");
            return 0;
        }

        var report = new List<string>();
        foreach (var orgId in orgIds)
        {
            await using var scope = services.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var checks = scope.ServiceProvider.GetRequiredService<IInvariantChecks>();

            IReadOnlyList<InvariantViolation> violations = [];
            await executor.RunAsync(orgId, async () => violations = await checks.CheckCoreAsync(CancellationToken.None));
            report.AddRange(violations.Select(v => $"[{orgId:N}] {v.Invariant}: {v.Detail}"));
        }

        if (report.Count > 0)
        {
            Console.Error.WriteLine($"check-invariants: {report.Count} violation(s) across {orgIds.Count} org(s):");
            report.ForEach(line => Console.Error.WriteLine("  " + line));
            return 1;
        }

        Console.WriteLine($"check-invariants: all clean across {orgIds.Count} org(s).");
        return 0;
    }

    // --org demo | --org cutover | --org load | --org <guid> | --all (default).
    private static async Task<IReadOnlyList<Guid>> ResolveOrgIdsAsync(IServiceProvider services, string[] args)
    {
        var orgFlag = Array.IndexOf(args, "--org");
        if (orgFlag >= 0 && orgFlag + 1 < args.Length)
        {
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

            return Guid.TryParse(value, out var id)
                ? [id]
                : throw new ArgumentException($"--org expects 'demo', 'cutover', 'load', or a GUID, got '{value}'.");
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orgs.Select(o => o.Id).ToListAsync();
    }
}
