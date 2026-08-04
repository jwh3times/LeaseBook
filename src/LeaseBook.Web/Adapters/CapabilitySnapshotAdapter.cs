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

        // Registry-driven, so the enabled set is complete by construction: CapabilitySet asserts it
        // resolves every capability in the catalog, and IsEnabled is the only way to read one out.
        var enabled = CapabilityCatalog.All
            .Where(resolved.IsEnabled)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        return new RunCapabilities(enabled, resolved.Version);
    }
}
