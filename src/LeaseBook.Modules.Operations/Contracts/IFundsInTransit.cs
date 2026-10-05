namespace LeaseBook.Modules.Operations.Contracts;

// ── Data DTO ─────────────────────────────────────────────────────────────────

/// <summary>
/// What a tenant has paid online that the trust bank has not yet received (ADR-053): the total going
/// to their ledger, and the earliest date any of it was paid.
/// </summary>
public sealed record TenantFundsInTransit(decimal Amount, DateOnly PaidOn);

// ── Port ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Consumer-owned read port (ADR-007) for tenants with funds in transit. A tenant with none is absent
/// from the map. The journal knows nothing of these payments until the bank does, so a run that
/// reasons from the journal alone would treat a tenant who has paid as one who has not.
/// </summary>
public interface IFundsInTransit
{
    Task<IReadOnlyDictionary<Guid, TenantFundsInTransit>> GetAsync(IReadOnlyList<Guid> tenantIds, CancellationToken ct);
}
