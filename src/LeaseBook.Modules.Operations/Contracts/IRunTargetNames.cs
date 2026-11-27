namespace LeaseBook.Modules.Operations.Contracts;

/// <summary>
/// Who a run's targets are today, for the run-detail read (#573). Consumer-owned port (ADR-007): the
/// host adapter reads Directory through <c>ISender</c> on the ambient RLS transaction. Run items keep
/// only the target id; the wording at run time survives in each posted entry's description, so a
/// name here is for finding the target, never part of the run's record.
/// <para>
/// Batch reads returning maps. A target missing from a map was not found — a deleted or system row, or
/// an id the run was handed that never existed — and the caller reports it as such, never drops it.
/// </para>
/// </summary>
public interface IRunTargetNames
{
    /// <summary>The tenant and unit of each lease in <paramref name="leaseIds"/> that exists.</summary>
    Task<IReadOnlyDictionary<Guid, LeaseTargetName>> GetLeaseNamesAsync(
        IReadOnlyList<Guid> leaseIds, CancellationToken ct);

    /// <summary>The name of each owner in <paramref name="ownerIds"/> that exists.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetOwnerNamesAsync(
        IReadOnlyList<Guid> ownerIds, CancellationToken ct);
}

/// <summary>One lease target's tenant (with the id a link to the tenant's ledger needs) and unit.</summary>
public sealed record LeaseTargetName(Guid TenantId, string TenantName, string UnitLabel);
