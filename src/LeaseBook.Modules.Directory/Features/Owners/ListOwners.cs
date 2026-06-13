using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Owners;

/// <summary>Paged owner roster (§C.3), excluding system roll-up rows (P40). Financials via the port.</summary>
public sealed record ListOwners(int? Page, int? PageSize, string? Q, string? Sort) : IQuery<PagedResponse<OwnerListRow>>;

public sealed record OwnerListRow(
    Guid Id, string Name, string? Initials, int Units, int Properties,
    decimal Operating, decimal Deposits, decimal Total);

internal sealed class ListOwnersHandler(DbContext db, IOwnerFinancials ownerFinancials)
    : IQueryHandler<ListOwners, PagedResponse<OwnerListRow>>
{
    public async Task<PagedResponse<OwnerListRow>> Handle(ListOwners query, CancellationToken ct)
    {
        var page = PageParams.Normalize(query.Page, query.PageSize, query.Q, query.Sort);

        // Directory data via EF LINQ — the org filter applies for free; system rows excluded (P40/M2-E2).
        var owners = db.Set<Owner>().AsNoTracking().Where(o => !o.IsSystem);
        if (page.Q is { } q)
        {
            var like = q.ToLower();
            owners = owners.Where(o => o.Name.ToLower().Contains(like));
        }

        var total = await owners.CountAsync(ct);
        var (field, descending) = page.ParseSort("name");
        owners = (field, descending) switch
        {
            ("name", true) => owners.OrderByDescending(o => o.Name),
            _ => owners.OrderBy(o => o.Name),
        };

        var rows = await owners.Skip(page.Skip).Take(page.PageSize)
            .Select(o => new { o.Id, o.Name, o.Initials })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();

        // Intra-Directory aggregates: property + unit counts per owner, batched (no per-row queries).
        var propertyCounts = await db.Set<Property>().AsNoTracking()
            .Where(p => ids.Contains(p.OwnerId))
            .GroupBy(p => p.OwnerId).Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OwnerId, x => x.Count, ct);
        var unitCounts = await (
                from u in db.Set<Unit>().AsNoTracking()
                join p in db.Set<Property>().AsNoTracking() on u.PropertyId equals p.Id
                where ids.Contains(p.OwnerId)
                group u by p.OwnerId into g
                select new { OwnerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OwnerId, x => x.Count, ct);

        // Cross-module financials through the port — one batch map, merged in memory (P49/M2-E12).
        var financials = await ownerFinancials.BalancesAsync(ct);

        var items = rows.Select(r =>
        {
            var f = financials.GetValueOrDefault(r.Id, new OwnerFinancialsRow(0, 0));
            return new OwnerListRow(
                r.Id, r.Name, r.Initials,
                unitCounts.GetValueOrDefault(r.Id), propertyCounts.GetValueOrDefault(r.Id),
                f.Operating, f.Deposits, f.Total);
        }).ToList();

        return new PagedResponse<OwnerListRow>(items, total, page.Page, page.PageSize);
    }
}
