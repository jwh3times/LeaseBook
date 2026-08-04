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
    /// The retirement case, which the cross-run guard has to tell apart from an ordinary flip. A
    /// capability REMOVED from the registry cannot be restored by any operator action — there is no
    /// flag left to write — so the rejection has to point at deliberate acknowledgement instead of at
    /// a switch that no longer exists.
    /// </summary>
    [Fact]
    public void A_recorded_state_naming_a_retired_capability_is_detected()
    {
        Resolved.NamesRetiredCapability(["beta=off", "retired-thing=on"]).ShouldBeTrue();
    }

    /// <summary>
    /// And the ordinary flip is NOT reported as a retirement, whichever way the value moved: the
    /// names still resolve, only the values differ.
    /// </summary>
    [Fact]
    public void A_recorded_state_naming_only_live_capabilities_is_not_a_retirement()
    {
        Resolved.NamesRetiredCapability(["beta=on", "gamma=off"]).ShouldBeFalse();
        Resolved.NamesRetiredCapability([]).ShouldBeFalse();
    }

    /// <summary>
    /// Decoding is the exact inverse of the encoder, which appends "=on" / "=off": the name is
    /// everything before the LAST separator, because the value can never contain one while a name
    /// conceivably could. Splitting on the first would truncate such a name to a prefix that resolves
    /// nowhere, misreporting every ordinary flip as a retirement and sending operators after an
    /// acknowledgement when a restore was available. This test found exactly that bug.
    /// </summary>
    [Fact]
    public void The_recorded_name_is_everything_before_the_last_separator()
    {
        var odd = Resolved with
        {
            MoneyPathNames = new HashSet<string>(StringComparer.Ordinal) { "a=b" },
        };

        odd.NamesRetiredCapability(["a=b=on"]).ShouldBeFalse();
    }
}
