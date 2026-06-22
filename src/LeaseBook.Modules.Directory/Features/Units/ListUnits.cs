using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Units;

/// <summary>Units for a property (§C.3) — a small flat list (≤ a few dozen), not paged.</summary>
public sealed record ListUnits(Guid PropertyId) : IQuery<IReadOnlyList<UnitRow>>;

public sealed record UnitRow(Guid Id, Guid PropertyId, string Label, decimal Rent, string Status)
{
    public static UnitRow From(Unit u) =>
        new(u.Id, u.PropertyId, u.Label, u.Rent.Amount, UnitStatusConverter.ToDb(u.Status));
}

internal sealed class ListUnitsHandler(DbContext db) : IQueryHandler<ListUnits, IReadOnlyList<UnitRow>>
{
    public async Task<IReadOnlyList<UnitRow>> Handle(ListUnits query, CancellationToken ct)
    {
        var units = await db.Set<Unit>().AsNoTracking()
            .Where(u => u.PropertyId == query.PropertyId).NotSystem()
            .OrderBy(u => u.Label)
            .ToListAsync(ct);
        return [.. units.Select(UnitRow.From)];
    }
}
