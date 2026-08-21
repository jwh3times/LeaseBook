using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// All-owner balances: owner equity (operating) + deposit attribution per owner (§C.6).
/// <para>
/// <paramref name="Basis"/> selects which owner-equity lines count. This is the one report read
/// where choosing a basis changes the answer: <c>owner_equity</c> is the only account class holding
/// both cash-only and accrual-only lines — credited <c>accrual</c> when rent is charged
/// (<c>RentCharged</c>) and <c>cash</c> when the money arrives (<c>PaymentReceived</c>) — so the two
/// bases answer different questions, earned vs. collected. Deposits do not move: every
/// <c>deposit_liability</c> line is tagged <c>both</c>, so that column is identical either way.
/// (<c>tenant_receivable</c> is also single-basis, but accrual-only, so it is fixed by
/// non-existence on cash rather than offering a choice — see <c>GetDelinquencyAging</c>.)
/// </para>
/// </summary>
/// <param name="Basis">"cash" (default) or "accrual"; any other value is treated as cash.</param>
public sealed record GetOwnerBalances(string Basis = "cash") : IQuery<OwnerBalancesResponse>;

public sealed record OwnerBalancesResponse(IReadOnlyList<OwnerBalanceRow> Rows, OwnerBalancesTotals Totals);

public sealed record OwnerBalanceRow(Guid OwnerId, decimal Operating, decimal Deposits, decimal Total);

public sealed record OwnerBalancesTotals(decimal Operating, decimal Deposits, decimal Total);

internal sealed class GetOwnerBalancesHandler(DbContext db) : IQueryHandler<GetOwnerBalances, OwnerBalancesResponse>
{
    public async Task<OwnerBalancesResponse> Handle(GetOwnerBalances query, CancellationToken ct)
    {
        // operating = owner_equity on the requested basis (cash: distributable cash; accrual: earned);
        // deposits = the deposit liability attributed to the owner (prepayment lines carry no owner,
        // so they fall out) and is basis-invariant.
        var basis = query.Basis.Equals("accrual", StringComparison.OrdinalIgnoreCase) ? "accrual" : "cash";
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
                WHERE owner_id IS NOT NULL AND basis IN ({basis}, 'both')
                GROUP BY owner_id
            ) s
            ORDER BY owner_id
            """).ToListAsync(ct);

        var totals = new OwnerBalancesTotals(
            rows.Sum(r => r.Operating), rows.Sum(r => r.Deposits), rows.Sum(r => r.Total));
        return new OwnerBalancesResponse(rows, totals);
    }
}
