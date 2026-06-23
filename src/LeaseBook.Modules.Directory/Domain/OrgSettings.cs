using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// The single settings row per org (§C.1, P46): accounting basis + money-display preferences and the
/// org profile. 1:1 with the org (unique <c>org_id</c>); lazily get-or-created on first read (WP-02).
/// The M1 read endpoints' <c>basis</c> default now reads <see cref="AccountingBasis"/>.
/// </summary>
public sealed class OrgSettings : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public AccountingBasis AccountingBasis { get; set; }

    public MoneyNegativeDisplay MoneyNegativeDisplay { get; set; }

    public string? LegalName { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip { get; set; }

    public string? Phone { get; set; }

    /// <summary>Upload wiring is M5/M8; M2 stores the ref only.</summary>
    public string? LogoBlobRef { get; set; }

    // ── Late-fee org defaults (WP-3 / NC §42-46) ─────────────────────────────
    // These columns supply the org-wide defaults; individual leases may override any field via
    // the nullable LateFee*Override columns on LeaseLite. GetLateFeePolicies resolves
    // (override ?? default) per field.

    /// <summary>Day of month rent is due (1–28). Default 1.</summary>
    public int RentDueDay { get; set; } = 1;

    /// <summary>Days after <see cref="RentDueDay"/> before a late fee applies. Default 5.</summary>
    public int LateFeeGraceDays { get; set; } = 5;

    /// <summary>Whether the org-default fee is a flat amount or a percentage of rent. Default Flat.</summary>
    public LateFeeKind LateFeeKind { get; set; } = LateFeeKind.Flat;

    /// <summary>Org-default flat late fee (dollars). Used when <see cref="LateFeeKind"/> is <see cref="LateFeeKind.Flat"/>. Default 50.</summary>
    public decimal LateFeeAmount { get; set; } = 50m;

    /// <summary>Org-default late-fee rate in basis points (100 bps = 1 %). Used when <see cref="LateFeeKind"/> is <see cref="LateFeeKind.Percent"/>. Default 500 (5 %).</summary>
    public int LateFeeRateBps { get; set; } = 500;

    public DateTime CreatedAt { get; set; }
}
