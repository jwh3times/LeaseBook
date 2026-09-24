using LeaseBook.Modules.Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Banking.Persistence;

public sealed class StatementLineConfiguration : IEntityTypeConfiguration<StatementLine>
{
    public void Configure(EntityTypeBuilder<StatementLine> builder)
    {
        builder.ToTable("statement_lines");

        builder.HasKey(e => e.Id);

        // (org_id, id) alternate key: the target of every composite FK that points here, so a
        // referencing row cannot name a row belonging to another organization. FK checks bypass
        // RLS, so this is what makes the two org_ids provably equal.
        builder.HasAlternateKey(e => new { e.OrgId, e.Id }).HasName("ak_statement_lines_org_id_id");

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.BankAccountId).IsRequired();
        builder.Property(e => e.ImportId).IsRequired();
        builder.Property(e => e.StatementDate).IsRequired();
        builder.Property(e => e.Description).IsRequired();
        // Amount is Money → NUMERIC(14,2) via the global Money convention (AppDbContext).
        builder.Property(e => e.Amount).HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(e => e.DedupHash).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();

        // Same-module FK to the import; no navigation, RESTRICT. Composite on (org_id, import_id):
        // carrying org_id on both rows is not enough, because the FK check itself bypasses RLS.
        builder.HasOne<StatementImport>()
            .WithMany()
            .HasForeignKey(e => new { e.OrgId, e.ImportId })
            .HasPrincipalKey(i => new { i.OrgId, i.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Dedup key (P67): a re-imported line collides here and is skipped. Spans all imports for the
        // account (bank_account_id is on the row). The composite FK to bank_accounts is DB-only (migration).
        builder.HasIndex(e => new { e.OrgId, e.BankAccountId, e.DedupHash }).IsUnique();
    }
}
