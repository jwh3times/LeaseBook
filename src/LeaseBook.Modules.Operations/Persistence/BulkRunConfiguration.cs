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
        // the FK is owned by BulkRunItem.
        builder.HasMany<BulkRunItem>()
            .WithOne()
            .HasForeignKey(i => i.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
