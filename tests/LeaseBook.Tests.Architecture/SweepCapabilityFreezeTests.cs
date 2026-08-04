using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The generalized form of the run-engine freeze (ADR-028, ADR-019 amendment): <b>snapshot at the
/// outermost unit of work.</b> <c>RunEngine.ConfirmAsync</c> takes its capability set as a parameter,
/// so the compiler carries that invariant. <c>InvariantSweepRunner</c> has no such parameter to hang
/// it on — it loops orgs with one scope and one transaction each — so the equivalent guarantee has to
/// be enforced from outside, which is what this file does.
/// <para>
/// The sweep is one logical unit of work spanning many per-org transactions. A capability resolved
/// inside that loop would let a flag flipped between org 3 and org 4 split a single night's sweep
/// across two behaviours, with nothing in the result saying which orgs got which. A doc comment
/// asking future authors not to do that is not enforcement; this is.
/// </para>
/// <para>
/// <b>Scope, stated so it is not mistaken for more than it is.</b> This scans one file. A per-org
/// resolve introduced in the Hangfire job wrapper, in a scope decorator, or in
/// <c>OrgScopedExecutor</c> itself would split a sweep just as effectively and would not be caught
/// here. The gap is narrow rather than open — a rename or move of the scanned file fails this suite
/// red with a <see cref="FileNotFoundException"/> rather than passing vacuously — and widening the
/// scan to every file that could host a per-org loop would be a guess at a list, which is a worse
/// gate than a precise one with its limits written down. Anyone adding a capability read to the
/// sweep's surrounding machinery has to hold the same rule by reading it here.
/// </para>
/// <para>
/// <b>This is a deliberate stop sign, not a permanent ban.</b> When the sweep genuinely needs a
/// capability, the author changes this test in the same commit — and by then has to state which kind
/// of gate it is (a deployment-wide kill switch resolved once before the loop, or a per-org
/// entitlement recorded per org in the result). That is the decision the guard exists to force into
/// the open.
/// </para>
/// </summary>
public sealed class SweepCapabilityFreezeTests
{
    private static readonly string SweepRunner =
        Path.Combine("src", "LeaseBook.Web", "Jobs", "InvariantSweepRunner.cs");

    private static readonly Regex CapabilitySeamMention = new(
        @"Capabilit|Entitlement|feature_flag|IsEnabled\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void The_sweep_resolves_no_capability_inside_its_per_org_loop()
    {
        var repoRoot = FindRepoRoot();
        var offenders = File.ReadLines(Path.Combine(repoRoot, SweepRunner))
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(x => CapabilitySeamMention.IsMatch(StripLineComment(x.Line)))
            .Select(x => $"{SweepRunner}:{x.Number}: {x.Line.Trim()}")
            .ToArray();

        offenders.ShouldBeEmpty(
            "the nightly sweep must not resolve a capability inside its per-org loop — a flip " +
            "between two orgs would split one sweep across two behaviours. Snapshot at the outermost " +
            "unit of work and pass it in explicitly, then update this test deliberately." +
            (offenders.Length == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }

    /// <summary>
    /// Keeps the guard above from passing vacuously. It scans one pinned file for one pattern, so a
    /// rewrite that moved the org loop elsewhere — or renamed the file — would leave it green over
    /// code that no longer does the job, exactly the failure mode
    /// <see cref="PlatformScopeCallSiteTests.The_executor_still_sets_the_guc_transaction_locally"/>
    /// guards against for its own subject.
    /// </summary>
    [Fact]
    public void The_sweep_still_loops_orgs_one_scope_and_transaction_at_a_time()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), SweepRunner));

        source.ShouldContain("foreach (var orgId in targets)");
        source.ShouldContain("services.CreateAsyncScope()");
        source.ShouldContain("executor.RunAsync(orgId");
    }

    /// <summary>
    /// Whole-line comments only, matching <see cref="PlatformScopeCallSiteTests"/>'s rule and
    /// rationale: the file under scan has to explain in prose WHY it resolves no capability, and
    /// cutting at the first marker anywhere on a line would let a marker inside a string literal
    /// blind the scan to real code after it.
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
