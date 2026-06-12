using LeaseBook.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Accounting.Persistence;

public sealed class AccountingPeriodConfiguration : IEntityTypeConfiguration<AccountingPeriod>
{
    public void Configure(EntityTypeBuilder<AccountingPeriod> builder)
    {
        builder.ToTable("accounting_periods", t =>
        {
            t.HasCheckConstraint("ck_accounting_periods_month", "month BETWEEN 1 AND 12");
            t.HasCheckConstraint(
                "ck_accounting_periods_status",
                $"status IN ({AccountingSql.Quote(PeriodStatusConverter.DbValues)})");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.Year).IsRequired();
        builder.Property(e => e.Month).IsRequired();
        builder.Property(e => e.Status).IsRequired().HasConversion<PeriodStatusConverter>();
        builder.Property(e => e.ClosedAt);
        builder.Property(e => e.CreatedAt).IsRequired();

        // One period per org-month (lazy get-or-create races against this, P32).
        builder.HasIndex(e => new { e.OrgId, e.Year, e.Month }).IsUnique();
    }
}
