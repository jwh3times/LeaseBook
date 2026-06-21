using LeaseBook.Modules.Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Banking.Persistence;

public sealed class BankCsvMappingConfiguration : IEntityTypeConfiguration<BankCsvMapping>
{
    public void Configure(EntityTypeBuilder<BankCsvMapping> builder)
    {
        builder.ToTable("bank_csv_mappings");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.BankAccountId).IsRequired();
        builder.Property(e => e.Name).IsRequired();
        builder.Property(e => e.ColumnMapJson).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();

        // One mapping per (org, bank account, name) — re-saving a name updates it. The composite FK to
        // bank_accounts (org_id, bank_account_id) is DB-only (raw in the migration), like the journal dims.
        builder.HasIndex(e => new { e.OrgId, e.BankAccountId, e.Name }).IsUnique();
    }
}
