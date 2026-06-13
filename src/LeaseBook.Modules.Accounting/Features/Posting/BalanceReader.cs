using LeaseBook.Modules.Accounting.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Posting;

/// <summary>
/// The balance reads the guarded events (P31) consult before posting: open receivable (auto-split),
/// held deposit/prepayment (over-application), held PM fees (over-sweep), and owner cash equity
/// (reserve floor). Raw SQL on the <b>ambient scoped connection</b> — value-converted Money columns
/// can't be aggregated in LINQ, and RLS rides the connection (M-E11). WP-06 owns the product-facing
/// read models; this duplicates only the minimal balances the engine itself needs (allowed this WP).
/// </summary>
internal sealed class BalanceReader(DbContext db)
{
    /// <summary>Net receivable owed by a tenant (DR-positive), accrual basis. Negative ⇒ net prepaid.</summary>
    public Task<decimal> TenantReceivableAsync(Guid tenantId, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(debit, 0) - COALESCE(credit, 0)), 0) AS "Value"
            FROM journal_lines
            WHERE account_class = 'tenant_receivable' AND tenant_id = {tenantId}
              AND basis IN ('accrual', 'both')
            """).SingleAsync(ct);

    /// <summary>Security deposit currently held for a tenant (CR-positive liability), cash+both.</summary>
    public Task<decimal> DepositsHeldAsync(Guid tenantId, CancellationToken ct) =>
        HeldLiabilityByCodeAsync(AccountCodes.SecurityDepositsHeld, tenantId, ct);

    /// <summary>Prepayment currently held for a tenant (CR-positive liability), cash+both.</summary>
    public Task<decimal> PrepaymentsHeldAsync(Guid tenantId, CancellationToken ct) =>
        HeldLiabilityByCodeAsync(AccountCodes.TenantPrepayments, tenantId, ct);

    /// <summary>PM fees held in a given trust bank (pm_income CR-positive on that bank dim), cash+both.</summary>
    public Task<decimal> HeldFeesAsync(Guid bankAccountId, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(credit, 0) - COALESCE(debit, 0)), 0) AS "Value"
            FROM journal_lines
            WHERE account_class = 'pm_income' AND bank_account_id = {bankAccountId}
              AND basis IN ('cash', 'both')
            """).SingleAsync(ct);

    /// <summary>An owner's distributable cash equity (CR-positive), cash+both basis (§C.6 / P30).</summary>
    public Task<decimal> OwnerEquityCashAsync(Guid ownerId, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(credit, 0) - COALESCE(debit, 0)), 0) AS "Value"
            FROM journal_lines
            WHERE account_class = 'owner_equity' AND owner_id = {ownerId}
              AND basis IN ('cash', 'both')
            """).SingleAsync(ct);

    private Task<decimal> HeldLiabilityByCodeAsync(string accountCode, Guid tenantId, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)), 0) AS "Value"
            FROM journal_lines jl JOIN accounts a ON a.id = jl.account_id
            WHERE a.code = {accountCode} AND jl.tenant_id = {tenantId}
              AND jl.basis IN ('cash', 'both')
            """).SingleAsync(ct);
}
