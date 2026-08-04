using System.Collections.Frozen;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Operations.Contracts;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / ADR-028) for the Operations module's <see cref="ICapabilitySnapshot"/>
/// port. Operations must not reference the Capabilities module, so it declares its own
/// <see cref="RunCapabilities"/> view and this adapter maps the resolved
/// <see cref="CapabilitySet"/> into it.
/// <para>
/// <b>No scope, no transaction, no second connection.</b> <see cref="ICapabilityGate.ResolveDurableAsync"/>
/// already reads on the ambient RLS transaction — the one <c>OrgContextMiddleware</c> or
/// <c>OrgScopedExecutor</c> opened — and that is precisely what makes the run's snapshot consistent
/// with the rows the run writes. Reaching for <see cref="IPlatformScope"/> here would call
/// <c>BeginTransactionAsync</c> with a transaction already open and throw on the money path.
/// </para>
/// <para>
/// <b>Async, not an expression-bodied pass-through.</b> The gate faults its returned task on missing
/// org context rather than throwing synchronously; the <c>await</c> below preserves that. The
/// difference is invisible under a bare await and load-bearing under <c>Task.WhenAll</c>.
/// </para>
/// </summary>
internal sealed class CapabilitySnapshotAdapter(ICapabilityGate gate) : ICapabilitySnapshot
{
    public async Task<RunCapabilities> ResolveDurableAsync(CancellationToken ct)
    {
        var resolved = await gate.ResolveDurableAsync(ct);

        // The COMPLETE map, not the enabled subset. CapabilitySet asserts it resolves every
        // capability in the catalog, so enumerating the catalog here preserves that guarantee across
        // the hop instead of discarding it: RunCapabilities.IsEnabled can then throw on an unknown
        // name rather than answering a silent "off" that a money-path gate cannot tell from a kill
        // switch.
        //
        // Frozen, like the type it is mapped from. IReadOnlyDictionary is a view, not a guarantee:
        // handing a strategy a Dictionary behind that interface would let a cast mutate the "frozen"
        // set mid-run, which is precisely the thing the parameter exists to prevent.
        var values = CapabilityCatalog.All.ToFrozenDictionary(
            c => c.Name, resolved.IsEnabled, StringComparer.Ordinal);

        // Which of those are money-path is a property of the REGISTRY, and the registry lives on this
        // side of the boundary. Operations gets the names, not the Capability records: it needs to
        // know which entries of the map above the cross-run guard compares, and nothing more. Passing
        // the records themselves would put a capability type into a module that must not have one.
        var moneyPath = CapabilityCatalog.MoneyPath
            .Select(c => c.Name)
            .ToFrozenSet(StringComparer.Ordinal);

        return new RunCapabilities(values, resolved.Version, moneyPath);
    }
}
