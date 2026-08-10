using LeaseBook.Web.Jobs;
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
/// <b>Scope, stated so it is not mistaken for more than it is.</b> This inspects the compiled
/// <see cref="InvariantSweepRunner"/> type, including its async state machines and string-backed
/// lookups. A per-org resolve introduced in the Hangfire job wrapper, in a scope decorator, or in
/// <c>OrgScopedExecutor</c> itself would split a sweep just as effectively and would not be caught
/// here. Widening the check to every type that could host a per-org loop would be a guess at a list,
/// which is a worse gate than a precise one with its limits written down. Anyone adding a capability
/// read to the sweep's surrounding machinery has to hold the same rule by reading it here.
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
    private const string SweepRunner = "src/LeaseBook.Web/Jobs/InvariantSweepRunner.cs";

    [Fact]
    public void The_sweep_resolves_no_capability_inside_its_per_org_loop()
    {
        var offenders = CapabilityCodeGuard.FindMentions(CompiledCode.In(typeof(InvariantSweepRunner)))
            .Select(reference => reference.ToString())
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
        var source = RepositorySource.Current.File(SweepRunner).Text;

        source.ShouldContain("foreach (var orgId in targets)");
        source.ShouldContain("services.CreateAsyncScope()");

        // Matched loosely on purpose. Pinning the whole call — "executor.RunAsync(orgId" — made this
        // assertion fail when the sweep moved to RunAsSystemAsync, a rename that could not affect the
        // property under test (one scope and one transaction per org). The member name is not the
        // invariant; opening the unit of work per org inside the loop is.
        source.ShouldContain("executor.RunAs");
        source.ShouldContain("(orgId, ");
    }

}
