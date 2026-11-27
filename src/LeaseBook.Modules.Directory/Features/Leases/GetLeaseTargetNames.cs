using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Leases;

/// <summary>
/// Who each lease is today — its tenant's name and id and its unit's label — for the run-detail read
/// (#573), under ambient org RLS. Any lease, in any status: a run may name a lease that has since
/// ended. A lease that does not exist, or whose tenant or unit is a system row, is absent from the
/// map, and the caller reports it as not found.
/// </summary>
public sealed record GetLeaseTargetNames(IReadOnlyCollection<Guid> LeaseIds)
    : IQuery<IReadOnlyDictionary<Guid, LeaseTargetName>>;

/// <summary>One lease's tenant and unit, as they read today.</summary>
public sealed record LeaseTargetName(Guid TenantId, string TenantName, string UnitLabel);

internal sealed class GetLeaseTargetNamesHandler(DbContext db)
    : IQueryHandler<GetLeaseTargetNames, IReadOnlyDictionary<Guid, LeaseTargetName>>
{
    public async Task<IReadOnlyDictionary<Guid, LeaseTargetName>> Handle(
        GetLeaseTargetNames query, CancellationToken ct) =>
        await (
            from l in db.Set<LeaseLite>().AsNoTracking()
            where query.LeaseIds.Contains(l.Id)
            join u in db.Set<Unit>().AsNoTracking().NotSystem() on l.UnitId equals u.Id
            join t in db.Set<Tenant>().AsNoTracking().NotSystem() on l.TenantId equals t.Id
            select new { l.Id, Name = new LeaseTargetName(t.Id, t.DisplayName, u.Label) })
            .ToDictionaryAsync(row => row.Id, row => row.Name, ct);
}
