using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Tenants;

/// <summary>Paged tenant list (§C.3): name / unit / rent / balance / status. Balance via the port.</summary>
public sealed record ListTenants(int? Page, int? PageSize, string? Q, string? Sort)
    : IQuery<PagedResponse<TenantListRow>>;

public sealed record TenantListRow(
    Guid Id, string DisplayName, string? UnitLabel, decimal Rent, decimal Balance, string Status);

internal sealed class ListTenantsHandler(DbContext db, ITenantFinancials tenantFinancials)
    : IQueryHandler<ListTenants, PagedResponse<TenantListRow>>
{
    public async Task<PagedResponse<TenantListRow>> Handle(ListTenants query, CancellationToken ct)
    {
        var page = PageParams.Normalize(query.Page, query.PageSize, query.Q, query.Sort);

        var tenants = db.Set<Tenant>().AsNoTracking().NotSystem();
        if (page.Q is { } q)
        {
            var like = q.ToLower();
            tenants = tenants.Where(t => t.DisplayName.ToLower().Contains(like));
        }

        var total = await tenants.CountAsync(ct);
        var (field, descending) = page.ParseSort("displayName");
        tenants = (field, descending) switch
        {
            ("displayName", true) => tenants.OrderByDescending(t => t.DisplayName),
            _ => tenants.OrderBy(t => t.DisplayName),
        };

        var rows = await tenants.Skip(page.Skip).Take(page.PageSize)
            .Select(t => new { t.Id, t.DisplayName, t.Status })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();

        var leaseByTenant = await LeaseLookup.ActiveByTenantAsync(db, ids, ct);
        var balances = await tenantFinancials.BalancesAsync(ct);

        var items = rows.Select(r =>
        {
            var lease = leaseByTenant.GetValueOrDefault(r.Id);
            return new TenantListRow(
                r.Id, r.DisplayName, lease?.UnitLabel, lease?.Rent ?? 0m,
                balances.GetValueOrDefault(r.Id), TenantStatusConverter.ToDb(r.Status));
        }).ToList();

        return new PagedResponse<TenantListRow>(items, total, page.Page, page.PageSize);
    }
}

/// <summary>Shared helper: each tenant's active lease projected to its unit label + rent.</summary>
internal static class LeaseLookup
{
    public sealed record ActiveLease(Guid UnitId, string UnitLabel, decimal Rent);

    public static async Task<Dictionary<Guid, ActiveLease>> ActiveByTenantAsync(
        DbContext db, IReadOnlyCollection<Guid> tenantIds, CancellationToken ct)
    {
        var rows = await (
            from l in db.Set<LeaseLite>().AsNoTracking()
            join u in db.Set<Unit>().AsNoTracking() on l.UnitId equals u.Id
            where tenantIds.Contains(l.TenantId) && l.Status == LeaseStatus.Active
            select new { l.TenantId, l.UnitId, u.Label, l.Rent })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.TenantId)
            .ToDictionary(g => g.Key, g =>
            {
                var x = g.First();
                return new ActiveLease(x.UnitId, x.Label, x.Rent.Amount);
            });
    }
}
