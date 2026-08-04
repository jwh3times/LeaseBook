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
    /// <b>No ambient clock.</b> The delay is load-bearing, not padding: two back-to-back calls prove
    /// almost nothing, because <c>DateTimeOffset.UtcNow</c> advances in ~15.6ms steps on Windows, so
    /// a clock-derived version would very likely return the same value twice and this test would pass
    /// against exactly the derivation it exists to forbid. 100ms clears that granularity with room to
    /// spare.
    /// <para>
    /// The cross-<i>host</i> half of the property is
    /// <c>CapabilityPropagationTests.Two_hosts_resolving_the_same_state_agree_on_the_version</c>; this
    /// is the cross-<i>time</i> half. Both matter, because Task 10 compares a preview's token against
    /// a confirm's, and the two can differ in either dimension.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Identical_sets_produce_identical_versions_across_time()
    {
        var first = CapabilityVersion.Compute(Complete());

        await Task.Delay(100, TestContext.Current.CancellationToken);

        CapabilityVersion.Compute(Complete()).ShouldBe(first);
    }

    /// <summary>
    /// <b>No hidden counter</b>, asserted without depending on timing at all. A per-call or per-host
    /// counter folded into the digest would drift between the first and third computation even though
    /// their inputs are identical — and unlike the clock case, no delay is needed to expose it.
    /// </summary>
    [Fact]
    public void Interleaving_a_different_set_does_not_perturb_the_version()
    {
        var before = CapabilityVersion.Compute(Complete());
        _ = CapabilityVersion.Compute(Complete(value: false));

        CapabilityVersion.Compute(Complete()).ShouldBe(before);
    }

    /// <summary>
    /// The encoding must be injective: <c>{"a=1\nb": true}</c> and <c>{"a": true, "b": true}</c> are
    /// different sets, and without length-prefixed fields they serialize to identical bytes — two
    /// different sets sharing a version, which is the direction that lets a confirm post under
    /// capabilities the preview never saw. Unreachable through the registry, but Compute is public
    /// and its doc promises the property for any dictionary.
    /// </summary>
    [Fact]
    public void Separator_bearing_keys_do_not_collide()
    {
        var single = new Dictionary<string, bool>(StringComparer.Ordinal) { ["a=1\nb"] = true };
        var pair = new Dictionary<string, bool>(StringComparer.Ordinal) { ["a"] = true, ["b"] = true };

        CapabilityVersion.Compute(single).ShouldNotBe(CapabilityVersion.Compute(pair));
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
