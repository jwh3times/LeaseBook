using LeaseBook.Modules.Reporting.Delivery;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Reporting.Persistence;

/// <summary>
/// EF configuration for <see cref="StatementDeliveryRecord"/>. Snake_case table/column names are
/// applied globally by <c>UseSnakeCaseNamingConvention()</c>; only the CHECK constraint and the
/// converter require explicit wiring.
/// </summary>
public sealed class StatementDeliveryRecordConfiguration
    : IEntityTypeConfiguration<StatementDeliveryRecord>
{
    public void Configure(EntityTypeBuilder<StatementDeliveryRecord> builder)
    {
        builder.ToTable("statement_deliveries", t =>
            t.HasCheckConstraint(
                "ck_statement_deliveries_state",
                $"state IN ({Quote(DeliveryStateConverter.DbValues)})"));

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.OwnerId).IsRequired();
        builder.Property(e => e.PeriodYear).IsRequired();
        builder.Property(e => e.PeriodMonth).IsRequired();
        builder.Property(e => e.ToEmail).IsRequired();
        builder.Property(e => e.State).IsRequired().HasConversion<DeliveryStateConverter>();
        builder.Property(e => e.ArtifactKey).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();

        // Index for the common query: all deliveries for an org + owner in a period.
        builder.HasIndex(e => new { e.OrgId, e.OwnerId, e.PeriodYear, e.PeriodMonth });
    }

    private static string Quote(string[] values) =>
        string.Join(", ", values.Select(v => $"'{v}'"));
}
