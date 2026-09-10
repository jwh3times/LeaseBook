using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// <b>An invariant id names exactly one assertion.</b>
/// <para>
/// The swept migration-clearing check and the harness's basis-convergence test were both called
/// "I5" for several milestones. Nothing broke, because nothing compares the two — which is precisely
/// why it survived. The cost lands on an operator: the violation id is what an alert carries and what
/// they take into the runbook, so one id meaning two things sends them to the wrong entry at the
/// worst moment. It was renumbered to I9 while no alert rule keyed on it yet; this test is what stops
/// the next one being free to reintroduce.
/// </para>
/// <para>
/// <b>The rule is narrower than "an id appears twice", deliberately.</b> A harness test named after a
/// swept id is legitimate and common — <c>Engine_rejects_an_entry_that_would_violate_I1</c> exercises
/// swept invariant I1, one assertion under one id. What went wrong with I5 was different: an id that
/// the contract documents as <i>harness-only</i> was reused by a swept check for an unrelated
/// assertion. So the checkable rule is that the harness-only ids stay unswept, plus swept ids being
/// unique among themselves.
/// </para>
/// <para>
/// <b>Scope, stated so it is not mistaken for more.</b> This reads the string literals passed to
/// <c>InvariantViolation</c> in the Accounting diagnostics. It cannot see an id asserted in a doc
/// table or a dashboard query, and it cannot tell whether two ids mean the same thing semantically —
/// only that a reserved one has been taken. It catches the shape that actually happened.
/// </para>
/// </summary>
public sealed class InvariantIdCollisionTests
{
    private static readonly Regex SweptId =
        new(@"new InvariantViolation\(\s*""(?<id>I\d+)""", RegexOptions.Compiled);

    /// <summary>
    /// Ids reserved for assertions that are relational or conditional and therefore proven in the
    /// test harness rather than swept per-org, per <c>IInvariantChecks</c>: I5 is basis convergence,
    /// I6 is a void and its reversal netting to zero. A swept check must mint a fresh id instead of
    /// taking one of these — which is the mistake that produced two different "I5"s.
    /// </summary>
    private static readonly string[] HarnessOnlyIds = ["I5", "I6"];

    [Fact]
    public void A_swept_check_does_not_take_an_id_reserved_for_the_harness()
    {
        var swept = Ids(SweptId, "src/LeaseBook.Modules.Accounting/Diagnostics");

        // Non-empty, or a regex that silently stopped matching would turn this into a test that
        // passes by finding nothing.
        swept.ShouldNotBeEmpty("no swept invariant ids found — the InvariantViolation scan is broken");

        var taken = HarnessOnlyIds.Where(swept.ContainsKey).OrderBy(id => id).ToList();

        taken.ShouldBeEmpty(
            "a swept check has taken an id reserved for a harness-proven assertion, so one id now "
            + "names two different things: "
            + string.Join("; ", taken.Select(id => $"{id} is emitted by {swept[id]}")));
    }

    [Fact]
    public void No_swept_invariant_id_is_emitted_by_two_different_checks()
    {
        var source = RepositorySource.Current
            .CodeFilesUnder("src/LeaseBook.Modules.Accounting/Diagnostics");

        var byId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in source)
        {
            // The check method an id sits in, so a duplicate names both sides rather than just a line.
            var method = "";
            foreach (var line in file.Lines)
            {
                var declaration = Regex.Match(line.Text, @"public\s+async\s+Task<[^>]+>\s+(?<name>\w+)\(");
                if (declaration.Success)
                {
                    method = declaration.Groups["name"].Value;
                }

                var id = SweptId.Match(line.Text);
                if (id.Success)
                {
                    byId.TryAdd(id.Groups["id"].Value, []);
                    var owners = byId[id.Groups["id"].Value];
                    if (!owners.Contains(method, StringComparer.Ordinal))
                    {
                        owners.Add(method);
                    }
                }
            }
        }

        byId.ShouldNotBeEmpty("no swept invariant ids found — the InvariantViolation scan is broken");

        var shared = byId.Where(pair => pair.Value.Count > 1).ToList();
        shared.ShouldBeEmpty(
            "one swept id is emitted by more than one check: "
            + string.Join("; ", shared.Select(pair => $"{pair.Key} in {string.Join(" and ", pair.Value)}")));
    }

    private static Dictionary<string, string> Ids(Regex pattern, params string[] roots)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepositorySource.Current.CodeFilesUnder(roots))
        {
            foreach (Match match in pattern.Matches(file.Text))
            {
                found.TryAdd(match.Groups["id"].Value, file.RelativePath);
            }
        }

        return found;
    }
}
