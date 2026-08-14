using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Directory.Persistence;

public sealed class PropertyOwnershipTransferConfiguration
    : IEntityTypeConfiguration<PropertyOwnershipTransfer>
{
    public void Configure(EntityTypeBuilder<PropertyOwnershipTransfer> builder)
    {
        builder.ToTable("property_ownership_transfers", table =>
            table.HasCheckConstraint(
                "ck_property_ownership_transfers_distinct_owners",
                "from_owner_id <> to_owner_id"));
        builder.HasKey(transfer => transfer.Id);
        builder.HasAlternateKey(transfer => new { transfer.OrgId, transfer.Id });

        builder.Property(transfer => transfer.OrgId).IsRequired();
        builder.Property(transfer => transfer.PropertyId).IsRequired();
        builder.Property(transfer => transfer.FromOwnerId).IsRequired();
        builder.Property(transfer => transfer.ToOwnerId).IsRequired();
        builder.Property(transfer => transfer.EffectiveDate).IsRequired();
        builder.Property(transfer => transfer.SourceRef).IsRequired().HasMaxLength(200);
        builder.Property(transfer => transfer.CreatedAt).IsRequired();

        builder.HasOne<Property>().WithMany()
            .HasForeignKey(transfer => new { transfer.OrgId, transfer.PropertyId })
            .HasPrincipalKey(property => new { property.OrgId, property.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Owner>().WithMany()
            .HasForeignKey(transfer => new { transfer.OrgId, transfer.FromOwnerId })
            .HasPrincipalKey(owner => new { owner.OrgId, owner.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Owner>().WithMany()
            .HasForeignKey(transfer => new { transfer.OrgId, transfer.ToOwnerId })
            .HasPrincipalKey(owner => new { owner.OrgId, owner.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(transfer => new { transfer.OrgId, transfer.PropertyId, transfer.EffectiveDate })
            .IsUnique();
        builder.HasIndex(transfer => new { transfer.OrgId, transfer.SourceRef }).IsUnique();
        builder.HasIndex(transfer => new { transfer.OrgId, transfer.FromOwnerId });
        builder.HasIndex(transfer => new { transfer.OrgId, transfer.ToOwnerId });
    }
}
