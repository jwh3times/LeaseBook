using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>Bank book balances — every bank account's book = its cash+both line balance (§C.6).</summary>
public sealed record GetBankBalances : IQuery<BankBalancesResponse>;

public sealed record BankBalancesResponse(IReadOnlyList<BankBalanceRow> Rows);

public sealed record BankBalanceRow(Guid BankAccountId, string Name, decimal Book);

internal sealed class GetBankBalancesHandler(DbContext db) : IQueryHandler<GetBankBalances, BankBalancesResponse>
{
    public async Task<BankBalancesResponse> Handle(GetBankBalances query, CancellationToken ct)
    {
        // Raw SQL on the ambient scoped connection so RLS scopes it (M-E11). Book is DR-positive (a
        // bank is an asset). cleared/uncleared columns arrive in M4 — left out, not null-stubbed.
        // Column aliases are snake_case: SqlQuery<unmapped> maps properties to columns through the
        // snake_case naming convention (BankAccountId → bank_account_id).
        var rows = await db.Database.SqlQuery<BankBalanceRow>(
            $"""
            SELECT a.bank_account_id,
                   a.name,
                   COALESCE(SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0))
                            FILTER (WHERE jl.basis IN ('cash', 'both')), 0) AS book
            FROM accounts a
            LEFT JOIN journal_lines jl ON jl.account_id = a.id
            WHERE a.class IN ('trust_bank', 'pm_operating_bank')
            GROUP BY a.bank_account_id, a.name
            ORDER BY a.name
            """).ToListAsync(ct);

        return new BankBalancesResponse(rows);
    }
}
