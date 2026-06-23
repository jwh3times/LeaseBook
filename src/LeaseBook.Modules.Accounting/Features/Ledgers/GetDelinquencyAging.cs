using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Tenant-receivable balance bucketed by age of the open charge as of <see cref="AsOf"/> (§M5 report #7).
/// Accrual basis only — <c>tenant_receivable</c> is an accrual account; cash-basis organisations still
/// need this report for compliance (NC fiduciary). Only tenants with a non-zero receivable surface.
/// </summary>
public sealed record GetDelinquencyAging(DateOnly AsOf) : IQuery<DelinquencyResponse>;

public sealed record DelinquencyResponse(IReadOnlyList<DelinquencyRow> Rows);

/// <summary>Per-tenant aging buckets (all amounts are positive = owed to PM).</summary>
/// <param name="TenantId">The owing tenant.</param>
/// <param name="Current">Balance on charges dated within the current month (0–0 days old).</param>
/// <param name="D1_30">Balance on charges 1–30 days past the <see cref="GetDelinquencyAging.AsOf"/> date.</param>
/// <param name="D31_60">31–60 days past due.</param>
/// <param name="D61_90">61–90 days past due.</param>
/// <param name="Over90">More than 90 days past due.</param>
/// <param name="Total">Sum of all buckets.</param>
/// <param name="OldestAgeDays">
/// The actual age in days of the oldest past-due charge (age_days &gt; 0 and net_owed &gt; 0).
/// Zero means no past-due balance (only a Current bucket). Used by the late-fee run strategy
/// to gate grace-period eligibility against the real age rather than a bucket floor.
/// </param>
public sealed record DelinquencyRow(
    Guid TenantId,
    decimal Current,
    decimal D1_30,
    decimal D31_60,
    decimal D61_90,
    decimal Over90,
    decimal Total,
    int OldestAgeDays);

internal sealed class GetDelinquencyAgingValidator : AbstractValidator<GetDelinquencyAging>
{
    public GetDelinquencyAgingValidator()
    {
        RuleFor(x => x.AsOf).NotEmpty();
    }
}

internal sealed class GetDelinquencyAgingHandler(DbContext db)
    : IQueryHandler<GetDelinquencyAging, DelinquencyResponse>
{
    public async Task<DelinquencyResponse> Handle(GetDelinquencyAging query, CancellationToken ct)
    {
        var asOf = query.AsOf;

        // Net receivable per (tenant, entry) — accrual basis only (tenant_receivable is accrual).
        // Age = AsOf − entry_date; bucket by how far past due each charge entry is.
        // Credits (payments/credits) reduce the oldest open charges first in a real AR system;
        // here we bucket by net running balance per entry_date to stay simple and correct for reporting.
        var rows = await db.Database.SqlQuery<DelinquencyRow>(
            $"""
            WITH receivable_by_entry AS (
                SELECT jl.tenant_id,
                       e.entry_date,
                       ({asOf} - e.entry_date)::int AS age_days,
                       SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0)) AS net_owed
                FROM journal_lines jl
                JOIN journal_entries e ON e.id = jl.entry_id
                WHERE jl.account_class = 'tenant_receivable'
                  AND jl.basis IN ('accrual', 'both')
                  AND jl.tenant_id IS NOT NULL
                  AND e.entry_date <= {asOf}
                GROUP BY jl.tenant_id, e.entry_date
            )
            SELECT
                tenant_id,
                COALESCE(SUM(CASE WHEN age_days <= 0 THEN net_owed ELSE 0 END), 0) AS current,
                COALESCE(SUM(CASE WHEN age_days BETWEEN 1  AND 30  THEN net_owed ELSE 0 END), 0) AS d1_30,
                COALESCE(SUM(CASE WHEN age_days BETWEEN 31 AND 60  THEN net_owed ELSE 0 END), 0) AS d31_60,
                COALESCE(SUM(CASE WHEN age_days BETWEEN 61 AND 90  THEN net_owed ELSE 0 END), 0) AS d61_90,
                COALESCE(SUM(CASE WHEN age_days > 90               THEN net_owed ELSE 0 END), 0) AS over90,
                SUM(net_owed) AS total,
                COALESCE(MAX(CASE WHEN age_days > 0 AND net_owed > 0 THEN age_days ELSE NULL END), 0) AS oldest_age_days
            FROM receivable_by_entry
            GROUP BY tenant_id
            HAVING SUM(net_owed) > 0
            ORDER BY tenant_id
            """).ToListAsync(ct);

        return new DelinquencyResponse(rows);
    }
}
