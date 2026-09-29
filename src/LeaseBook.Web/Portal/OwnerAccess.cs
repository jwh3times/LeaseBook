using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Portal;

/// <summary>
/// Host-owned identity binding between an Identity user and a non-system Directory owner (ADR-003).
/// Revoked links remain as history; one active link per user; many users may share one owner.
/// </summary>
public sealed class OwnerAccess : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid UserId { get; set; }
    public Guid OwnerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class OwnerAccessConfiguration : IEntityTypeConfiguration<OwnerAccess>
{
    public void Configure(EntityTypeBuilder<OwnerAccess> builder)
    {
        builder.ToTable("owner_access");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.OrgId, e.UserId }).IsUnique().HasFilter("revoked_at IS NULL");
        builder.HasOne<AppUser>().WithMany().HasForeignKey(e => new { e.OrgId, e.UserId })
            .HasPrincipalKey(u => new { u.OrgId, u.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Owner>().WithMany().HasForeignKey(e => new { e.OrgId, e.OwnerId })
            .HasPrincipalKey(o => new { o.OrgId, o.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
