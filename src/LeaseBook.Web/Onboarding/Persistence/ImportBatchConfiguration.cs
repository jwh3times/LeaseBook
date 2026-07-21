using LeaseBook.Web.Onboarding.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Onboarding.Persistence;

public sealed class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        builder.ToTable("import_batches");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.OrgId).IsRequired();
        builder.Property(b => b.EntityKind).IsRequired();
        builder.Property(b => b.MappingProfile).IsRequired();
        builder.Property(b => b.SourceFilename).IsRequired();
        builder.Property(b => b.RowCount).IsRequired();
        builder.Property(b => b.ErrorCount).IsRequired();
        builder.Property(b => b.Status).IsRequired();
        builder.Property(b => b.Actor);
        builder.Property(b => b.SupersedesBatchId);
        builder.Property(b => b.CreatedAt).IsRequired();

        builder.HasIndex(b => new { b.OrgId, b.EntityKind, b.Status });
        builder.HasIndex(b => new { b.OrgId, b.SupersedesBatchId })
            .HasDatabaseName("ix_import_batches_org_id_supersedes_batch_id");

        builder.HasMany<ImportRow>()
            .WithOne()
            .HasForeignKey(r => r.BatchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ImportBatch>()
            .WithMany()
            .HasForeignKey(b => b.SupersedesBatchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
