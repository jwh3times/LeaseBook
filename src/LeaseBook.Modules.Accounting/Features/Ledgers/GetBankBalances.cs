using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>Bank book balances — every bank account's book = its cash+both line balance (§C.6).</summary>
public sealed record GetBankBalances : IQuery<BankBalancesResponse>;

public sealed record BankBalancesResponse(IReadOnlyList<BankBalanceRow> Rows);

public sealed record BankBalanceRow(Guid BankAccountId, string Name, decimal Book, decimal Cleared, decimal Uncleared);

internal sealed class GetBankBalancesHandler(DbContext db) : IQueryHandler<GetBankBalances, BankBalancesResponse>
{
    public async Task<BankBalancesResponse> Handle(GetBankBalances query, CancellationToken ct)
    {
        // Raw SQL on the ambient scoped connection so RLS scopes it (M-E11). Book is DR-positive (a
        // bank is an asset). cleared = book restricted to cleared/reconciled lines (M4 / bank_line_status,
        // LEFT JOIN so absence ≡ uncleared); uncleared = book − cleared, computed below.
        // Column aliases are snake_case: SqlQuery<unmapped> maps properties to columns through the
        // snake_case naming convention (BankAccountId → bank_account_id).
        var rows = await db.Database.SqlQuery<BankBalanceSqlRow>(
            $"""
            SELECT a.bank_account_id,
                   a.name,
                   COALESCE(SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0))
                            FILTER (WHERE jl.basis IN ('cash', 'both')), 0) AS book,
                   COALESCE(SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0))
                            FILTER (WHERE jl.basis IN ('cash', 'both')
                                    AND COALESCE(s.status, 'uncleared') IN ('cleared', 'reconciled')), 0) AS cleared
            FROM accounts a
            LEFT JOIN journal_lines jl ON jl.account_id = a.id
            LEFT JOIN bank_line_status s ON s.journal_line_id = jl.id
            WHERE a.class IN ('trust_bank', 'pm_operating_bank')
            GROUP BY a.bank_account_id, a.name
            ORDER BY a.name
            """).ToListAsync(ct);

        var mapped = rows
            .Select(r => new BankBalanceRow(r.BankAccountId, r.Name, r.Book, r.Cleared, r.Book - r.Cleared))
            .ToList();

        return new BankBalancesResponse(mapped);
    }

    private sealed record BankBalanceSqlRow(Guid BankAccountId, string Name, decimal Book, decimal Cleared);
}
