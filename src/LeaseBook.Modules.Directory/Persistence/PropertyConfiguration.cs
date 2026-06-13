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
        // entities stay flat POCOs. RESTRICT: an owner with properties cannot be deleted.
        builder.HasOne<Owner>().WithMany().HasForeignKey(e => e.OwnerId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.OrgId, e.OwnerId });
        builder.HasIndex(e => new { e.OrgId, e.Address });
        // GIN trigram index on address added in the AddDirectory migration (raw SQL).
    }
}
