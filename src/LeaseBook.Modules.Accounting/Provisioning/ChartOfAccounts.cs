using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Provisioning;

/// <summary>
/// Provisions the per-org chart of accounts (§C.2). Reads existing codes through the org-filtered
/// context, so it only ever adds the accounts this org is missing — idempotent by <c>code</c>. The
/// (org_id, code) unique index is the backstop if two provisions race.
/// </summary>
internal sealed class ChartOfAccounts(DbContext db) : IChartOfAccounts
{
    public async Task ProvisionAsync(IReadOnlyList<BankAccountSpec> banks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(banks);

        // Org-scoped read (global query filter): this org's existing codes only.
        var codes = (await db.Set<Account>().Select(a => a.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var toCreate = new List<Account>();

        void AddIfMissing(string code, AccountClass @class, string name, Guid? bankAccountId)
        {
            // HashSet.Add doubles as the missing-check and de-dups within this call.
            if (codes.Add(code))
            {
                toCreate.Add(Account.Create(code, @class, name, bankAccountId));
            }
        }

        AddIfMissing(AccountCodes.TenantReceivable, AccountClass.TenantReceivable, "Tenant Receivable", null);
        AddIfMissing(AccountCodes.OwnerEquity, AccountClass.OwnerEquity, "Owner Equity", null);
        AddIfMissing(AccountCodes.SecurityDepositsHeld, AccountClass.DepositLiability, "Security Deposits Held", null);
        AddIfMissing(AccountCodes.TenantPrepayments, AccountClass.DepositLiability, "Tenant Prepayments", null);
        AddIfMissing(AccountCodes.PmIncome, AccountClass.PmIncome, "PM Income", null);
        AddIfMissing(AccountCodes.MigrationClearing, AccountClass.MigrationClearing, "Migration Clearing", null);

        foreach (var bank in banks)
        {
            if (bank.Purpose == BankPurpose.Operating)
            {
                AddIfMissing(
                    AccountCodes.PmOperatingBank(bank.BankAccountId),
                    AccountClass.PmOperatingBank, bank.Name, bank.BankAccountId);
            }
            else
            {
                AddIfMissing(
                    AccountCodes.TrustBank(bank.BankAccountId),
                    AccountClass.TrustBank, bank.Name, bank.BankAccountId);
            }
        }

        if (toCreate.Count > 0)
        {
            db.Set<Account>().AddRange(toCreate);
            await db.SaveChangesAsync(ct);
        }
    }
}
