using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007) for Operations' <see cref="IRunTargetNames"/> port. Dispatches Directory's
/// <see cref="GetLeaseTargetNames"/> and <see cref="GetOwnerNames"/> on the ambient transaction and maps
/// to Operations-owned types, so no Directory type reaches Modules.Operations.
/// </summary>
internal sealed class RunTargetNamesAdapter(ISender sender) : IRunTargetNames
{
    public async Task<IReadOnlyDictionary<Guid, RunLeaseTarget>> GetLeaseNamesAsync(
        IReadOnlyList<Guid> leaseIds, CancellationToken ct)
    {
        var names = await sender.Query(new GetLeaseTargetNames(leaseIds), ct);
        return names.ToDictionary(
            entry => entry.Key,
            entry => new RunLeaseTarget(entry.Value.TenantId, entry.Value.TenantName, entry.Value.UnitLabel));
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetOwnerNamesAsync(
        IReadOnlyList<Guid> ownerIds, CancellationToken ct) =>
        sender.Query(new GetOwnerNames(ownerIds), ct);
}
