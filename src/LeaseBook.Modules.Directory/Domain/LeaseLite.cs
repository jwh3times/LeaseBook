using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// The Phase-1 minimum lease: just enough to tie a <see cref="Tenant"/> to a <see cref="Unit"/> with a
/// rent, deposit and term so ledgers and statements have what they need (§C.1). Full lease management
/// is Phase 3 — this entity "grows up" there. <see cref="Rent"/> may differ from the unit's scheduled
/// rent.
/// </summary>
public sealed class LeaseLite : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    /// <summary>FK → tenants(id).</summary>
    public Guid TenantId { get; set; }

    /// <summary>FK → units(id).</summary>
    public Guid UnitId { get; set; }

    public DateOnly? StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    /// <summary>Lease rent (may differ from <see cref="Unit.Rent"/>).</summary>
    public Money Rent { get; set; }

    public Money DepositRequired { get; set; }

    public LeaseStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
}
