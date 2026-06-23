using LeaseBook.Modules.Accounting.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Diagnostics;

/// <summary>
/// Executable I1–I5 checks (§C.7) over the ambient org's journal via raw SQL on the scoped connection
/// (RLS-scoped, M-E11). Shared by the CLI sweep and the test harness; this is the future nightly
/// sweep body (P33).
/// </summary>
internal sealed class InvariantChecks(DbContext db) : IInvariantChecks
{
    public async Task<IReadOnlyList<InvariantViolation>> CheckCoreAsync(CancellationToken ct)
    {
        var violations = new List<InvariantViolation>();
        violations.AddRange(await CheckEntriesBalanceAsync(ct));
        violations.AddRange(await CheckTrustEquationAsync(ct));
        violations.AddRange(await CheckPmIncomeIsolationAsync(ct));
        violations.AddRange(await CheckDepositLiabilitiesNonNegativeAsync(ct));
        violations.AddRange(await CheckMigrationClearingBalancedAsync(ct));
        return violations;
    }

    // I1: for cash and accrual, debits == credits over {basis, both} for every entry.
    public async Task<IReadOnlyList<InvariantViolation>> CheckEntriesBalanceAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<UnbalancedEntry>(
            $"""
            SELECT entry_id, basis_name, sum_debit, sum_credit FROM (
                SELECT entry_id, 'cash' AS basis_name,
                       SUM(COALESCE(debit, 0)) FILTER (WHERE basis IN ('cash', 'both')) AS sum_debit,
                       SUM(COALESCE(credit, 0)) FILTER (WHERE basis IN ('cash', 'both')) AS sum_credit
                FROM journal_lines GROUP BY entry_id
                UNION ALL
                SELECT entry_id, 'accrual',
                       SUM(COALESCE(debit, 0)) FILTER (WHERE basis IN ('accrual', 'both')),
                       SUM(COALESCE(credit, 0)) FILTER (WHERE basis IN ('accrual', 'both'))
                FROM journal_lines GROUP BY entry_id
            ) s
            WHERE sum_debit <> sum_credit
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I1",
                $"entry {r.EntryId} does not balance in {r.BasisName}: debits {r.SumDebit:0.00} != credits {r.SumCredit:0.00}"))
            .ToList();
    }

    // I2: book(B) == owner equity + deposit liabilities + prepayments + held PM fees, per trust bank.
    public async Task<IReadOnlyList<InvariantViolation>> CheckTrustEquationAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<EquationVariance>(
            $"""
            SELECT bank_account_id, book - (owner_equity + deposit_liabilities + prepayments + held_pm_fees) AS variance
            FROM (
                SELECT jl.bank_account_id,
                       COALESCE(SUM(CASE WHEN jl.account_class = 'trust_bank'
                                         THEN COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0) ELSE 0 END), 0) AS book,
                       COALESCE(SUM(CASE WHEN jl.account_class = 'owner_equity'
                                         THEN COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0) ELSE 0 END), 0) AS owner_equity,
                       COALESCE(SUM(CASE WHEN a.code = 'security_deposits_held'
                                         THEN COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0) ELSE 0 END), 0) AS deposit_liabilities,
                       COALESCE(SUM(CASE WHEN a.code = 'tenant_prepayments'
                                         THEN COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0) ELSE 0 END), 0) AS prepayments,
                       COALESCE(SUM(CASE WHEN jl.account_class = 'pm_income'
                                         THEN COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0) ELSE 0 END), 0) AS held_pm_fees
                FROM journal_lines jl JOIN accounts a ON a.id = jl.account_id
                WHERE jl.basis IN ('cash', 'both')
                  AND jl.bank_account_id IN (SELECT bank_account_id FROM accounts WHERE class = 'trust_bank')
                GROUP BY jl.bank_account_id
            ) e
            WHERE book - (owner_equity + deposit_liabilities + prepayments + held_pm_fees) <> 0
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I2",
                $"trust equation off by {r.Variance:0.00} on bank {r.BankAccountId}"))
            .ToList();
    }

    // I3: no pm_income line carries an owner dimension (DB CHECK backs this; the sweep proves zero rows).
    public async Task<IReadOnlyList<InvariantViolation>> CheckPmIncomeIsolationAsync(CancellationToken ct)
    {
        var ids = await db.Database.SqlQuery<Guid>(
            $"""SELECT id AS "Value" FROM journal_lines WHERE account_class = 'pm_income' AND owner_id IS NOT NULL""")
            .ToListAsync(ct);

        return ids
            .Select(id => new InvariantViolation("I3", $"pm_income line {id} carries an owner_id"))
            .ToList();
    }

    // I4: held deposit and held prepayment are each ≥ 0 per tenant.
    public async Task<IReadOnlyList<InvariantViolation>> CheckDepositLiabilitiesNonNegativeAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<NegativeLiability>(
            $"""
            SELECT jl.tenant_id, a.code, SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS held
            FROM journal_lines jl JOIN accounts a ON a.id = jl.account_id
            WHERE a.code IN ('security_deposits_held', 'tenant_prepayments') AND jl.basis IN ('cash', 'both')
            GROUP BY jl.tenant_id, a.code
            HAVING SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) < 0
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I4",
                $"held {r.Code} for tenant {r.TenantId} is negative ({r.Held:0.00})"))
            .ToList();
    }

    // I5: migration_clearing nets to $0 per basis — non-zero residual is a quantified import discrepancy
    // (ADR-020 / M7). The invariant is vacuous (no rows) for orgs that haven't imported, so it is safe
    // to include in the core sweep for all orgs.
    public async Task<IReadOnlyList<InvariantViolation>> CheckMigrationClearingBalancedAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ClearingVariance>(
            $"""
            SELECT basis_name, net
            FROM (
                SELECT 'cash' AS basis_name,
                       SUM(COALESCE(debit, 0) - COALESCE(credit, 0)) FILTER (WHERE basis IN ('cash', 'both')) AS net
                FROM journal_lines WHERE account_class = 'migration_clearing'
                UNION ALL
                SELECT 'accrual',
                       SUM(COALESCE(debit, 0) - COALESCE(credit, 0)) FILTER (WHERE basis IN ('accrual', 'both'))
                FROM journal_lines WHERE account_class = 'migration_clearing'
            ) s
            WHERE net IS NOT NULL AND net <> 0
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I5",
                $"migration_clearing does not net to $0 in {r.BasisName}: residual {r.Net:0.00}"))
            .ToList();
    }

    private sealed record UnbalancedEntry(Guid EntryId, string BasisName, decimal SumDebit, decimal SumCredit);

    private sealed record EquationVariance(Guid BankAccountId, decimal Variance);

    private sealed record NegativeLiability(Guid? TenantId, string Code, decimal Held);

    private sealed record ClearingVariance(string BasisName, decimal Net);
}
