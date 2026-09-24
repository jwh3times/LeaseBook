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

        // FK into the immutable journal, composite on (org_id, journal_line_id). It was single-column
        // on the reasoning that journal_lines.id is globally unique and RLS scopes the read — but FK
        // checks run with RLS bypassed, so that only proved the line existed in SOME organization.
        // ON DELETE RESTRICT — a line with a state row is never deleted.
        builder.HasOne<JournalLine>()
            .WithMany()
            .HasForeignKey(e => new { e.OrgId, e.JournalLineId })
            .HasPrincipalKey(l => new { l.OrgId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The reconciliation that locked this line, once reconciled (M4 / WP-04). Composite for the same
        // reason; reconciliation_id stays nullable and the check is skipped while it is NULL.
        builder.HasOne<BankReconciliation>()
            .WithMany()
            .HasForeignKey(e => new { e.OrgId, e.ReconciliationId })
            .HasPrincipalKey(r => new { r.OrgId, r.Id })
            .OnDelete(DeleteBehavior.Restrict)
            // Named explicitly: EF's default for this pair is 64 characters and Postgres truncates it
            // to 63, leaving a trailing underscore that then shows up in every violation message.
            .HasConstraintName("fk_bank_line_status_reconciliation_org_id_reconciliation_id");

        builder.HasIndex(e => new { e.OrgId, e.Status });
    }
}
