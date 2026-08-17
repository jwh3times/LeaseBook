using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Units;

/// <summary>Units for a property (§C.3) — a small flat list (≤ a few dozen), not paged.</summary>
public sealed record ListUnits(Guid PropertyId) : IQuery<IReadOnlyList<UnitRow>>;

public sealed record UnitRow(
    Guid Id,
    Guid PropertyId,
    string Label,
    decimal Rent,
    string Occupancy,
    string Availability)
{
    public static UnitRow From(Unit unit, bool occupied) =>
        new(
            unit.Id,
            unit.PropertyId,
            unit.Label,
            unit.Rent.Amount,
            occupied ? "occupied" : "vacant",
            UnitAvailabilityConverter.ToDb(unit.Availability));
}

internal sealed class ListUnitsHandler(DbContext db, TimeProvider clock)
    : IQueryHandler<ListUnits, IReadOnlyList<UnitRow>>
{
    public async Task<IReadOnlyList<UnitRow>> Handle(ListUnits query, CancellationToken ct)
    {
        var units = await db.Set<Unit>().AsNoTracking()
            .Where(u => u.PropertyId == query.PropertyId).NotSystem()
            .OrderBy(u => u.Label)
            .ToListAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var occupiedIds = await UnitOccupancy.OccupiedIdsAsync(db, units.Select(unit => unit.Id).ToList(), today, ct);
        return [.. units.Select(unit => UnitRow.From(unit, occupiedIds.Contains(unit.Id)))];
    }
}
