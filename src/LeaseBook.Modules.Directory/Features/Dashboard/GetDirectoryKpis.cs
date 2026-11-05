using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Dashboard;

/// <summary>
/// The Directory-owned dashboard KPI (§C.6 / P45): vacancy = Σ vacant units. Period-aware scheduled
/// rent is owned by Operations because it must use the same overlap and proration policy as a rent
/// run; the host dashboard composer dispatches both without cross-module SQL.
/// </summary>
public sealed record GetDirectoryKpis : IQuery<DirectoryKpis>;

public sealed record DirectoryKpis(int Vacancy);

internal sealed class GetDirectoryKpisHandler(DbContext db, OrgCalendar calendar)
    : IQueryHandler<GetDirectoryKpis, DirectoryKpis>
{
    public async Task<DirectoryKpis> Handle(GetDirectoryKpis query, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct); // the organization's today, not the UTC date (#642)
        var vacancy = await db.Set<Unit>().AsNoTracking()
            .NotSystem()
            .CountAsync(
                unit => !db.Set<LeaseLite>().AsNoTracking().EffectiveOn(today)
                    .Any(lease => lease.UnitId == unit.Id),
                ct);

        return new DirectoryKpis(vacancy);
    }
}
