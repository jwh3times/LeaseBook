using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Directory.Persistence;

public sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> builder)
    {
        builder.ToTable("properties");
        builder.HasKey(e => e.Id);

        // (org_id, id) alternate key — target of journal_lines' composite dimension FK (ADR-013, P60).
        builder.HasAlternateKey(e => new { e.OrgId, e.Id }).HasName("ak_properties_org_id_id");

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.OwnerId).IsRequired();
        builder.Property(e => e.Address).IsRequired();
        builder.Property(e => e.City);
        builder.Property(e => e.State);
        builder.Property(e => e.Zip);
        builder.Property(e => e.MgmtFeeBps);
        builder.Property(e => e.IsSystem).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).IsRequired();

        // Intra-module FK (both tables Directory-owned) — constraint only, no navigation, so the
        // entities stay flat POCOs. RESTRICT: an owner with properties cannot be deleted. Composite on
        // (org_id, owner_id): FK checks bypass RLS, so a single-column key would let a property name an
        // owner in another organization.
        builder.HasOne<Owner>().WithMany()
            .HasForeignKey(e => new { e.OrgId, e.OwnerId })
            .HasPrincipalKey(o => new { o.OrgId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.OrgId, e.OwnerId });
        builder.HasIndex(e => new { e.OrgId, e.Address });
        // GIN trigram index on address added in the AddDirectory migration (raw SQL).
    }
}
