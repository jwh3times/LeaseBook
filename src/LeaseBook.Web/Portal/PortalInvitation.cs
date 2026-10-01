using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Portal;

/// <summary>Durable enrollment state; redeemable proof is never stored in this row.</summary>
public sealed class PortalInvitation : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public required string Persona { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public string DeliveryStatus { get; set; } = "pending";
    public string? DeliveryError { get; set; }
    public int DeliveryAttempts { get; set; }
}

public sealed class PortalInvitationConfiguration : IEntityTypeConfiguration<PortalInvitation>
{
    public void Configure(EntityTypeBuilder<PortalInvitation> builder)
    {
        builder.ToTable("portal_invitations", table =>
        {
            table.HasCheckConstraint("ck_portal_invitations_target",
                "(persona = 'tenant' AND tenant_id IS NOT NULL AND owner_id IS NULL) OR " +
                "(persona = 'owner' AND owner_id IS NOT NULL AND tenant_id IS NULL)");
            table.HasCheckConstraint("ck_portal_invitations_status", "status IN ('pending', 'accepted', 'cancelled', 'replaced')");
            table.HasCheckConstraint("ck_portal_invitations_delivery_status", "delivery_status IN ('pending', 'delivered', 'failed')");
            table.HasCheckConstraint("ck_portal_invitations_delivery_attempts", "delivery_attempts >= 0");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Persona).HasMaxLength(16).IsRequired();
        builder.Property(e => e.Email).HasMaxLength(256).IsRequired();
        builder.Property(e => e.NormalizedEmail).HasMaxLength(256).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(16).IsRequired();
        builder.Property(e => e.DeliveryStatus).HasMaxLength(16).IsRequired();
        builder.Property(e => e.DeliveryError).HasMaxLength(100);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => new { e.OrgId, e.TenantId })
            .HasPrincipalKey(e => new { e.OrgId, e.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Owner>().WithMany().HasForeignKey(e => new { e.OrgId, e.OwnerId })
            .HasPrincipalKey(e => new { e.OrgId, e.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(e => new { e.OrgId, e.CreatedByUserId })
            .HasPrincipalKey(e => new { e.OrgId, e.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(e => new { e.OrgId, e.AcceptedByUserId })
            .HasPrincipalKey(e => new { e.OrgId, e.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
