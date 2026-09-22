using LeaseBook.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Accounting.Persistence;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", t =>
        {
            t.HasCheckConstraint(
                "ck_accounts_class",
                $"class IN ({AccountingSql.Quote(AccountClassConverter.DbValues)})");

            // bank_account_id is set iff the account represents a bank (trust or PM operating); the two
            // bank classes carry it, every singleton account does not.
            t.HasCheckConstraint(
                "ck_accounts_bank_account_id_matches_class",
                "(bank_account_id IS NOT NULL) = (class IN ('trust_bank','pm_operating_bank'))");
        });

        builder.HasKey(e => e.Id);

        // (org_id, id) alternate key: the target of every composite FK that points here, so a
        // referencing row cannot name a row belonging to another organization. FK checks bypass
        // RLS, so this is what makes the two org_ids provably equal.
        builder.HasAlternateKey(e => new { e.OrgId, e.Id }).HasName("ak_accounts_org_id_id");

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.Code).IsRequired();
        builder.Property(e => e.Class).IsRequired().HasConversion<AccountClassConverter>();
        builder.Property(e => e.Name).IsRequired();
        builder.Property(e => e.BankAccountId);
        builder.Property(e => e.CreatedAt).IsRequired();

        // Stable provisioning key, unique per org; templates resolve accounts by it.
        builder.HasIndex(e => new { e.OrgId, e.Code }).IsUnique();
    }
}
