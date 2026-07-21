using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Migration;

/// <summary>
/// Query: read imported subledger totals + clearing residuals for WP-4 verification.
///
/// Reads ONLY Accounting's own tables (journal_lines + accounts) — no cross-module boundary.
///
/// Per-basis clearing residual formula (debit-normal):
///   ClearingCash    = SUM(debit - credit) FILTER basis IN ('cash','both')  for migration_clearing lines
///   ClearingAccrual = SUM(debit - credit) FILTER basis IN ('accrual','both') for migration_clearing lines
///
/// Subledger totals use credit-normal convention for equity/liability accounts:
///   OwnerEquityCashTotal   = SUM(credit - debit) FILTER basis IN ('cash','both') for owner_equity lines
///   DepositLiabilityTotal  = SUM(credit - debit) FILTER basis IN ('cash','both') for deposit_liability lines
///
/// Bank book balance is debit-normal: SUM(debit - credit) FILTER basis IN ('cash','both') per bank account.
/// </summary>
public sealed record GetMigrationVerificationData : IQuery<MigrationVerificationData>;

internal sealed class GetMigrationVerificationDataHandler(DbContext db)
    : IQueryHandler<GetMigrationVerificationData, MigrationVerificationData>
{
    public async Task<MigrationVerificationData> Handle(GetMigrationVerificationData query, CancellationToken ct)
    {
        // ── Clearing residuals (both bases) ───────────────────────────────────────────────────────
        // A zero residual means the import balanced internally in that basis.
        var clearing = await db.Database.SqlQuery<ClearingRow>(
            $"""
            SELECT
                COALESCE(SUM(COALESCE(debit, 0) - COALESCE(credit, 0))
                    FILTER (WHERE basis IN ('cash', 'both')), 0)    AS cash_net,
                COALESCE(SUM(COALESCE(debit, 0) - COALESCE(credit, 0))
                    FILTER (WHERE basis IN ('accrual', 'both')), 0) AS accrual_net
            FROM journal_lines
            WHERE account_class = 'migration_clearing'
            """).ToListAsync(ct);

        var clearingRow = clearing.Count > 0 ? clearing[0] : new ClearingRow(0m, 0m);

        // ── Owner equity total (credit-normal, cash basis) ───────────────────────────────────────
        var equityRows = await db.Database.SqlQuery<ScalarRow>(
            $"""
            SELECT COALESCE(SUM(COALESCE(credit, 0) - COALESCE(debit, 0))
                    FILTER (WHERE basis IN ('cash', 'both')), 0) AS value
            FROM journal_lines
            WHERE account_class = 'owner_equity'
            """).ToListAsync(ct);

        var ownerEquityTotal = equityRows.Count > 0 ? equityRows[0].Value : 0m;

        // ── Deposit liability total (credit-normal, cash basis) ──────────────────────────────────
        var depositRows = await db.Database.SqlQuery<ScalarRow>(
            $"""
            SELECT COALESCE(SUM(COALESCE(credit, 0) - COALESCE(debit, 0))
                    FILTER (WHERE basis IN ('cash', 'both')), 0) AS value
            FROM journal_lines
            WHERE account_class = 'deposit_liability'
            """).ToListAsync(ct);

        var depositTotal = depositRows.Count > 0 ? depositRows[0].Value : 0m;

        // ── Bank book balances per account (debit-normal, cash+both basis) ───────────────────────
        // Joined to accounts to get the stable account code. Only trust_bank + pm_operating_bank
        // account classes carry the bank dimension, so no cross-module read is needed.
        var bankRows = await db.Database.SqlQuery<BankBookRow>(
            $"""
            SELECT a.bank_account_id,
                   a.code           AS account_code,
                   COALESCE(SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0))
                            FILTER (WHERE jl.basis IN ('cash', 'both')), 0) AS book
            FROM accounts a
            LEFT JOIN journal_lines jl ON jl.account_id = a.id
            WHERE a.class IN ('trust_bank', 'pm_operating_bank')
            GROUP BY a.bank_account_id, a.code
            ORDER BY a.code
            """).ToListAsync(ct);

        var balances = bankRows
            .Select(r => new BankBookBalance(r.BankAccountId, r.AccountCode, r.Book))
            .ToList();

        // ── Held PM fees (credit-normal, cash basis, TRUST BANKS ONLY — mirrors I2's term) ────────
        // D12: filtered to bank accounts of class 'trust_bank', exactly like invariant I2's held_pm_fees
        // component (InvariantChecks.cs:65-70). NOT an org-wide pm_income sum — verification compares the
        // operator's attested held-fees figure against the fiduciary (trust-side) journal position only.
        var heldRows = await db.Database.SqlQuery<ScalarRow>(
            $"""
            SELECT COALESCE(SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0))
                    FILTER (WHERE jl.basis IN ('cash', 'both')), 0) AS value
            FROM journal_lines jl
            WHERE jl.account_class = 'pm_income'
              AND jl.bank_account_id IN (SELECT bank_account_id FROM accounts WHERE class = 'trust_bank')
            """).ToListAsync(ct);
        var heldTotal = heldRows.Count > 0 ? heldRows[0].Value : 0m;

        return new MigrationVerificationData(
            clearingRow.CashNet,
            clearingRow.AccrualNet,
            ownerEquityTotal,
            depositTotal,
            balances,
            heldTotal);
    }

    // ── Private projection types for SqlQuery ─────────────────────────────────────────────────────
    // SqlQuery<T> maps columns by snake_case → property name via the naming convention.

    private sealed record ClearingRow(decimal CashNet, decimal AccrualNet);

    private sealed record ScalarRow(decimal Value);

    private sealed record BankBookRow(Guid BankAccountId, string AccountCode, decimal Book);
}
