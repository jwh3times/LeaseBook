using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Accounting.Persistence;

public sealed class JournalLineConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> builder)
    {
        builder.ToTable("journal_lines", t =>
        {
            // Exactly one of debit/credit is present, and it is strictly positive (§C.1/P25).
            t.HasCheckConstraint(
                "ck_journal_lines_debit_xor_credit",
                "(debit IS NULL) <> (credit IS NULL) AND COALESCE(debit, credit) > 0");
            t.HasCheckConstraint(
                "ck_journal_lines_basis",
                $"basis IN ({AccountingSql.Quote(EntryBasisConverter.DbValues)})");
            t.HasCheckConstraint(
                "ck_journal_lines_account_class",
                $"account_class IN ({AccountingSql.Quote(AccountClassConverter.DbValues)})");

            // The structural PM-income / owner isolation, enforced at the row level (P25 / pitfall M-E4):
            // no pm_income line may carry an owner dimension, so it can never reach an owner statement.
            t.HasCheckConstraint(
                "ck_journal_lines_pm_income_no_owner",
                "account_class <> 'pm_income' OR owner_id IS NULL");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.EntryId).IsRequired();
        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.AccountClass).IsRequired().HasConversion<AccountClassConverter>();

        // Nullable Money may slip past the host's Properties<Money>() convention (pitfall M-E6), so the
        // converter + NUMERIC(14,2) are pinned here explicitly; the WP-01 round-trip test proves exactness.
        builder.Property(e => e.Debit).HasConversion(new MoneyConverter()).HasColumnType("numeric(14,2)");
        builder.Property(e => e.Credit).HasConversion(new MoneyConverter()).HasColumnType("numeric(14,2)");

        builder.Property(e => e.Basis).IsRequired().HasConversion<EntryBasisConverter>();
        builder.Property(e => e.PropertyId);
        builder.Property(e => e.UnitId);
        builder.Property(e => e.OwnerId);
        builder.Property(e => e.TenantId);
        builder.Property(e => e.BankAccountId);
        builder.Property(e => e.Memo);
        builder.Property(e => e.CreatedAt).IsRequired();

        // FK to accounts (no inverse navigation). Dimensions stay FK-less in M1 (P26).
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(e => e.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Org-leading indexes so the RLS equality predicate rides every access path (§1 of TODO).
        builder.HasIndex(e => new { e.OrgId, e.AccountId });
        builder.HasIndex(e => new { e.OrgId, e.EntryId });
        builder.HasIndex(e => new { e.OrgId, e.TenantId });
        builder.HasIndex(e => new { e.OrgId, e.OwnerId });
        builder.HasIndex(e => new { e.OrgId, e.BankAccountId });
    }
}
