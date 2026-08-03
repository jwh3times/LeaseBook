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

    [Fact]
    public void Cohort_match_turns_on_an_otherwise_off_flag()
    {
        var state = new CapabilityState(FlagEnabled: false, HasGrant: false, CohortMatch: true);
        CapabilityResolver.Resolve(Free, state).ShouldBeTrue("this is the beta case");
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
