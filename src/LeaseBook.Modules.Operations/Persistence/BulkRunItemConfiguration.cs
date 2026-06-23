using LeaseBook.Modules.Operations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Operations.Persistence;

public sealed class BulkRunItemConfiguration : IEntityTypeConfiguration<BulkRunItem>
{
    public void Configure(EntityTypeBuilder<BulkRunItem> builder)
    {
        builder.ToTable("bulk_run_items");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.OrgId).IsRequired();
        builder.Property(i => i.RunId).IsRequired();
        builder.Property(i => i.TargetKind)
            .IsRequired()
            .HasConversion<string>();
        builder.Property(i => i.TargetId).IsRequired();
        builder.Property(i => i.Status)
            .IsRequired()
            .HasConversion<string>();
        builder.Property(i => i.Amount)
            .IsRequired()
            .HasColumnType("numeric(14,2)");
        builder.Property(i => i.SnapshotJson)
            .IsRequired()
            .HasColumnType("jsonb");
        builder.Property(i => i.CreatedAt).IsRequired();

        builder.HasIndex(i => new { i.OrgId, i.RunId });
    }
}
