using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// A managed property belonging to one <see cref="Owner"/> (§C.1). Carries the optional per-property
/// management-fee override (<see cref="MgmtFeeBps"/>, P44) that beats the owner default. The journal's
/// <c>property_id</c> dimension FKs here (P38).
/// </summary>
public sealed class Property : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    /// <summary>FK → owners(id). Required: a property always has an owner.</summary>
    public Guid OwnerId { get; set; }

    public required string Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip { get; set; }

    /// <summary>Per-property fee override in basis points; null = use the owner default (P44).</summary>
    public int? MgmtFeeBps { get; set; }

    public bool IsSystem { get; set; }

    public DateTime CreatedAt { get; set; }
}
