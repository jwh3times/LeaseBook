using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// An append-only, effective-dated handoff of a managed property from one owner to another.
/// The earliest record's seller is the property's baseline owner; each later record establishes the
/// owner for financial events on and after its effective date.
/// </summary>
public sealed class PropertyOwnershipTransfer : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public Guid FromOwnerId { get; set; }

    public Guid ToOwnerId { get; set; }

    public DateOnly EffectiveDate { get; set; }

    public required string SourceRef { get; set; }

    public DateTime CreatedAt { get; set; }
}
