using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Properties;

/// <summary>Property detail (§C.3): the property + its owner, units, and tenants (the prototype page).</summary>
public sealed record GetPropertyDetail(Guid Id) : IQuery<PropertyDetail?>;

public sealed record PropertyDetail(
    Guid Id, string Address, string? City, string? State, string? Zip,
    Guid OwnerId, string OwnerName, int? MgmtFeeBps,
    IReadOnlyList<UnitRow> Units, IReadOnlyList<TenantListRow> Tenants);

internal sealed class GetPropertyDetailHandler(DbContext db, ITenantFinancials tenantFinancials)
    : IQueryHandler<GetPropertyDetail, PropertyDetail?>
{
    public async Task<PropertyDetail?> Handle(GetPropertyDetail query, CancellationToken ct)
    {
        var property = await db.Set<Property>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == query.Id && !p.IsSystem, ct);
        if (property is null)
        {
            return null;
        }

        var ownerName = await db.Set<Owner>().AsNoTracking()
            .Where(o => o.Id == property.OwnerId).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? "";

        var units = await db.Set<Unit>().AsNoTracking()
            .Where(u => u.PropertyId == property.Id).OrderBy(u => u.Label).ToListAsync(ct);
        var unitRows = units.Select(UnitRow.From).ToList();

        // The property's tenants: those with an active lease on one of this property's units.
        var unitIds = units.Select(u => u.Id).ToList();
        var tenantRows = await (
            from l in db.Set<LeaseLite>().AsNoTracking()
            join t in db.Set<Tenant>().AsNoTracking() on l.TenantId equals t.Id
            join u in db.Set<Unit>().AsNoTracking() on l.UnitId equals u.Id
            where unitIds.Contains(l.UnitId) && l.Status == LeaseStatus.Active && !t.IsSystem
            select new { t.Id, t.DisplayName, t.Status, u.Label, l.Rent })
            .ToListAsync(ct);

        var balances = tenantRows.Count > 0 ? await tenantFinancials.BalancesAsync(ct) : EmptyBalances;
        var tenants = tenantRows.Select(t => new TenantListRow(
            t.Id, t.DisplayName, t.Label, t.Rent.Amount,
            balances.GetValueOrDefault(t.Id), TenantStatusConverter.ToDb(t.Status))).ToList();

        return new PropertyDetail(
            property.Id, property.Address, property.City, property.State, property.Zip,
            property.OwnerId, ownerName, property.MgmtFeeBps, unitRows, tenants);
    }

    private static readonly IReadOnlyDictionary<Guid, decimal> EmptyBalances =
        new Dictionary<Guid, decimal>();
}
