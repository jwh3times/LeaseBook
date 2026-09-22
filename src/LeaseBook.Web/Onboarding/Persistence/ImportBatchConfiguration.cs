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

        // (org_id, id) alternate key: the target of every composite FK that points here, so a
        // referencing row cannot name a row belonging to another organization. FK checks bypass
        // RLS, so this is what makes the two org_ids provably equal.
        builder.HasAlternateKey(b => new { b.OrgId, b.Id }).HasName("ak_import_batches_org_id_id");

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

        // Composite on (org_id, batch_id) — FK checks bypass RLS, so a single-column key would let a
        // row attach to another organization's batch.
        builder.HasMany<ImportRow>()
            .WithOne()
            .HasForeignKey(r => new { r.OrgId, r.BatchId })
            .HasPrincipalKey(b => new { b.OrgId, b.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Supersede chain (ADR-020 §5). Composite for the same reason; supersedes_batch_id stays
        // nullable and MATCH SIMPLE skips the check while it is NULL.
        builder.HasOne<ImportBatch>()
            .WithMany()
            .HasForeignKey(b => new { b.OrgId, b.SupersedesBatchId })
            .HasPrincipalKey(b => new { b.OrgId, b.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
