using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Directory.Persistence;

public sealed class OrgSettingsConfiguration : IEntityTypeConfiguration<OrgSettings>
{
    public void Configure(EntityTypeBuilder<OrgSettings> builder)
    {
        builder.ToTable("org_settings", t =>
        {
            t.HasCheckConstraint(
                "ck_org_settings_accounting_basis",
                $"accounting_basis IN ({DirectorySql.Quote(AccountingBasisConverter.DbValues)})");
            t.HasCheckConstraint(
                "ck_org_settings_money_negative_display",
                $"money_negative_display IN ({DirectorySql.Quote(MoneyNegativeDisplayConverter.DbValues)})");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.AccountingBasis).IsRequired()
            .HasConversion<AccountingBasisConverter>().HasDefaultValue(AccountingBasis.Cash);
        builder.Property(e => e.MoneyNegativeDisplay).IsRequired()
            .HasConversion<MoneyNegativeDisplayConverter>().HasDefaultValue(MoneyNegativeDisplay.Minus);
        builder.Property(e => e.LegalName);
        builder.Property(e => e.Address);
        builder.Property(e => e.City);
        builder.Property(e => e.State);
        builder.Property(e => e.Zip);
        builder.Property(e => e.Phone);
        builder.Property(e => e.LogoBlobRef);
        builder.Property(e => e.CreatedAt).IsRequired();

        // One settings row per org — unique org_id (§C.1, P46).
        builder.HasIndex(e => e.OrgId).IsUnique();
    }
}
