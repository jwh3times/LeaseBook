using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// The trust equation per trust bank (§C.6 / invariant I2): for cash+both lines,
/// book = owner equity + deposit liabilities + prepayments + held PM fees. Variance must be 0.00.
/// <para><see cref="AsOf"/> (inclusive) bounds every component to entries dated on or before it, so the
/// equation can be read as of a period end; null ⇒ all-time (as-of-now). Because each posting balances
/// per bank within a single dated entry, a date-bounded prefix still nets to variance 0.00 (WP-8).</para>
/// </summary>
public sealed record GetTrustEquation(DateOnly? AsOf = null) : IQuery<TrustEquationResponse>;

public sealed record TrustEquationResponse(IReadOnlyList<TrustEquationRow> Rows);

public sealed record TrustEquationRow(
    Guid BankAccountId, decimal Book, decimal OwnerEquity, decimal DepositLiabilities,
    decimal Prepayments, decimal HeldPmFees, decimal Variance);

internal sealed class GetTrustEquationHandler(DbContext db) : IQueryHandler<GetTrustEquation, TrustEquationResponse>
{
    public async Task<TrustEquationResponse> Handle(GetTrustEquation query, CancellationToken ct)
    {
        // Only trust banks (class trust_bank) carry the equation; the PM operating bank is excluded
        // (P30 / §C.8). Every component is tagged with the bank's id (P36), so one grouping by
        // bank_account_id yields book and all counter-positions.
        var rows = await db.Database.SqlQuery<TrustEquationSqlRow>(
            $"""
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
            FROM journal_lines jl
            JOIN accounts a ON a.id = jl.account_id
            JOIN journal_entries e ON e.id = jl.entry_id
            WHERE jl.basis IN ('cash', 'both')
              AND jl.bank_account_id IN (SELECT bank_account_id FROM accounts WHERE class = 'trust_bank')
              AND ({query.AsOf}::date IS NULL OR e.entry_date <= {query.AsOf})
            GROUP BY jl.bank_account_id
            ORDER BY jl.bank_account_id
            """).ToListAsync(ct);

        var result = rows
            .Select(r => new TrustEquationRow(
                r.BankAccountId, r.Book, r.OwnerEquity, r.DepositLiabilities, r.Prepayments, r.HeldPmFees,
                Variance: r.Book - (r.OwnerEquity + r.DepositLiabilities + r.Prepayments + r.HeldPmFees)))
            .ToList();

        return new TrustEquationResponse(result);
    }

    private sealed record TrustEquationSqlRow(
        Guid BankAccountId, decimal Book, decimal OwnerEquity, decimal DepositLiabilities,
        decimal Prepayments, decimal HeldPmFees);
}
