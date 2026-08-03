using LeaseBook.Modules.Capabilities.Registry;

namespace LeaseBook.Modules.Capabilities.Resolution;

/// <summary>Raw per-capability state read from the three tables, before resolution.</summary>
/// <param name="FlagEnabled">
/// null when no <c>feature_flags</c> row exists. The three-state nature of this field is
/// load-bearing, not incidental: <b>absent</b> and <b>explicitly false</b> mean different things.
/// See <see cref="CapabilityResolver.Resolve"/>.
/// </param>
/// <param name="HasGrant">Latest entitlement row for (org, capability) has granted = true.</param>
/// <param name="CohortMatch">An org-level or (org, user)-level cohort row matches.</param>
public sealed record CapabilityState(bool? FlagEnabled, bool HasGrant, bool CohortMatch);

/// <summary>
/// The resolution order, as pure logic so it is unit-testable without a database (ADR-028).
/// <para>
/// The distinction the order turns on: <b>"off by default"</b> is no <c>feature_flags</c> row at all
/// plus <c>DefaultEnabled: false</c>, whereas <b>"killed"</b> is an explicit <c>enabled = false</c>
/// row. An explicit <c>false</c> always wins — including over a cohort match. Anything less would
/// mean that flipping a money-path kill switch during an incident left the capability live for
/// exactly the cohort most likely to be exercising it, which is the whole reason flags and
/// entitlements are separate sources in the first place.
/// </para>
/// <para>
/// Beta access is unaffected, because a cohort ORs an <i>absent</i> flag row, not a disabled one:
/// the ordinary way to run a beta is to add the cohort and create no flag row.
/// </para>
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

        // 2. An explicit kill beats everything below it, cohorts included. Note this is
        //    `== false`, not `!= true`: a null flag is an ABSENT row, which step 3 may still OR on.
        if (state.FlagEnabled == false)
        {
            return false;
        }

        // 3. Cohorts are an OR over an ABSENT flag row: the beta case. Where there is no
        //    authenticated user (Hangfire, CLI, InvariantSweepRunner) the caller passes
        //    CohortMatch: false for user-level rows — see CapabilityStateReader.
        if (state.CohortMatch)
        {
            return true;
        }

        // 4. Otherwise the flag, defaulting to the registry when no row exists. FlagEnabled can only
        //    be true or null by now, so this reads as "an enabled row, else the registry default".
        return state.FlagEnabled ?? capability.DefaultEnabled;
    }
}
