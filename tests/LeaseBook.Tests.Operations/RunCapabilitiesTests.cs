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
        "v1");

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
}
