using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Tenant-receivable balance bucketed by age of the open charge as of <see cref="AsOf"/> (§M5 report #7).
/// Accrual basis only — <c>tenant_receivable</c> is an accrual account; cash-basis organisations still
/// need this report for compliance (NC fiduciary). Tenants with an open charge or unapplied general
/// credit surface; credit is reported separately and never makes a bucket negative.
/// </summary>
public sealed record GetDelinquencyAging(DateOnly AsOf) : IQuery<DelinquencyResponse>;

public sealed record DelinquencyResponse(IReadOnlyList<DelinquencyRow> Rows);

/// <summary>Per-tenant aging buckets (all amounts are positive = owed to PM).</summary>
/// <param name="TenantId">The owing tenant.</param>
/// <param name="Current">Remaining charges whose due date has not passed.</param>
/// <param name="D1_30">Remaining charges 1–30 days past due as of the report date.</param>
/// <param name="D31_60">31–60 days past due.</param>
/// <param name="D61_90">61–90 days past due.</param>
/// <param name="Over90">More than 90 days past due.</param>
/// <param name="Total">Gross remaining open charges; the sum of all buckets.</param>
/// <param name="UnappliedCredit">General credit left after every open charge is satisfied.</param>
/// <param name="OldestAgeDays">
/// The actual age in days of the oldest past-due charge (age_days &gt; 0 and net_owed &gt; 0).
/// Zero means no past-due balance (only a Current bucket).
/// </param>
public sealed record DelinquencyRow(
    Guid TenantId,
    decimal Current,
    decimal D1_30,
    decimal D31_60,
    decimal D61_90,
    decimal Over90,
    decimal Total,
    decimal UnappliedCredit,
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
        var allocation = await ReceivableAllocationReader.ReadAsync(db, query.AsOf, ct);
        var chargesByTenant = allocation.OpenCharges.ToLookup(charge => charge.TenantId);
        var tenantIds = chargesByTenant
            .Select(group => group.Key)
            .Concat(allocation.UnappliedCredits.Keys)
            .Distinct()
            .Order();

        var rows = tenantIds.Select(tenantId =>
        {
            decimal current = 0;
            decimal d1_30 = 0;
            decimal d31_60 = 0;
            decimal d61_90 = 0;
            decimal over90 = 0;
            var oldestAgeDays = 0;

            foreach (var charge in chargesByTenant[tenantId])
            {
                var ageDays = query.AsOf.DayNumber - charge.DueDate.DayNumber;
                if (ageDays <= 0) current += charge.RemainingAmount;
                else if (ageDays <= 30) d1_30 += charge.RemainingAmount;
                else if (ageDays <= 60) d31_60 += charge.RemainingAmount;
                else if (ageDays <= 90) d61_90 += charge.RemainingAmount;
                else over90 += charge.RemainingAmount;

                if (ageDays > oldestAgeDays) oldestAgeDays = ageDays;
            }

            allocation.UnappliedCredits.TryGetValue(tenantId, out var unappliedCredit);
            return new DelinquencyRow(
                tenantId,
                current,
                d1_30,
                d31_60,
                d61_90,
                over90,
                current + d1_30 + d31_60 + d61_90 + over90,
                unappliedCredit,
                oldestAgeDays);
        }).ToList();

        return new DelinquencyResponse(rows);
    }
}
