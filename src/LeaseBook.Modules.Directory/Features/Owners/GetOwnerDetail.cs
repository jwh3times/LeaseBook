using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Owners;

/// <summary>Owner detail (§C.3): the owner, its properties, and aggregate balances via the port.</summary>
public sealed record GetOwnerDetail(Guid Id) : IQuery<OwnerDetail?>;

public sealed record OwnerContact(string? Email, string? Phone);

public sealed record OwnerDetail(
    Guid Id, string Name, OwnerContact Contact, int? DefaultMgmtFeeBps, decimal ReserveAmount,
    IReadOnlyList<PropertyListRow> Properties, decimal Operating, decimal Deposits, decimal Total);

internal sealed class GetOwnerDetailHandler(DbContext db, IOwnerFinancials ownerFinancials)
    : IQueryHandler<GetOwnerDetail, OwnerDetail?>
{
    public async Task<OwnerDetail?> Handle(GetOwnerDetail query, CancellationToken ct)
    {
        var owner = await db.Set<Owner>().AsNoTracking()
            .NotSystem().FirstOrDefaultAsync(o => o.Id == query.Id, ct);
        if (owner is null)
        {
            return null;
        }

        // The owner's properties with unit/occupancy counts (pure Directory).
        var properties = await db.Set<Property>().AsNoTracking()
            .Where(p => p.OwnerId == owner.Id)
            .Select(p => new { p.Id, p.Address, p.City })
            .ToListAsync(ct);
        var propertyIds = properties.Select(p => p.Id).ToList();
        var unitStats = await db.Set<Unit>().AsNoTracking().Where(u => propertyIds.Contains(u.PropertyId))
            .GroupBy(u => u.PropertyId)
            .Select(g => new { PropertyId = g.Key, Total = g.Count(), Occupied = g.Count(u => u.Status == UnitStatus.Occupied) })
            .ToDictionaryAsync(x => x.PropertyId, x => x, ct);

        var propertyRows = properties.Select(p =>
        {
            var stats = unitStats.GetValueOrDefault(p.Id);
            return new PropertyListRow(p.Id, p.Address, p.City, owner.Id, owner.Name, stats?.Total ?? 0, stats?.Occupied ?? 0);
        }).ToList();

        var financials = (await ownerFinancials.BalancesAsync(ct))
            .GetValueOrDefault(owner.Id, new OwnerFinancialsRow(0, 0));

        return new OwnerDetail(
            owner.Id, owner.Name, new OwnerContact(owner.ContactEmail, owner.ContactPhone),
            owner.DefaultMgmtFeeBps, owner.ReserveAmount.Amount, propertyRows,
            financials.Operating, financials.Deposits, financials.Total);
    }
}
