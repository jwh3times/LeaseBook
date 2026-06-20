using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Reconciliation;

/// <summary>The state of one reconciliation, with the live difference (statement − cleared) the UI drives to $0.</summary>
public sealed record ReconciliationView(
    Guid Id, string Status, Guid BankAccountId, int Year, int Month,
    decimal StatementEndingBalance, decimal ClearedBalance, decimal Difference, DateTime? FinalizedAt);

/// <summary>Shared reconciliation read SQL (own-module, ADR-007).</summary>
internal static class ReconciliationSql
{
    /// <summary>
    /// The account's cleared balance = the net of its bank-account lines whose status is cleared or
    /// reconciled. The reconcile difference is <c>statement_ending_balance − cleared_balance</c>.
    /// </summary>
    public static async Task<decimal> ClearedBalanceAsync(DbContext db, Guid bankAccountId, CancellationToken ct) =>
        (await db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0))
                   FILTER (WHERE COALESCE(s.status, 'uncleared') IN ('cleared', 'reconciled')), 0) AS "Value"
            FROM journal_lines jl
            LEFT JOIN bank_line_status s ON s.journal_line_id = jl.id
            WHERE jl.bank_account_id = {bankAccountId}
              AND jl.account_class IN ('trust_bank', 'pm_operating_bank')
              AND jl.basis IN ('cash', 'both')
            """).ToListAsync(ct)).Single();

    /// <summary>Projects a reconciliation plus its live cleared balance + difference into a <see cref="ReconciliationView"/>.</summary>
    public static async Task<ReconciliationView> BuildAsync(DbContext db, BankReconciliation recon, CancellationToken ct)
    {
        var cleared = await ClearedBalanceAsync(db, recon.BankAccountId, ct);
        var statement = recon.StatementEndingBalance.Amount;
        return new ReconciliationView(
            recon.Id, ReconciliationStatusConverter.ToDb(recon.Status), recon.BankAccountId,
            recon.PeriodYear, recon.PeriodMonth, statement, cleared, statement - cleared, recon.FinalizedAt);
    }
}
