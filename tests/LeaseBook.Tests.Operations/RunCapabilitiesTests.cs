using LeaseBook.Modules.Operations.Contracts;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// The completeness property, carried across the module hop (ADR-028). <c>CapabilitySet</c> refuses
/// to be built from a partial map and says why: an absent entry read as "off" is indistinguishable
/// from a deployment-wide kill switch and effectively undiagnosable in production. Operations' view
/// of that set has to hold the same line, because it is the one a run strategy actually asks.
/// </summary>
public sealed class RunCapabilitiesTests
{
    private static readonly RunCapabilities Resolved = new(
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["alpha"] = true,
            ["beta"] = false,
            ["gamma"] = true,
        },
        "v1",
        new HashSet<string>(StringComparer.Ordinal) { "beta", "gamma" });

    [Fact]
    public void A_resolved_capability_answers_its_resolved_value()
    {
        Resolved.IsEnabled("alpha").ShouldBeTrue();
        Resolved.IsEnabled("beta").ShouldBeFalse("a resolved 'off' is an answer, not an absence");
    }

    /// <summary>
    /// The whole point. A typo'd, renamed or retired name is a bug in the gate, not a state — and on
    /// a money path the silent reading is the dangerous one: charges quietly do not post, every
    /// downstream figure stays internally consistent, and nothing records that the gate was asked
    /// about a capability that no longer exists.
    /// </summary>
    [Fact]
    public void An_unresolved_name_throws_rather_than_reading_as_off()
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(
            () => Resolved.IsEnabled("consolidated-statementz"));

        ex.ActualValue.ShouldBe("consolidated-statementz");
        ex.Message.ShouldContain("kill switch");
    }

    /// <summary>
    /// Case matters: the registry keys are ordinal, so a near-miss must fail loudly rather than
    /// matching by accident under a culture-aware comparison.
    /// </summary>
    [Fact]
    public void Name_matching_is_ordinal()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Resolved.IsEnabled("Alpha"));
    }

    /// <summary>
    /// What the run summary records. Ordered, so two runs under the same state produce the same
    /// bytes, and enabled-only, so the recorded line reads as a statement of what was on.
    /// </summary>
    [Fact]
    public void Enabled_names_are_the_on_entries_in_ordinal_order()
    {
        Resolved.EnabledNames().ShouldBe(["alpha", "gamma"]);
    }

    /// <summary>
    /// What the cross-run period guard compares. Only the money-path entries, BOTH values, in
    /// ordinal order — a run recording only the "on" ones could not tell "a new money-path gate
    /// exists and resolved off" from "nothing changed", and those are different periods.
    /// </summary>
    [Fact]
    public void Money_path_state_carries_both_values_for_money_path_names_only()
    {
        Resolved.MoneyPathState().ShouldBe(["beta=off", "gamma=on"]);
    }

    /// <summary>
    /// The money-path view is DERIVED from the one resolved map, never carried alongside it, so it
    /// cannot drift from the set the run actually posts under. A name outside the map is the same
    /// bug <see cref="An_unresolved_name_throws_rather_than_reading_as_off"/> pins, and gets the same
    /// loud answer rather than a quiet "off" in the recorded state.
    /// </summary>
    [Fact]
    public void A_money_path_name_outside_the_resolved_map_throws()
    {
        var inconsistent = Resolved with
        {
            MoneyPathNames = new HashSet<string>(StringComparer.Ordinal) { "delta" },
        };

        Should.Throw<ArgumentOutOfRangeException>(() => inconsistent.MoneyPathState());
    }

    /// <summary>
    /// Removal: the recorded state names a capability the registry no longer defines. The cross-run
    /// guard has to tell this from an ordinary flip, because no operator action resurrects a removed
    /// capability — there is no flag left to write — so the rejection must point at deliberate
    /// confirmation rather than at a switch that no longer exists.
    /// </summary>
    [Fact]
    public void A_capability_removed_from_the_registry_is_a_registry_move()
    {
        Resolved.RegistryMoved(["beta=off", "gamma=on", "removed-thing=on"]).ShouldBeTrue();
    }

    /// <summary>
    /// ADDITION, the direction an asymmetric predicate misses — and the commoner deploy. A prior run
    /// recorded <c>["beta=off"]</c>; this release added <c>gamma</c>; the states differ and no
    /// <c>feature_flags</c> write can take <c>gamma</c> off the list, because the list is derived from
    /// the REGISTRY. A one-way "does the recorded state name something unknown" scan answers false
    /// here and hands the operator the one remedy they cannot perform.
    /// </summary>
    [Fact]
    public void A_capability_added_to_the_registry_is_also_a_registry_move()
    {
        Resolved.RegistryMoved(["beta=off"]).ShouldBeTrue();
    }

    /// <summary>
    /// A run recorded before any money-path capability existed: every name in the current registry is
    /// new to it. Same direction as the addition above, and the case the first money-path capability
    /// after this task will actually produce.
    /// </summary>
    [Fact]
    public void An_empty_recorded_state_against_a_non_empty_registry_is_a_registry_move()
    {
        Resolved.RegistryMoved([]).ShouldBeTrue();
    }

    /// <summary>
    /// The ordinary flip is NOT a registry move, whichever way the values went: the same names
    /// resolve on both sides, so the earlier state IS restorable and the message must say so.
    /// </summary>
    [Fact]
    public void The_same_names_with_different_values_are_not_a_registry_move()
    {
        Resolved.RegistryMoved(["beta=on", "gamma=off"]).ShouldBeFalse();
        Resolved.RegistryMoved(["beta=off", "gamma=on"]).ShouldBeFalse();
    }

    /// <summary>
    /// Order is not significance: <see cref="RunCapabilities.MoneyPathState"/> emits ordinal order, but
    /// the recorded state came off another deployment and the predicate is about set membership.
    /// </summary>
    [Fact]
    public void Recorded_order_does_not_make_a_registry_move()
    {
        Resolved.RegistryMoved(["gamma=on", "beta=off"]).ShouldBeFalse();
    }

    /// <summary>
    /// The name is decoded on the LAST separator, the exact inverse of the <c>name=on</c> encoder.
    /// <para>
    /// Unreachable today — registry names are kebab-case and
    /// <c>CapabilityRegistryTests.Names_are_unique_and_kebab_case</c> pins a pattern that excludes
    /// '=', so first-split and last-split agree for every name that exists. Pinned anyway as the
    /// inverse property, so the decoder stays correct if that ever changes. Even then the blast radius
    /// is which of two error MESSAGES renders: the guard compares whole entries with SequenceEqual, so
    /// its decision, the recorded state and every posted amount are the same either way.
    /// </para>
    /// </summary>
    [Fact]
    public void The_recorded_name_is_everything_before_the_last_separator()
    {
        var odd = Resolved with
        {
            MoneyPathNames = new HashSet<string>(StringComparer.Ordinal) { "a=b" },
        };

        odd.RegistryMoved(["a=b=on"]).ShouldBeFalse();
    }
}
