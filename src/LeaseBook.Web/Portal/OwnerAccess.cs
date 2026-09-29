using LeaseBook.SharedKernel;

namespace LeaseBook.Web.Portal;

/// <summary>Host-owned identity binding. Revoked links remain as history; one active link per user.</summary>
public sealed class OwnerAccess : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid UserId { get; set; }
    public Guid OwnerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
