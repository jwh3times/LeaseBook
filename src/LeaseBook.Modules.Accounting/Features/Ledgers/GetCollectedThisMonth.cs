using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Cash collected this accounting month: Σ owner-equity <b>credits</b> (cash/both basis) on entries
/// dated within the month — the rent/income recognized into owner equity on a cash basis (§C.6,
/// dashboard <c>collectedMtd</c>). Reads Accounting's own <c>journal_lines</c>, so it belongs here; the
/// host dashboard composer (WP-05) dispatches it for the current month.
/// </summary>
public sealed record GetCollectedThisMonth(int Year, int Month) : IQuery<decimal>;

internal sealed class GetCollectedThisMonthHandler(DbContext db) : IQueryHandler<GetCollectedThisMonth, decimal>
{
    public async Task<decimal> Handle(GetCollectedThisMonth query, CancellationToken ct)
    {
        var start = new DateOnly(query.Year, query.Month, 1);
        var end = start.AddMonths(1);

        var rows = await db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(jl.credit, 0)), 0) AS "Value"
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            WHERE jl.account_class = 'owner_equity'
              AND jl.basis IN ('cash', 'both')
              AND e.entry_date >= {start} AND e.entry_date < {end}
            """).ToListAsync(ct);

        return rows.Count > 0 ? rows[0] : 0m;
    }
}
