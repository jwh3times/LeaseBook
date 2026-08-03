using LeaseBook.Modules.Capabilities.Registry;

namespace LeaseBook.Modules.Capabilities.Resolution;

/// <summary>Raw per-capability state read from the three tables, before resolution.</summary>
/// <param name="FlagEnabled">null when no feature_flags row exists.</param>
/// <param name="HasGrant">Latest entitlement row for (org, capability) has granted = true.</param>
/// <param name="CohortMatch">An org-level or (org, user)-level cohort row matches.</param>
public sealed record CapabilityState(bool? FlagEnabled, bool HasGrant, bool CohortMatch);

/// <summary>
/// The resolution order, as pure logic so it is unit-testable without a database (ADR-028).
/// </summary>
public static class CapabilityResolver
{
    public static bool Resolve(Capability capability, CapabilityState state)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(state);

        // 1. Entitlement gates first. A capability the org is not entitled to is unavailable
        //    regardless of flag or cohort state — this is what makes it structurally impossible for
        //    an ops rollout to hand out a paid feature.
        if (capability.RequiresGrant && !state.HasGrant)
        {
            return false;
        }

        // 2. Cohorts are an OR over an otherwise-off flag: the beta case. Where there is no
        //    authenticated user (Hangfire, CLI, InvariantSweepRunner) the caller passes
        //    CohortMatch: false for user-level rows — see CapabilityStateReader.
        if (state.CohortMatch)
        {
            return true;
        }

        // 3. Otherwise the flag, defaulting to the registry when no row exists.
        return state.FlagEnabled ?? capability.DefaultEnabled;
    }
}
