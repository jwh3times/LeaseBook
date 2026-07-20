using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// ADR-025's single-factory rule, enforced: every problem-details response is built by
/// ProblemResults. 28 pre-existing direct call sites are the proof that a doc-comment convention
/// does not hold on its own. Reflection cannot see call sites, so this scans source.
/// </summary>
public sealed class ErrorContractTests
{
    private static readonly Regex Forbidden = new(
        @"\b(?:TypedResults|Results)\.(?:Problem|ValidationProblem)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void Only_ProblemResults_builds_problem_details_responses()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.EndsWith($"Endpoints{Path.DirectorySeparatorChar}ProblemResults.cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (Forbidden.IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{number}: {line.Trim()}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "route these through ProblemResults (ADR-025) — direct Problem/ValidationProblem calls " +
            "ship responses without code/correlationId");
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
