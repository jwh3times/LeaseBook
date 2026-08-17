using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Units;

/// <summary>Derives unit occupancy solely from leases effective on the stated date.</summary>
internal static class UnitOccupancy
{
    public static async Task<HashSet<Guid>> OccupiedIdsAsync(
        DbContext db,
        IReadOnlyCollection<Guid> unitIds,
        DateOnly date,
        CancellationToken ct) =>
        [.. await db.Set<LeaseLite>().AsNoTracking().EffectiveOn(date)
            .Where(lease => unitIds.Contains(lease.UnitId))
            .Select(lease => lease.UnitId)
            .Distinct()
            .ToListAsync(ct)];
}
