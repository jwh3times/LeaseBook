using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.ManagementFee;

/// <summary>
/// Directory-internal resolver for the effective fee rate (P44): property override ?? owner default.
/// Reads only Directory's own owner/property columns (the org query filter applies). No fee math.
/// </summary>
internal sealed class ManagementFeeConfig(DbContext db) : IManagementFeeConfig
{
    public async Task<int?> GetEffectiveFeeBpsAsync(Guid ownerId, Guid? propertyId, CancellationToken ct)
    {
        if (propertyId is { } id)
        {
            var propertyOverride = await db.Set<Property>()
                .Where(p => p.Id == id).Select(p => p.MgmtFeeBps).FirstOrDefaultAsync(ct);
            if (propertyOverride is not null)
            {
                return propertyOverride;
            }
        }

        return await db.Set<Owner>()
            .Where(o => o.Id == ownerId).Select(o => o.DefaultMgmtFeeBps).FirstOrDefaultAsync(ct);
    }
}
