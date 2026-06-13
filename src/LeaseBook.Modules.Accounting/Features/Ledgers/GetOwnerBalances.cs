using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>All-owner balances: cash-basis equity (operating) + deposit attribution per owner (§C.6).</summary>
public sealed record GetOwnerBalances : IQuery<OwnerBalancesResponse>;

public sealed record OwnerBalancesResponse(IReadOnlyList<OwnerBalanceRow> Rows, OwnerBalancesTotals Totals);

public sealed record OwnerBalanceRow(Guid OwnerId, decimal Operating, decimal Deposits, decimal Total);

public sealed record OwnerBalancesTotals(decimal Operating, decimal Deposits, decimal Total);

internal sealed class GetOwnerBalancesHandler(DbContext db) : IQueryHandler<GetOwnerBalances, OwnerBalancesResponse>
{
    public async Task<OwnerBalancesResponse> Handle(GetOwnerBalances query, CancellationToken ct)
    {
        // operating = owner_equity cash+both (the owner's distributable cash); deposits = the deposit
        // liability attributed to the owner (prepayment lines carry no owner, so they fall out).
        var rows = await db.Database.SqlQuery<OwnerBalanceRow>(
            $"""
            SELECT owner_id, operating, deposits, operating + deposits AS total
            FROM (
                SELECT owner_id,
                       COALESCE(SUM(CASE WHEN account_class = 'owner_equity'
                                         THEN COALESCE(credit, 0) - COALESCE(debit, 0) ELSE 0 END), 0) AS operating,
                       COALESCE(SUM(CASE WHEN account_class = 'deposit_liability'
                                         THEN COALESCE(credit, 0) - COALESCE(debit, 0) ELSE 0 END), 0) AS deposits
                FROM journal_lines
                WHERE owner_id IS NOT NULL AND basis IN ('cash', 'both')
                GROUP BY owner_id
            ) s
            ORDER BY owner_id
            """).ToListAsync(ct);

        var totals = new OwnerBalancesTotals(
            rows.Sum(r => r.Operating), rows.Sum(r => r.Deposits), rows.Sum(r => r.Total));
        return new OwnerBalancesResponse(rows, totals);
    }
}
