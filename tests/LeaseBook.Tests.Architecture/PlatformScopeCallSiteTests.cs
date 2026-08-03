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
/// <para>
/// Scope is <c>src/**/*.cs</c> AND <c>infra/**/*.sql</c>. The SQL half is not hypothetical: an
/// <c>ALTER ROLE leasebook_app SET app.platform = 'on'</c> in the bootstrap would open the escape for
/// every pooled connection in production, and no isolation test would go red — the fixture applies
/// the same bootstrap, so those tests would still pass with their own SET LOCAL merely redundant.
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

        // Pinned to the full relative path, not just the file name: an EndsWith on
        // "Tenancy/PlatformScopedExecutor.cs" would exempt a same-named file dropped into any
        // module's Tenancy folder, which is a free bypass of this guard.
        var allowed = Path.Combine("src", "LeaseBook.Web", "Tenancy", "PlatformScopedExecutor.cs");
        var offenders = new List<string>();

        foreach (var file in EnumerateGuardedFiles(repoRoot))
        {
            if (Path.GetRelativePath(repoRoot, file).Equals(allowed, StringComparison.Ordinal))
            {
                continue;
            }

            var marker = Path.GetExtension(file).Equals(".sql", StringComparison.OrdinalIgnoreCase) ? "--" : "//";
            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (SetsPlatformScope.IsMatch(StripLineComment(line, marker)))
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

    /// <summary>
    /// Comments are not call sites. This matters in practice, not in theory: the doc comments on
    /// <c>Rls.cs</c> and the capabilities migration have to spell out <i>why</i> a SET LOCAL of this
    /// GUC inside the request transaction is unsafe, and without this the guard fails on its own
    /// rationale. Only the code left of the line-comment marker is matched, so a commented-out
    /// statement is ignored (it does nothing) while a real one on the same line is still caught.
    /// </summary>
    private static string StripLineComment(string line, string marker)
    {
        var at = line.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    /// <summary>
    /// Application code AND database bootstrap. SQL is in scope because
    /// <c>ALTER ROLE leasebook_app SET app.platform = 'on'</c> in <c>infra/db/bootstrap.sql</c> would
    /// open the escape permanently, for every pooled connection in production — and nothing else in
    /// the suite would notice, because the test fixture applies that same bootstrap, so the isolation
    /// tests would keep passing with their own <c>SET LOCAL</c> merely redundant.
    /// </summary>
    private static IEnumerable<string> EnumerateGuardedFiles(string repoRoot)
    {
        var roots = new (string Dir, string Pattern)[]
        {
            (Path.Combine(repoRoot, "src"), "*.cs"),
            (Path.Combine(repoRoot, "infra"), "*.sql"),
        };

        foreach (var (dir, pattern) in roots)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
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
