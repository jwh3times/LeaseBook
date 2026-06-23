using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Reporting;

/// <summary>
/// Returns the active-lease schedule for a calendar month: all active leases whose term overlaps
/// the period, joined to tenant / unit / property / owner. Used by the M6 rent-run strategy
/// (WP-2) via the <c>ILeaseScheduleData</c> port + host adapter (ADR-007 / ADR-019).
/// <para>
/// <b>Active</b> = <see cref="LeaseStatus.Active"/>. <b>Overlaps period</b> = start is null or
/// start &lt;= last day of period AND end is null or end &gt;= first day of period.
/// </para>
/// <para>
/// <see cref="Domain.Unit.IsSystem"/> / <see cref="Property.IsSystem"/> / <see cref="Tenant.IsSystem"/>
/// are filtered via <c>NotSystem()</c> on every join (M5-prep convention).
/// </para>
/// </summary>
public sealed record GetActiveLeaseSchedule(int Year, int Month) : IQuery<LeaseScheduleResponse>;

/// <summary>The full set of schedule rows for the period.</summary>
public sealed record LeaseScheduleResponse(IReadOnlyList<LeaseScheduleRow> Rows);

/// <summary>
/// One row per active lease that overlaps the requested period.
/// </summary>
/// <param name="LeaseId">The lease's stable id.</param>
/// <param name="TenantId">FK to tenants.</param>
/// <param name="PropertyId">FK to properties.</param>
/// <param name="OwnerId">FK to owners.</param>
/// <param name="UnitId">FK to units.</param>
/// <param name="TenantName">Display name of the tenant.</param>
/// <param name="UnitLabel">Unit label (e.g. "#2B").</param>
/// <param name="Rent">Monthly rent on the lease.</param>
/// <param name="StartDate">Lease start date; null = month-to-month with no stated start.</param>
/// <param name="EndDate">Lease end date; null = open-ended.</param>
public sealed record LeaseScheduleRow(
    Guid LeaseId,
    Guid TenantId,
    Guid PropertyId,
    Guid OwnerId,
    Guid? UnitId,
    string TenantName,
    string UnitLabel,
    decimal Rent,
    DateOnly? StartDate,
    DateOnly? EndDate);

internal sealed class GetActiveLeaseScheduleHandler(DbContext db)
    : IQueryHandler<GetActiveLeaseSchedule, LeaseScheduleResponse>
{
    public async Task<LeaseScheduleResponse> Handle(GetActiveLeaseSchedule query, CancellationToken ct)
    {
        var periodStart = new DateOnly(query.Year, query.Month, 1);
        var periodEnd = new DateOnly(query.Year, query.Month, DateTime.DaysInMonth(query.Year, query.Month));

        // Active leases whose term overlaps the period, joined to unit → property (for owner) + tenant.
        // NotSystem() applied to Unit, Property, Tenant (M5-prep convention — never leak system rows).
        var rows = await (
            from l in db.Set<LeaseLite>().AsNoTracking()
                        .Where(l => l.Status == LeaseStatus.Active)
                        // Overlaps: start <= periodEnd AND end >= periodStart (treating null as unbounded).
                        .Where(l => (l.StartDate == null || l.StartDate <= periodEnd)
                                 && (l.EndDate == null || l.EndDate >= periodStart))
            join u in db.Set<Unit>().AsNoTracking().NotSystem() on l.UnitId equals u.Id
            join p in db.Set<Property>().AsNoTracking().NotSystem() on u.PropertyId equals p.Id
            join t in db.Set<Tenant>().AsNoTracking().NotSystem() on l.TenantId equals t.Id
            select new LeaseScheduleRow(
                l.Id,
                l.TenantId,
                p.Id,
                p.OwnerId,
                (Guid?)u.Id,
                t.DisplayName,
                u.Label,
                l.Rent.Amount,
                l.StartDate,
                l.EndDate))
            .ToListAsync(ct);

        return new LeaseScheduleResponse(rows);
    }
}
