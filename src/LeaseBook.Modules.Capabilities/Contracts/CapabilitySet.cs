using System.Collections.Frozen;
using LeaseBook.Modules.Capabilities.Registry;

namespace LeaseBook.Modules.Capabilities.Contracts;

/// <summary>
/// A frozen, fully-resolved capability set. Passed explicitly through multi-step work (bulk runs,
/// the nightly sweep) so the freeze is carried by the compiler rather than by an ambient service —
/// see ADR-019's amendment. <see cref="Version"/> is an opaque token used for optimistic
/// concurrency between preview and confirm.
/// </summary>
public sealed class CapabilitySet
{
    private readonly FrozenDictionary<string, bool> _values;

    private CapabilitySet(FrozenDictionary<string, bool> values, string version)
    {
        _values = values;
        Version = version;
    }

    public string Version { get; }

    public static CapabilitySet From(IReadOnlyDictionary<string, bool> values, string version)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(values.ToFrozenDictionary(StringComparer.Ordinal), version);
    }

    public bool IsEnabled(Capability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return _values.TryGetValue(capability.Name, out var enabled) && enabled;
    }
}
