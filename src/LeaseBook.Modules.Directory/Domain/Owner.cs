using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// A property owner — the principal whose trust money LeaseBook holds (§C.1). The M1 journal already
/// carries this row's id as the <c>owner_id</c> dimension (P26); M2 gives it a real row that the
/// journal FK (P38) now points at. Unlike the journal aggregates, owners are CRUD entities — public
/// setters, no internal-ctor lock-down. <see cref="IsSystem"/> rows (the "All other owners" roll-up,
/// P40) are excluded from every list/search/CRUD surface and rendered only by the dashboard hero.
/// </summary>
public sealed class Owner : IOrgScoped, ISystemFlagged
{
    public Guid Id { get; set; }

    /// <summary>Set by the org-stamping interceptor on insert (§C.4); never assigned by callers.</summary>
    public Guid OrgId { get; set; }

    public required string Name { get; set; }

    /// <summary>Avatar initials (e.g. "HF"); optional.</summary>
    public string? Initials { get; set; }

    public string? ContactEmail { get; set; }

    public string? ContactPhone { get; set; }

    /// <summary>Org/owner default management fee in basis points (800 = 8%); P44. M2 stores, M6 computes.</summary>
    public int? DefaultMgmtFeeBps { get; set; }

    /// <summary>The <c>OwnerDisbursed</c> reserve floor (P31). M2 stores it; M6 enforces it.</summary>
    public Money ReserveAmount { get; set; }

    /// <summary>Aggregate roll-up rows (P40) — hidden from lists/search/CRUD.</summary>
    public bool IsSystem { get; set; }

    public DateTime CreatedAt { get; set; }
}
