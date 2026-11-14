using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Shared;

/// <summary>Who owned a property on a date, from its append-only ownership transfers.</summary>
internal static class PropertyOwnerHistory
{
    /// <summary>
    /// The owner of <paramref name="propertyId"/> on <paramref name="date"/>, holding a shared lock on the
    /// property row until the caller's transaction commits. An ownership transfer takes the matching
    /// exclusive lock, so the answer is read wholly before or wholly after a concurrent transfer instead of
    /// racing its current-owner and history writes. The latest transfer effective on or before the date
    /// names the owner; before the first transfer it is that transfer's seller; with no transfers it is
    /// <paramref name="currentOwner"/>.
    /// </summary>
    public static async Task<Guid> OwnerOnAsync(
        DbContext db, Guid propertyId, DateOnly date, Guid currentOwner, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM properties WHERE id = {propertyId} FOR SHARE""")
            .SingleAsync(ct);

        var effectiveOwner = await db.Set<PropertyOwnershipTransfer>().AsNoTracking()
            .Where(transfer => transfer.PropertyId == propertyId && transfer.EffectiveDate <= date)
            .OrderByDescending(transfer => transfer.EffectiveDate)
            .Select(transfer => (Guid?)transfer.ToOwnerId)
            .FirstOrDefaultAsync(ct);
        effectiveOwner ??= await db.Set<PropertyOwnershipTransfer>().AsNoTracking()
            .Where(transfer => transfer.PropertyId == propertyId)
            .OrderBy(transfer => transfer.EffectiveDate)
            .Select(transfer => (Guid?)transfer.FromOwnerId)
            .FirstOrDefaultAsync(ct);

        return effectiveOwner ?? currentOwner;
    }
}
