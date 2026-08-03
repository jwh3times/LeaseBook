using System.Collections.Frozen;
using LeaseBook.Modules.Capabilities.Registry;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Modules.Capabilities.Contracts;

/// <summary>
/// A frozen, fully-resolved capability set. Passed explicitly through multi-step work (bulk runs,
/// the nightly sweep) so the freeze is carried by the compiler rather than by an ambient service —
/// see ADR-019's amendment.
/// </summary>
public sealed class CapabilitySet
{
    private readonly FrozenDictionary<string, bool> _values;

    private CapabilitySet(FrozenDictionary<string, bool> values, string version)
    {
        _values = values;
        Version = version;
    }

    /// <summary>
    /// An opaque token used for optimistic concurrency between a preview and its confirm: the
    /// confirm rejects when the version it was handed no longer matches.
    /// <para>
    /// <b>Derivation contract — whoever produces this must satisfy it.</b> The property the
    /// concurrency check depends on is <i>version equality implies set equality</i>, in both
    /// directions, across hosts and across time. So it must be a stable hash over the ordered
    /// <c>(name, value)</c> pairs of the resolved set, plus a discriminator for the shape of the
    /// source-code registry itself — so that adding, removing or re-flagging a
    /// <see cref="Capability"/> changes the version even when no database row moved.
    /// </para>
    /// <para>
    /// Two derivations that look reasonable and are both wrong. A cache-load timestamp: two hosts
    /// resolve identical sets at different instants, so every cross-instance preview/confirm pair
    /// is rejected spuriously. A per-org mutation counter: it misses a deployment-wide flag flip, so
    /// the two sides genuinely differ while the version claims they match — the direction that
    /// actually lets a confirm post under capabilities the preview never saw.
    /// </para>
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Builds a frozen set. <paramref name="values"/> must cover <c>Capabilities.All</c> in full —
    /// see the completeness check for why a partial set is rejected rather than tolerated. Extra
    /// keys are allowed and ignored, so a stale <c>feature_flags</c> row for a retired capability
    /// does not break a running host.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A registry capability is missing from <paramref name="values"/>, or <paramref name="version"/>
    /// is null, empty or whitespace.
    /// </exception>
    public static CapabilitySet From(IReadOnlyDictionary<string, bool> values, string version)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        // Completeness is asserted, not assumed. IsEnabled answers false for anything absent, so a
        // partially-populated set does not fail — it silently turns a DefaultEnabled: true money path
        // off for every org on the host, and looks exactly like a deliberate kill switch while doing
        // it. Cheap to check here; effectively undiagnosable in production.
        var missing = CapabilityCatalog.All
            .Where(c => !values.ContainsKey(c.Name))
            .Select(c => c.Name)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new ArgumentException(
                "A CapabilitySet must resolve every capability in the registry; missing: " +
                $"{string.Join(", ", missing)}. An absent entry reads as 'off', so a partial set is " +
                "indistinguishable from a deployment-wide kill switch.",
                nameof(values));
        }

        return new(values.ToFrozenDictionary(StringComparer.Ordinal), version);
    }

    public bool IsEnabled(Capability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return _values.TryGetValue(capability.Name, out var enabled) && enabled;
    }
}
