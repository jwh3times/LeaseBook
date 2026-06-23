using LeaseBook.Web.Onboarding.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Onboarding.Persistence;

public sealed class ImportRowConfiguration : IEntityTypeConfiguration<ImportRow>
{
    public void Configure(EntityTypeBuilder<ImportRow> builder)
    {
        builder.ToTable("import_rows");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.OrgId).IsRequired();
        builder.Property(r => r.BatchId).IsRequired();
        builder.Property(r => r.RowNumber).IsRequired();
        builder.Property(r => r.RawJson).IsRequired().HasColumnType("jsonb");
        builder.Property(r => r.MappedJson).IsRequired().HasColumnType("jsonb");
        builder.Property(r => r.RowStatus).IsRequired();
        builder.Property(r => r.ErrorsJson).HasColumnType("jsonb");
        builder.Property(r => r.ResultingJournalEntryId);
        builder.Property(r => r.CreatedAt).IsRequired();

        builder.HasIndex(r => new { r.OrgId, r.BatchId });
    }
}
