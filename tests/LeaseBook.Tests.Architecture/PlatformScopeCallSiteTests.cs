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
/// Scope is <c>src/**/*.cs</c>, <c>infra/**/*.sql</c> AND <c>tests/**/*.cs</c>. The SQL half is not
/// hypothetical: an <c>ALTER ROLE leasebook_app SET app.platform = 'on'</c> in the bootstrap would
/// open the escape for every pooled connection in production, and no isolation test would go red —
/// the fixture applies the same bootstrap, so those tests would still pass with their own SET LOCAL
/// merely redundant.
/// </para>
/// <para>
/// <b>Tests are in scope because leaving them out did exactly what you would expect.</b> While this
/// guard read only <c>src/</c> and <c>infra/</c>, thirteen independent copies of the
/// <c>set_config</c> statement accumulated across seven test files, and not one of them ever failed
/// a build. Test-side duplication of the escape is not harmless: it is where the property "you
/// cannot open the platform plane without it being visible" is quietly untrue, and it is also how a
/// test stops testing the production seam without anyone noticing. There is now one sanctioned
/// setter per side — <c>PlatformScopedExecutor</c> in production, <c>RlsProbe.SetPlatformAsync</c>
/// for the tests that must be raw.
/// </para>
/// </summary>
public sealed class PlatformScopeCallSiteTests
{
    private static readonly Regex SetsPlatformScope = new(
        @"set_config\s*\(\s*'app\.platform'|\bSET\s+(?:LOCAL\s+)?app\.platform\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void Only_the_sanctioned_setters_touch_the_platform_scope_guc()
    {
        var repoRoot = FindRepoRoot();

        // Pinned to full relative paths, not file names: an EndsWith on
        // "Tenancy/PlatformScopedExecutor.cs" would exempt a same-named file dropped into any
        // module's Tenancy folder, which is a free bypass of this guard.
        //
        // Two entries, one per side. RlsProbe is NOT a second production escape — it sets the GUC on
        // a connection and transaction the calling test already owns, for the cases that have to be
        // raw (no NOTIFY, a statement expected to raise, a migrator-role write, or a context
        // production cannot compose). Everything else goes through the executor.
        //
        // This file is deliberately NOT exempt. Its own vacuity assertions below match the GUC with a
        // regex rather than a substring, so they do not trip this scan — an allow-list entry here
        // would be a free bypass in the one file nobody would think to check.
        string[] allowed =
        [
            Path.Combine("src", "LeaseBook.Web", "Tenancy", "PlatformScopedExecutor.cs"),
            Path.Combine("tests", "LeaseBook.Tests.Common", "RlsProbe.cs"),
        ];
        var offenders = new List<string>();

        foreach (var file in EnumerateGuardedFiles(repoRoot))
        {
            var relative = Path.GetRelativePath(repoRoot, file);
            if (allowed.Contains(relative, StringComparer.Ordinal))
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
            "route platform-plane work through PlatformScopedExecutor in src/, or RlsProbe.SetPlatformAsync " +
            "in tests/ (ADR-028) — a second setter of app.platform makes the cross-org escape unauditable" +
            (offenders.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }

    /// <summary>
    /// The two allowed setters must actually still set it, so a refactor cannot leave the guard above
    /// passing vacuously over files that no longer do the job — the same vacuity risk
    /// <see cref="SweepCapabilityFreezeTests"/> guards for its own subject.
    /// <para>
    /// Matched with a regex rather than a substring on purpose. A literal
    /// <c>"set_config('app.platform', 'on', true)"</c> in this file would make THIS file a setter as
    /// far as the scan above is concerned, forcing an allow-list entry for the very file that defines
    /// the allow-list. The escaped pattern below asserts the same thing — including the
    /// <c>is_local</c> argument — without matching it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("src", "LeaseBook.Web", "Tenancy", "PlatformScopedExecutor.cs")]
    [InlineData("tests", "LeaseBook.Tests.Common", "RlsProbe.cs")]
    public void An_allowed_setter_still_sets_the_guc_transaction_locally(params string[] segments)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), Path.Combine(segments)));

        SetsPlatformScope.IsMatch(source).ShouldBeTrue();

        // The third argument is is_local: a session-level SET would leak the escape onto the pooled
        // connection and disable org isolation for the next request to pick that connection up.
        Regex.IsMatch(source, @"set_config\(\s*'app\.platform'\s*,\s*'on'\s*,\s*true\s*\)")
            .ShouldBeTrue("the escape must be transaction-local — the third argument is is_local");
    }

    /// <summary>
    /// Comments are not call sites. This matters in practice, not in theory: the doc comments on
    /// <c>Rls.cs</c> and the capabilities migration have to spell out <i>why</i> setting this GUC
    /// inside the request transaction is unsafe, and without this the guard fails on its own
    /// rationale.
    /// <para>
    /// Only a WHOLE-LINE comment is skipped, never a trailing one. Cutting at the first marker
    /// anywhere on the line would let a marker inside an earlier string literal blind the guard to
    /// real code after it — <c>Log("https://wiki/x"); db.ExecuteSqlRaw("SET LOCAL app.platform …");</c>
    /// would be silently skipped on the <c>//</c> in the URL. Whole-line matching covers every false
    /// positive actually hit and removes that bypass entirely.
    /// </para>
    /// </summary>
    private static string StripLineComment(string line, string marker) =>
        line.TrimStart().StartsWith(marker, StringComparison.Ordinal) ? "" : line;

    /// <summary>
    /// Application code, database bootstrap, AND tests. SQL is in scope because
    /// <c>ALTER ROLE leasebook_app SET app.platform = 'on'</c> in <c>infra/db/bootstrap.sql</c> would
    /// open the escape permanently, for every pooled connection in production — and nothing else in
    /// the suite would notice, because the test fixture applies that same bootstrap, so the isolation
    /// tests would keep passing with their own <c>SET LOCAL</c> merely redundant.
    /// <para>
    /// Tests are in scope because omitting them let thirteen copies of the statement accumulate
    /// unchallenged. A guard that cannot see half the repository is a guard over the half nobody was
    /// going to break.
    /// </para>
    /// </summary>
    private static IEnumerable<string> EnumerateGuardedFiles(string repoRoot)
    {
        var roots = new (string Dir, string Pattern)[]
        {
            (Path.Combine(repoRoot, "src"), "*.cs"),
            (Path.Combine(repoRoot, "infra"), "*.sql"),
            (Path.Combine(repoRoot, "tests"), "*.cs"),
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
