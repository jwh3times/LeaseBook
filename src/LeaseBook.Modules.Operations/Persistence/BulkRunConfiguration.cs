using LeaseBook.Modules.Operations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Operations.Persistence;

public sealed class BulkRunConfiguration : IEntityTypeConfiguration<BulkRun>
{
    public void Configure(EntityTypeBuilder<BulkRun> builder)
    {
        builder.ToTable("bulk_runs");

        builder.HasKey(r => r.Id);

        // (org_id, id) alternate key: the target of every composite FK that points here, so a
        // referencing row cannot name a row belonging to another organization. FK checks bypass
        // RLS, so this is what makes the two org_ids provably equal.
        builder.HasAlternateKey(r => new { r.OrgId, r.Id }).HasName("ak_bulk_runs_org_id_id");

        builder.Property(r => r.OrgId).IsRequired();
        builder.Property(r => r.RunType)
            .IsRequired()
            .HasConversion<string>();
        builder.Property(r => r.PeriodYear).IsRequired();
        builder.Property(r => r.PeriodMonth).IsRequired();
        builder.Property(r => r.SummaryJson)
            .IsRequired()
            .HasColumnType("jsonb");
        builder.Property(r => r.CreatedAt).IsRequired();

        // Index to support "runs for this org/type/period" lookups by WP-2/3/4 UI endpoints.
        builder.HasIndex(r => new { r.OrgId, r.RunType, r.PeriodYear, r.PeriodMonth });

        // One run → many items. EF navigation is not declared on BulkRun (write-once aggregate);
        // the FK is owned by BulkRunItem. Composite on (org_id, run_id) so an item cannot attach to
        // another organization's run — FK checks bypass RLS.
        builder.HasMany<BulkRunItem>()
            .WithOne()
            .HasForeignKey(i => new { i.OrgId, i.RunId })
            .HasPrincipalKey(r => new { r.OrgId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
