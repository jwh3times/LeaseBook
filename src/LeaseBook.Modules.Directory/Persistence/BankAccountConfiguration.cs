using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Directory.Persistence;

public sealed class BankAccountConfiguration : IEntityTypeConfiguration<BankAccount>
{
    public void Configure(EntityTypeBuilder<BankAccount> builder)
    {
        builder.ToTable("bank_accounts", t =>
            t.HasCheckConstraint("ck_bank_accounts_purpose", $"purpose IN ({DirectorySql.Quote(BankPurposeConverter.DbValues)})"));

        builder.HasKey(e => e.Id);

        // (org_id, id) alternate key — target of journal_lines' composite dimension FK (ADR-013, P60).
        builder.HasAlternateKey(e => new { e.OrgId, e.Id });

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.Name).IsRequired();
        builder.Property(e => e.Institution);
        builder.Property(e => e.Mask);
        builder.Property(e => e.Purpose).IsRequired().HasConversion<BankPurposeConverter>();

        // is_active DEFAULT true (§C.1). M2 only ever creates active accounts (deactivation is M4), so
        // the EF store-default sentinel never misfires — a future false write is M4's concern.
        builder.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(e => e.CreatedAt).IsRequired();

        builder.HasIndex(e => new { e.OrgId, e.Name });
        // GIN trigram index on name added in the AddDirectory migration (raw SQL).
    }
}
