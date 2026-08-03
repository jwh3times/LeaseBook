using LeaseBook.Modules.Capabilities.Registry;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Capabilities;

public sealed class CapabilityRegistryTests
{
    [Fact]
    public void Names_are_unique_and_kebab_case()
    {
        var names = CapabilityCatalog.All.Select(c => c.Name).ToArray();

        names.ShouldBeUnique();
        names.ShouldAllBe(n => n == n.ToLowerInvariant() && !n.Contains(' '));
    }

    [Fact]
    public void TryGet_resolves_a_known_name()
    {
        CapabilityCatalog.TryGet("consolidated-statements", out var capability).ShouldBeTrue();
        capability.RequiresGrant.ShouldBeTrue();
    }

    [Fact]
    public void TryGet_rejects_an_unknown_name()
    {
        CapabilityCatalog.TryGet("no-such-capability", out _).ShouldBeFalse();
    }
}
