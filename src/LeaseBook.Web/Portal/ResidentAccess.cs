using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Portal;

/// <summary>Host-owned identity binding. Revoked links remain as history; one active link per user.</summary>
public sealed class ResidentAccess : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class ResidentAccessConfiguration : IEntityTypeConfiguration<ResidentAccess>
{
    public void Configure(EntityTypeBuilder<ResidentAccess> builder)
    {
        builder.ToTable("resident_access");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.OrgId, e.UserId }).IsUnique().HasFilter("revoked_at IS NULL");
        builder.HasOne<AppUser>().WithMany().HasForeignKey(e => new { e.OrgId, e.UserId })
            .HasPrincipalKey(u => new { u.OrgId, u.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => new { e.OrgId, e.TenantId })
            .HasPrincipalKey(t => new { t.OrgId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
