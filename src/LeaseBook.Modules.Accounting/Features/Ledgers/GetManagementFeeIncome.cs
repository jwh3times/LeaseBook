using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// PM-facing management-fee income summary: credits less debits on <c>pm_income</c> lines grouped by
/// property, for a given calendar month (§M5 report #8). PM isolation: no owner_id dimension surfaces
/// here — pm_income lines carry no owner, and the query is explicitly PM-facing (not owner-facing).
/// Opening positions (<c>OpeningBalance</c>, and their voids via the reversal link) are excluded:
/// openings are position, not in-period fee flow (WP-7 D10); the batched <c>BalanceForward</c> seed
/// path stays included deliberately (demo-golden safety).
/// </summary>
public sealed record GetManagementFeeIncome(int Year, int Month) : IQuery<MgmtFeeIncomeResponse>;

public sealed record MgmtFeeIncomeResponse(IReadOnlyList<MgmtFeeIncomeRow> Rows);

/// <summary>Per-property management-fee income for the requested month.</summary>
/// <param name="PropertyId">Property dimension from the journal line (may be null for unattributed lines).</param>
/// <param name="Amount">Net credit (credit − debit) over <c>pm_income</c> lines for the month.</param>
public sealed record MgmtFeeIncomeRow(Guid? PropertyId, decimal Amount);

internal sealed class GetManagementFeeIncomeValidator : AbstractValidator<GetManagementFeeIncome>
{
    public GetManagementFeeIncomeValidator()
    {
        RuleFor(x => x.Year).InclusiveBetween(2000, 2100);
        RuleFor(x => x.Month).InclusiveBetween(1, 12);
    }
}

internal sealed class GetManagementFeeIncomeHandler(DbContext db)
    : IQueryHandler<GetManagementFeeIncome, MgmtFeeIncomeResponse>
{
    public async Task<MgmtFeeIncomeResponse> Handle(GetManagementFeeIncome query, CancellationToken ct)
    {
        var start = new DateOnly(query.Year, query.Month, 1);
        var end = start.AddMonths(1);

        var rows = await db.Database.SqlQuery<MgmtFeeIncomeRow>(
            $"""
            SELECT jl.property_id, SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS amount
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            LEFT JOIN journal_entries orig ON orig.id = e.reverses_entry_id
            WHERE jl.account_class = 'pm_income'
              AND jl.basis IN ('cash', 'both')
              AND e.entry_date >= {start}
              AND e.entry_date < {end}
              AND COALESCE(orig.event_type, e.event_type) <> 'OpeningBalance'
            GROUP BY jl.property_id
            HAVING SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) <> 0
            ORDER BY jl.property_id
            """).ToListAsync(ct);

        return new MgmtFeeIncomeResponse(rows);
    }
}
