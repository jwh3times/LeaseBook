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

    // ── Late-fee per-lease overrides (WP-3 / NC §42-46) ──────────────────────
    // All four are nullable: null means "use org default" for that field. GetLateFeePolicies
    // resolves (override ?? org default) per field before passing the effective policy to the
    // late-fee run strategy via ILateFeePolicyData.

    /// <summary>Per-lease override for rent due day (1–28). Null = use org default.</summary>
    public int? LateFeeRentDueDayOverride { get; set; }

    /// <summary>Per-lease override for grace days. Null = use org default.</summary>
    public int? LateFeeGraceDaysOverride { get; set; }

    /// <summary>Per-lease override for fee kind. Null = use org default.</summary>
    public LateFeeKind? LateFeeKindOverride { get; set; }

    /// <summary>Per-lease override for flat fee amount. Null = use org default.</summary>
    public decimal? LateFeeAmountOverride { get; set; }

    /// <summary>Per-lease override for fee rate in basis points. Null = use org default.</summary>
    public int? LateFeeRateBpsOverride { get; set; }

    public DateTime CreatedAt { get; set; }
}
