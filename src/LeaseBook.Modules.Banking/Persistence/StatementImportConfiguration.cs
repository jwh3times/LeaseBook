using LeaseBook.Modules.Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Banking.Persistence;

public sealed class StatementImportConfiguration : IEntityTypeConfiguration<StatementImport>
{
    public void Configure(EntityTypeBuilder<StatementImport> builder)
    {
        builder.ToTable("statement_imports");

        builder.HasKey(e => e.Id);

        // (org_id, id) alternate key: the target of every composite FK that points here, so a
        // referencing row cannot name a row belonging to another organization. FK checks bypass
        // RLS, so this is what makes the two org_ids provably equal.
        builder.HasAlternateKey(e => new { e.OrgId, e.Id }).HasName("ak_statement_imports_org_id_id");

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.BankAccountId).IsRequired();
        builder.Property(e => e.Filename).IsRequired();
        builder.Property(e => e.ImportedAt).IsRequired();
        builder.Property(e => e.ImportedBy);
        builder.Property(e => e.RowCount).IsRequired();
        builder.Property(e => e.Status).IsRequired();

        // Composite FK to bank_accounts (org_id, bank_account_id) is DB-only (raw in the migration).
        builder.HasIndex(e => new { e.OrgId, e.BankAccountId });
    }
}
