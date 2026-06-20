using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Accounting.Persistence;

public sealed class BankReconciliationConfiguration : IEntityTypeConfiguration<BankReconciliation>
{
    public void Configure(EntityTypeBuilder<BankReconciliation> builder)
    {
        builder.ToTable("bank_reconciliations", t =>
            t.HasCheckConstraint("ck_bank_reconciliations_status",
                $"status IN ({AccountingSql.Quote(ReconciliationStatusConverter.DbValues)})"));

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.BankAccountId).IsRequired();
        builder.Property(e => e.PeriodYear).IsRequired();
        builder.Property(e => e.PeriodMonth).IsRequired();

        builder.Property(e => e.StatementEndingBalance)
            .HasConversion(new MoneyConverter()).HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(e => e.Status).IsRequired().HasConversion<ReconciliationStatusConverter>();
        builder.Property(e => e.FinalizedAt);
        builder.Property(e => e.FinalizedBy);
        builder.Property(e => e.ReportSnapshot).HasColumnType("jsonb");
        builder.Property(e => e.ReopenReason);
        builder.Property(e => e.CreatedAt).IsRequired();

        // One reconciliation per (org, bank account, month). The composite FK to bank_accounts
        // (org_id, bank_account_id) is DB-only (raw in the migration), like the journal-dimension FKs.
        builder.HasIndex(e => new { e.OrgId, e.BankAccountId, e.PeriodYear, e.PeriodMonth }).IsUnique();
    }
}
