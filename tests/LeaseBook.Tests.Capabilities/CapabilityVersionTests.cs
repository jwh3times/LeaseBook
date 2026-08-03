using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Capabilities.Resolution;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Capabilities;

/// <summary>
/// The derivation contract behind <see cref="CapabilitySet.Version"/> (ADR-028). The property that
/// matters is <b>version equality implies set equality</b>, in both directions, across hosts and
/// across time — Task 10 compares this token server-side between a preview and its confirm, and the
/// two sides may be served by different replicas.
/// </summary>
public sealed class CapabilityVersionTests
{
    private static Dictionary<string, bool> Complete(bool value = true) =>
        CapabilityCatalog.All.ToDictionary(c => c.Name, _ => value, StringComparer.Ordinal);

    /// <summary>
    /// The cross-host half. A cache-load timestamp or a per-host counter would fail here, and the
    /// consequence in production is a confirm rejected on every request that happened to land on a
    /// different replica than its preview.
    /// </summary>
    [Fact]
    public void Identical_sets_produce_identical_versions()
    {
        CapabilityVersion.Compute(Complete()).ShouldBe(CapabilityVersion.Compute(Complete()));
    }

    /// <summary>Insertion order is not part of the set's identity, so it must not reach the digest.</summary>
    [Fact]
    public void Insertion_order_does_not_change_the_version()
    {
        var forward = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["alpha"] = true,
            ["beta"] = false,
            ["gamma"] = true,
        };
        var reversed = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["gamma"] = true,
            ["beta"] = false,
            ["alpha"] = true,
        };

        CapabilityVersion.Compute(reversed).ShouldBe(CapabilityVersion.Compute(forward));
    }

    /// <summary>
    /// The direction that actually causes harm if it breaks: two genuinely different sets comparing
    /// equal lets a confirm post under capabilities the preview never saw.
    /// </summary>
    [Fact]
    public void A_changed_value_changes_the_version()
    {
        CapabilityVersion.Compute(Complete(value: false))
            .ShouldNotBe(CapabilityVersion.Compute(Complete()));
    }

    /// <summary>
    /// Every pair participates, including keys outside the registry — so the implication holds for
    /// the dictionary actually handed to <see cref="CapabilitySet.From"/>, not a filtered view of it.
    /// </summary>
    [Fact]
    public void An_extra_key_changes_the_version()
    {
        var extended = Complete();
        extended["retired-capability"] = true;

        CapabilityVersion.Compute(extended).ShouldNotBe(CapabilityVersion.Compute(Complete()));
    }

    /// <summary>The token is opaque, but it must be a token: CapabilitySet rejects blank versions.</summary>
    [Fact]
    public void Produces_a_version_CapabilitySet_accepts()
    {
        var values = Complete();

        var set = CapabilitySet.From(values, CapabilityVersion.Compute(values));

        set.Version.ShouldStartWith("v1.");
        set.Version.Length.ShouldBeGreaterThan("v1.".Length);
    }

    [Fact]
    public void Rejects_a_null_dictionary()
    {
        Should.Throw<ArgumentNullException>(() => CapabilityVersion.Compute(null!));
    }
}
