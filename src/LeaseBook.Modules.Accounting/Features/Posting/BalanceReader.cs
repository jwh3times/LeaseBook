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

    /// <summary>
    /// Security deposit currently held in one exact attribution bucket. Dispositions use this
    /// stronger read so a positive tenant total cannot mask an empty seller/bank/property bucket.
    /// </summary>
    public Task<decimal> DepositsHeldAsync(
        Guid tenantId,
        Guid? propertyId,
        Guid? ownerId,
        Guid bankAccountId,
        CancellationToken ct) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)), 0) AS "Value"
            FROM journal_lines jl JOIN accounts a ON a.id = jl.account_id
            WHERE a.code = {AccountCodes.SecurityDepositsHeld}
              AND jl.tenant_id = {tenantId}
              AND jl.property_id IS NOT DISTINCT FROM {propertyId}
              AND jl.owner_id IS NOT DISTINCT FROM {ownerId}
              AND jl.bank_account_id = {bankAccountId}
              AND jl.basis IN ('cash', 'both')
            """).SingleAsync(ct);

    /// <summary>Prepayment currently held for a tenant (CR-positive liability), cash+both.</summary>
    public Task<decimal> PrepaymentsHeldAsync(Guid tenantId, CancellationToken ct) =>
        HeldLiabilityByCodeAsync(AccountCodes.TenantPrepayments, tenantId, ct);

    /// <summary>
    /// Prepayment held for a tenant in one trust bank. Refunds use this per-bank read (#473) so a
    /// positive tenant total cannot mask a bank that holds less than the refund draws from it.
    /// </summary>
    public Task<decimal> PrepaymentsHeldAsync(Guid tenantId, Guid bankAccountId, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)), 0) AS "Value"
            FROM journal_lines jl JOIN accounts a ON a.id = jl.account_id
            WHERE a.code = {AccountCodes.TenantPrepayments} AND jl.tenant_id = {tenantId}
              AND jl.bank_account_id = {bankAccountId}
              AND jl.basis IN ('cash', 'both')
            """).SingleAsync(ct);

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

    // The three "floor" reads serve a backdated posting (a payment return, #490). They answer with the
    // LOWEST end-of-day balance on or after a date, not today's: a debit dated in the past lowers every
    // balance from that day on, so a balance that has since recovered can still have been too low then.
    // The zero row at the date makes the balance carried into it part of the minimum.

    /// <summary>The lowest prepayment held for a tenant in one trust bank on or after <paramref name="from"/>.</summary>
    public Task<decimal> PrepaymentsHeldFloorAsync(
        Guid tenantId, Guid bankAccountId, DateOnly from, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT MIN(running) AS "Value" FROM (
              SELECT d, SUM(delta) OVER (ORDER BY d) AS running FROM (
                SELECT je.entry_date AS d, SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS delta
                FROM journal_lines jl
                JOIN journal_entries je ON je.id = jl.entry_id
                JOIN accounts a ON a.id = jl.account_id
                WHERE a.code = {AccountCodes.TenantPrepayments} AND jl.tenant_id = {tenantId}
                  AND jl.bank_account_id = {bankAccountId}
                  AND jl.basis IN ('cash', 'both')
                GROUP BY je.entry_date
                UNION ALL SELECT {from}, 0
              ) days
            ) balances WHERE d >= {from}
            """).SingleAsync(ct);

    /// <summary>The lowest cash equity an owner holds, across all banks, on or after <paramref name="from"/>.</summary>
    public Task<decimal> OwnerEquityCashFloorAsync(Guid ownerId, DateOnly from, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT MIN(running) AS "Value" FROM (
              SELECT d, SUM(delta) OVER (ORDER BY d) AS running FROM (
                SELECT je.entry_date AS d, SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS delta
                FROM journal_lines jl JOIN journal_entries je ON je.id = jl.entry_id
                WHERE jl.account_class = 'owner_equity' AND jl.owner_id = {ownerId}
                  AND jl.basis IN ('cash', 'both')
                GROUP BY je.entry_date
                UNION ALL SELECT {from}, 0
              ) days
            ) balances WHERE d >= {from}
            """).SingleAsync(ct);

    /// <summary>
    /// The lowest cash equity an owner holds in one trust bank on or after <paramref name="from"/>. Read
    /// alongside the all-bank floor, so equity held in another bank cannot cover a reversal drawn on this one.
    /// </summary>
    public Task<decimal> OwnerEquityCashFloorAsync(
        Guid ownerId, Guid bankAccountId, DateOnly from, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT MIN(running) AS "Value" FROM (
              SELECT d, SUM(delta) OVER (ORDER BY d) AS running FROM (
                SELECT je.entry_date AS d, SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS delta
                FROM journal_lines jl JOIN journal_entries je ON je.id = jl.entry_id
                WHERE jl.account_class = 'owner_equity' AND jl.owner_id = {ownerId}
                  AND jl.bank_account_id = {bankAccountId}
                  AND jl.basis IN ('cash', 'both')
                GROUP BY je.entry_date
                UNION ALL SELECT {from}, 0
              ) days
            ) balances WHERE d >= {from}
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
