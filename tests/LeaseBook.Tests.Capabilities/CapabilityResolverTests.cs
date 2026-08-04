using LeaseBook.Modules.Capabilities.Registry;
using LeaseBook.Modules.Capabilities.Resolution;
using Shouldly;

namespace LeaseBook.Tests.Capabilities;

/// <summary>
/// The resolution truth table (ADR-028). Entitlement gates FIRST so an ops rollout can never hand
/// out a paid feature; cohorts are an OR over an otherwise-off flag (the beta case).
/// </summary>
public sealed class CapabilityResolverTests
{
    private static readonly Capability Paid = new("paid-thing", RequiresGrant: true, DefaultEnabled: true, IsMoneyPath: false);
    private static readonly Capability Free = new("free-thing", RequiresGrant: false, DefaultEnabled: false, IsMoneyPath: false);

    [Fact]
    public void Missing_grant_beats_an_enabled_flag()
    {
        var state = new CapabilityState(FlagEnabled: true, HasGrant: false, CohortMatch: true);

        CapabilityResolver.Resolve(Paid, state).ShouldBeFalse(
            "entitlement gates before flag — a rollout must never hand out a paid feature");
    }

    [Fact]
    public void Grant_plus_enabled_flag_is_on()
    {
        var state = new CapabilityState(FlagEnabled: true, HasGrant: true, CohortMatch: false);
        CapabilityResolver.Resolve(Paid, state).ShouldBeTrue();
    }

    [Fact]
    public void Grant_with_disabled_flag_is_off()
    {
        var state = new CapabilityState(FlagEnabled: false, HasGrant: true, CohortMatch: false);
        CapabilityResolver.Resolve(Paid, state).ShouldBeFalse("kill switch still applies to paid features");
    }

    /// <summary>
    /// The beta case. "Otherwise off" here means the registry default is false and there is NO
    /// feature_flags row — an ABSENT row, which is what a cohort ORs over.
    /// </summary>
    [Fact]
    public void Cohort_match_turns_on_an_otherwise_off_capability()
    {
        var state = new CapabilityState(FlagEnabled: null, HasGrant: false, CohortMatch: true);
        CapabilityResolver.Resolve(Free, state).ShouldBeTrue("this is the beta case");
    }

    /// <summary>
    /// The incident case, and the reason absent and explicitly-false are not the same value. If a
    /// cohort could out-rank an explicit kill, flipping a money-path switch off mid-incident would
    /// leave the capability live for exactly the cohort most likely to be exercising it.
    /// </summary>
    [Fact]
    public void Explicit_kill_switch_beats_a_cohort_match()
    {
        var state = new CapabilityState(FlagEnabled: false, HasGrant: false, CohortMatch: true);

        CapabilityResolver.Resolve(Free, state).ShouldBeFalse(
            "an explicit enabled = false is a kill switch — a beta cohort must not survive it");

        // And for a granted paid capability, where the gate above does not already short-circuit.
        var granted = new CapabilityState(FlagEnabled: false, HasGrant: true, CohortMatch: true);
        CapabilityResolver.Resolve(Paid, granted).ShouldBeFalse();
    }

    /// <summary>
    /// The gap between "entitlement satisfied" and "flag row exists": a granted org with no flag row
    /// falls all the way through to the registry default, exactly like a free capability does.
    /// </summary>
    [Fact]
    public void Granted_capability_with_no_flag_row_falls_back_to_its_registry_default()
    {
        var state = new CapabilityState(FlagEnabled: null, HasGrant: true, CohortMatch: false);

        CapabilityResolver.Resolve(Paid, state).ShouldBeTrue("Paid is DefaultEnabled: true");
        CapabilityResolver.Resolve(Paid with { DefaultEnabled = false }, state).ShouldBeFalse();
    }

    [Fact]
    public void Absent_flag_row_falls_back_to_registry_default()
    {
        var state = new CapabilityState(FlagEnabled: null, HasGrant: false, CohortMatch: false);

        CapabilityResolver.Resolve(Free, state).ShouldBeFalse();
        CapabilityResolver.Resolve(Free with { DefaultEnabled = true }, state).ShouldBeTrue();
    }

    [Fact]
    public void Requires_grant_capability_is_off_by_default_for_an_ungranted_org()
    {
        // The backfill hazard: making an EXISTING feature RequiresGrant silently removes it from
        // every org that has no grant row. ADR-028 requires a data migration alongside such a change.
        var state = new CapabilityState(FlagEnabled: null, HasGrant: false, CohortMatch: false);
        CapabilityResolver.Resolve(Paid with { DefaultEnabled = true }, state).ShouldBeFalse();
    }
}
