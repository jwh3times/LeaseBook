using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Net tenant payments received in a calendar month on the cash basis. The metric is anchored to the
/// <c>PaymentReceived</c> business event and its trust-bank movement, so owner contributions and
/// other owner-equity credits cannot be mistaken for tenant receipts. Receipt month—not charge due
/// month—controls attribution; reversals net in the month they are posted.
/// </summary>
public sealed record GetTenantPaymentsReceived(int Year, int Month) : IQuery<decimal>;

internal sealed class GetTenantPaymentsReceivedHandler(DbContext db)
    : IQueryHandler<GetTenantPaymentsReceived, decimal>
{
    public async Task<decimal> Handle(GetTenantPaymentsReceived query, CancellationToken ct)
    {
        var start = new DateOnly(query.Year, query.Month, 1);
        var end = start.AddMonths(1);

        var rows = await db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0)), 0) AS "Value"
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            LEFT JOIN journal_entries original ON original.id = e.reverses_entry_id
            WHERE jl.account_class = 'trust_bank'
              AND jl.basis IN ('cash', 'both')
              AND COALESCE(original.event_type, e.event_type) = 'PaymentReceived'
              AND e.entry_date >= {start} AND e.entry_date < {end}
            """).ToListAsync(ct);

        return rows.Count > 0 ? rows[0] : 0m;
    }
}
