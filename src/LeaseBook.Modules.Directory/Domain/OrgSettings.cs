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

    public DateTime CreatedAt { get; set; }
}
