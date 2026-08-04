using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Capabilities.Registry;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Capabilities;

/// <summary>
/// The construction contract for a frozen capability set (ADR-028). The interesting property is not
/// lookup — it is that a malformed set is rejected at the boundary instead of behaving like a
/// deliberate configuration.
/// </summary>
public sealed class CapabilitySetTests
{
    private static Dictionary<string, bool> Complete(bool value = true) =>
        CapabilityCatalog.All.ToDictionary(c => c.Name, _ => value, StringComparer.Ordinal);

    [Fact]
    public void Round_trips_a_complete_set()
    {
        var set = CapabilitySet.From(Complete(), "v1");

        set.Version.ShouldBe("v1");
        set.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeTrue();
    }

    [Fact]
    public void Disabled_capability_reads_as_off()
    {
        var set = CapabilitySet.From(Complete(value: false), "v1");

        set.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeFalse();
    }

    /// <summary>
    /// The silent-outage case. IsEnabled answers false for anything absent, so a partial set does
    /// not fail — it turns a DefaultEnabled: true money path off for every org on the host and looks
    /// exactly like an intentional kill switch. Rejecting at construction is the only cheap moment.
    /// </summary>
    [Fact]
    public void Rejects_a_set_that_is_missing_a_registry_capability()
    {
        var partial = Complete();
        partial.Remove(CapabilityCatalog.ConsolidatedStatements.Name);

        var ex = Should.Throw<ArgumentException>(() => CapabilitySet.From(partial, "v1"));

        ex.Message.ShouldContain(CapabilityCatalog.ConsolidatedStatements.Name);
    }

    /// <summary>
    /// The other direction is tolerated on purpose: a leftover feature_flags row for a capability
    /// that has since been deleted from the registry must not take a running host down.
    /// </summary>
    [Fact]
    public void Tolerates_an_unknown_capability_name()
    {
        var extra = Complete();
        extra["retired-capability"] = true;

        var set = CapabilitySet.From(extra, "v1");

        set.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeTrue();
    }

    /// <summary>
    /// Version carries the preview/confirm concurrency check, so an empty one is not a harmless
    /// placeholder — it would compare equal across two genuinely different sets.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_missing_version(string? version)
    {
        Should.Throw<ArgumentException>(() => CapabilitySet.From(Complete(), version!));
    }

    [Fact]
    public void Rejects_a_null_dictionary()
    {
        Should.Throw<ArgumentNullException>(() => CapabilitySet.From(null!, "v1"));
    }

    [Fact]
    public void IsEnabled_rejects_a_null_capability()
    {
        var set = CapabilitySet.From(Complete(), "v1");

        Should.Throw<ArgumentNullException>(() => set.IsEnabled(null!));
    }

    /// <summary>
    /// An unknown Capability instance (not from the registry) reads as off rather than throwing —
    /// the completeness check at construction is what guarantees registry entries are all present,
    /// so a miss here can only mean a hand-built Capability, and false is the safe answer.
    /// </summary>
    [Fact]
    public void Unknown_capability_reads_as_off()
    {
        var set = CapabilitySet.From(Complete(), "v1");
        var stranger = new Capability("not-in-the-set", RequiresGrant: false, DefaultEnabled: true, IsMoneyPath: false);

        set.IsEnabled(stranger).ShouldBeFalse();
    }
}
