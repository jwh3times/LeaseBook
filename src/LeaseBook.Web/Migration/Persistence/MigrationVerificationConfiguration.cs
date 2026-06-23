using LeaseBook.Web.Onboarding.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Onboarding.Persistence;

public sealed class MigrationVerificationConfiguration : IEntityTypeConfiguration<MigrationVerification>
{
    public void Configure(EntityTypeBuilder<MigrationVerification> builder)
    {
        builder.ToTable("migration_verifications");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.OrgId).IsRequired();
        builder.Property(v => v.CutoverDate).IsRequired();
        builder.Property(v => v.ExpectedJson).IsRequired().HasColumnType("jsonb");
        builder.Property(v => v.ActualJson).IsRequired().HasColumnType("jsonb");
        builder.Property(v => v.VarianceTotal).IsRequired().HasColumnType("numeric(14,2)");
        builder.Property(v => v.IsTied).IsRequired();
        builder.Property(v => v.SignedOffBy);
        builder.Property(v => v.SignedOffAt);
        builder.Property(v => v.ReportSnapshot).IsRequired();
        builder.Property(v => v.CreatedAt).IsRequired();

        builder.HasIndex(v => new { v.OrgId, v.CutoverDate });
    }
}
