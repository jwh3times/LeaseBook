using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// A rentable unit within a <see cref="Property"/> (§C.1). <see cref="Rent"/> is the scheduled rent
/// (the lease may set a different figure on <see cref="LeaseLite.Rent"/>). The journal's <c>unit_id</c>
/// dimension FKs here (P38).
/// </summary>
public sealed class Unit : IOrgScoped, ISystemFlagged
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    /// <summary>FK → properties(id).</summary>
    public Guid PropertyId { get; set; }

    /// <summary>e.g. "#2B" or the unit line.</summary>
    public required string Label { get; set; }

    /// <summary>Scheduled rent for the unit.</summary>
    public Money Rent { get; set; }

    public UnitStatus Status { get; set; }

    public bool IsSystem { get; set; }

    public DateTime CreatedAt { get; set; }
}
