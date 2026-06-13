using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Properties;

/// <summary>Paged property list (§C.3) — pure Directory: owner name + unit count + occupancy.</summary>
public sealed record ListProperties(int? Page, int? PageSize, string? Q, string? Sort)
    : IQuery<PagedResponse<PropertyListRow>>;

public sealed record PropertyListRow(
    Guid Id, string Address, string? City, Guid OwnerId, string OwnerName, int Units, int Occupied);

internal sealed class ListPropertiesHandler(DbContext db) : IQueryHandler<ListProperties, PagedResponse<PropertyListRow>>
{
    public async Task<PagedResponse<PropertyListRow>> Handle(ListProperties query, CancellationToken ct)
    {
        var page = PageParams.Normalize(query.Page, query.PageSize, query.Q, query.Sort);

        var properties = db.Set<Property>().AsNoTracking().Where(p => !p.IsSystem);
        if (page.Q is { } q)
        {
            var like = q.ToLower();
            properties = properties.Where(p => p.Address.ToLower().Contains(like));
        }

        var total = await properties.CountAsync(ct);
        var (field, descending) = page.ParseSort("address");
        properties = (field, descending) switch
        {
            ("address", true) => properties.OrderByDescending(p => p.Address),
            _ => properties.OrderBy(p => p.Address),
        };

        var rows = await properties.Skip(page.Skip).Take(page.PageSize)
            .Select(p => new { p.Id, p.Address, p.City, p.OwnerId })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var ownerIds = rows.Select(r => r.OwnerId).Distinct().ToList();

        var ownerNames = await db.Set<Owner>().AsNoTracking().Where(o => ownerIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, ct);
        var unitStats = await db.Set<Unit>().AsNoTracking().Where(u => ids.Contains(u.PropertyId))
            .GroupBy(u => u.PropertyId)
            .Select(g => new
            {
                PropertyId = g.Key,
                Total = g.Count(),
                Occupied = g.Count(u => u.Status == UnitStatus.Occupied),
            })
            .ToDictionaryAsync(x => x.PropertyId, x => x, ct);

        var items = rows.Select(r =>
        {
            var stats = unitStats.GetValueOrDefault(r.Id);
            return new PropertyListRow(
                r.Id, r.Address, r.City, r.OwnerId, ownerNames.GetValueOrDefault(r.OwnerId, ""),
                stats?.Total ?? 0, stats?.Occupied ?? 0);
        }).ToList();

        return new PagedResponse<PropertyListRow>(items, total, page.Page, page.PageSize);
    }
}
