using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// ADR-028's single-escape rule, enforced. <c>app.platform</c> is the GUC that unlocks cross-org
/// reads and every write on the four platform tables, so it must be set in exactly ONE place —
/// <c>PlatformScopedExecutor</c> — and stay greppable there. A doc-comment saying "do not add a
/// second caller" is not enforcement (see <see cref="ErrorContractTests"/> for the same lesson).
/// <para>
/// Only <i>setting</i> the GUC is restricted. <c>Rls.cs</c> reads it with <c>current_setting</c> when
/// it emits the policies, which is the intended and necessary counterpart.
/// </para>
/// </summary>
public sealed class PlatformScopeCallSiteTests
{
    private static readonly Regex SetsPlatformScope = new(
        @"set_config\s*\(\s*'app\.platform'|\bSET\s+(?:LOCAL\s+)?app\.platform\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void Only_PlatformScopedExecutor_sets_the_platform_scope_guc()
    {
        var repoRoot = FindRepoRoot();
        var allowed = Path.Combine("Tenancy", "PlatformScopedExecutor.cs");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.EndsWith(allowed, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (SetsPlatformScope.IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{number}: {line.Trim()}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "route platform-plane work through PlatformScopedExecutor (ADR-028) — a second setter of " +
            "app.platform makes the cross-org escape unauditable" +
            (offenders.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }

    /// <summary>The executor is the one allowed setter — assert it actually still sets it, so a
    /// refactor cannot leave this guard passing vacuously over a file that no longer does the job.</summary>
    [Fact]
    public void The_executor_still_sets_the_guc_transaction_locally()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "LeaseBook.Web", "Tenancy", "PlatformScopedExecutor.cs"));

        SetsPlatformScope.IsMatch(source).ShouldBeTrue();
        // The third argument is is_local: a session-level SET would leak the escape onto the pooled
        // connection and disable org isolation for the next request to pick that connection up.
        source.ShouldContain("set_config('app.platform', 'on', true)");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeaseBook.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("LeaseBook.slnx not found above the test base directory.");
    }
}
