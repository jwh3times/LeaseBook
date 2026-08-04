using System.Text.RegularExpressions;
using LeaseBook.Modules.Operations.Runs;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The strategies still DISCARD the capability set (ADR-028 §4). Today every
/// <see cref="IRunStrategy"/> implementation opens <c>ConfirmAsync</c> with <c>_ = capabilities;</c>
/// and never mentions the parameter again — so a capability difference can only prevent a run (both
/// guards reject ABOVE the strategy call), never alter what one produces.
/// <para>
/// <b>Why this is worth a test rather than a comment.</b> That property is the whole distance
/// between "reachability" and "amount", and nothing else asserts it. No reference-graph test can:
/// the violation is a VALUE crossing, not a type crossing — a strategy reading a bool off
/// <c>capabilities</c> and passing a different amount into a business event references no capability
/// type anywhere near Accounting, so <see cref="MoneyPathBoundaryTests"/> and
/// <see cref="ModuleBoundaryTests"/> both stay green. What IS mechanically checkable is the much
/// narrower fact that the parameter is read nowhere, and while that holds it implies the rule.
/// </para>
/// <para>
/// <b>A deliberate stop sign, not a permanent ban</b> — the same shape as
/// <see cref="SweepCapabilityFreezeTests"/>. Whoever first reads a capability inside a strategy
/// deletes this pin in the same commit, and in doing so has to state which of the two they are
/// doing: gating whether a target is posted at all (allowed), or deriving a posted value (forbidden
/// by ADR-028 §4 and by the money rule it rests on). The pin exists to force that sentence to be
/// written, because the difference is invisible in a diff that merely "uses the parameter".
/// </para>
/// <para>
/// <b>Scope, stated so it is not mistaken for more than it is.</b> This checks the ARGUMENT is
/// unread. A strategy that injected a capability port through its constructor, or reached one
/// through another collaborator, would violate the same rule and is not caught here — that route is
/// covered separately by <see cref="IRunStrategy"/>'s "do not inject the snapshot port" instruction,
/// which is prose. The narrow check is kept narrow on purpose: a wider textual scan over strategy
/// files would fire on the explanatory comments the rule itself asks authors to write.
/// </para>
/// </summary>
public sealed class RunStrategyCapabilityFreezeTests
{
    private static readonly string StrategyDirectory =
        Path.Combine("src", "LeaseBook.Modules.Operations", "Runs");

    /// <summary>
    /// The strategies that exist today. Discovery is by reflection so a fourth one is covered the
    /// moment it is written, but these three are named so a discovery that silently returns nothing —
    /// a renamed interface, a moved assembly, a scan that quietly matched zero types — fails instead
    /// of passing over an empty set.
    /// </summary>
    private static readonly string[] KnownStrategies =
        [nameof(RentRunStrategy), nameof(LateFeeRunStrategy), nameof(DisbursementRunStrategy)];

    /// <summary>The parameter name the freeze is written in terms of, in both places it may appear.</summary>
    private static readonly Regex CapabilitiesIdentifier =
        new(@"\bcapabilities\b", RegexOptions.Compiled);

    /// <summary>
    /// The only two lines allowed to name it: the <c>ConfirmAsync</c> parameter declaration, and the
    /// discard. Everything else that mentions the identifier outside a comment is a read.
    /// </summary>
    private static readonly Regex AllowedMention =
        new(@"^(RunCapabilities capabilities,?|_ = capabilities;)$", RegexOptions.Compiled);

    [Fact]
    public void Every_run_strategy_still_discards_the_capability_parameter()
    {
        var failures = new List<string>();

        foreach (var (type, path) in DiscoverStrategySources())
        {
            var code = File.ReadLines(path)
                .Select(StripLineComment)
                .Where(line => CapabilitiesIdentifier.IsMatch(line))
                .Select(line => line.Trim())
                .ToArray();

            if (!code.Contains("_ = capabilities;", StringComparer.Ordinal))
            {
                failures.Add(
                    $"{type.Name}: no `_ = capabilities;` discard. If it now READS the set, delete " +
                    "this test in the same commit and say in the message which kind of gate it is.");
            }

            // Exactly two mentions outside comments: the parameter in the signature, and the discard.
            // Anything else is a read. (A trailing comment naming the parameter after real code would
            // read as one too — the same constraint on future comment style SweepCapabilityFreezeTests
            // documents, and no such line exists in these files.)
            var extra = code.Where(line => !AllowedMention.IsMatch(line)).ToArray();

            if (extra.Length > 0)
            {
                failures.Add($"{type.Name}: reads `capabilities`: {string.Join(" | ", extra)}");
            }
        }

        failures.ShouldBeEmpty(
            "run strategies must not read the capability set. ADR-028 §4 lets a capability decide " +
            "whether a posting path is REACHABLE and nothing else, and today that is guaranteed " +
            "structurally: both capability guards reject above the strategy call, and the strategies " +
            "discard the parameter, so a capability difference cannot move an amount, a date, or any " +
            "other input to a business event. The first read inside a strategy ends that guarantee " +
            "and needs a deliberate decision, not a passing build." + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The vacuity guard. The check above iterates a discovered set; a discovery returning zero types
    /// — or three types whose source files moved out from under a stale directory constant — would
    /// pass over nothing at all, which is the failure mode this branch has hit repeatedly. So the set
    /// is asserted to contain the three known strategies AND every discovered type is asserted to
    /// have a readable source file.
    /// </summary>
    [Fact]
    public void The_scan_finds_every_strategy_source_file()
    {
        var discovered = DiscoverStrategySources();

        foreach (var name in KnownStrategies)
        {
            discovered.Any(x => x.Type.Name == name).ShouldBeTrue(
                $"{name} implements IRunStrategy and must be discovered — if reflection stopped " +
                "finding it, the freeze above scanned a smaller set than it claims to.");
        }

        discovered.Length.ShouldBeGreaterThanOrEqualTo(
            KnownStrategies.Length,
            "the discovered strategy set must never shrink below the three that exist");

        foreach (var (type, path) in discovered)
        {
            File.Exists(path).ShouldBeTrue(
                $"{type.Name} must live at {Path.Combine(StrategyDirectory, type.Name + ".cs")}: the " +
                "freeze above reads the file by that path, so a strategy moved elsewhere would be " +
                "scanned as an empty file and pass without being checked.");
        }
    }

    private static (Type Type, string Path)[] DiscoverStrategySources()
    {
        var repoRoot = FindRepoRoot();

        return typeof(IRunStrategy).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IRunStrategy).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => (t, Path.Combine(repoRoot, StrategyDirectory, t.Name + ".cs")))
            .ToArray();
    }

    /// <summary>
    /// Whole-line comments only, matching <see cref="SweepCapabilityFreezeTests"/>'s rule and
    /// rationale: the strategies are REQUIRED to explain in prose why they discard the parameter, so
    /// their doc comments name it repeatedly and must not count as reads.
    /// </summary>
    private static string StripLineComment(string line) =>
        line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line;

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
