using LeaseBook.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Accounting.Persistence;

public sealed class BankLineStatusConfiguration : IEntityTypeConfiguration<BankLineState>
{
    public void Configure(EntityTypeBuilder<BankLineState> builder)
    {
        builder.ToTable("bank_line_status", t =>
            t.HasCheckConstraint("ck_bank_line_status_status",
                $"status IN ({AccountingSql.Quote(BankLineStatusConverter.DbValues)})"));

        // PK == FK to journal_lines (one state row per bank line).
        builder.HasKey(e => e.JournalLineId);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.Status).IsRequired().HasConversion<BankLineStatusConverter>();
        builder.Property(e => e.ClearedAt);
        builder.Property(e => e.ReconciliationId);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        // FK into the immutable journal stays single-column (P61): journal_lines.id is globally unique,
        // and org_id is carried + RLS-scoped. ON DELETE RESTRICT — a line with a state row is never deleted.
        builder.HasOne<JournalLine>()
            .WithMany()
            .HasForeignKey(e => e.JournalLineId)
            .OnDelete(DeleteBehavior.Restrict);

        // The reconciliation that locked this line, once reconciled (M4 / WP-04). Same-module single-column
        // FK (both rows carry org_id under RLS); no navigation, RESTRICT.
        builder.HasOne<BankReconciliation>()
            .WithMany()
            .HasForeignKey(e => e.ReconciliationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.OrgId, e.Status });
        builder.HasIndex(e => e.ReconciliationId);
    }
}
