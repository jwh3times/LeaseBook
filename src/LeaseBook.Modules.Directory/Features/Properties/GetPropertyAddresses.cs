using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Properties;

/// <summary>
/// Id → street address for exactly the non-system properties named, under ambient org RLS. The batch
/// counterpart of <c>GetPropertyLookup</c> for callers that must not learn about any property beyond
/// the ids they already hold — the owner portal passes only the property ids on the owner's own rows.
/// </summary>
public sealed record GetPropertyAddresses(IReadOnlyCollection<Guid> PropertyIds)
    : IQuery<IReadOnlyDictionary<Guid, string>>;

internal sealed class GetPropertyAddressesHandler(DbContext db)
    : IQueryHandler<GetPropertyAddresses, IReadOnlyDictionary<Guid, string>>
{
    public async Task<IReadOnlyDictionary<Guid, string>> Handle(GetPropertyAddresses query, CancellationToken ct) =>
        query.PropertyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<Property>().AsNoTracking()
                .NotSystem()
                .Where(p => query.PropertyIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Address, ct);
}
