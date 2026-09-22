using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Owners;

/// <summary>
/// id → the address on file for each requested non-system owner that has one. Statements are
/// delivered only to this address, never to one a request supplies, so an owner's statement cannot
/// be redirected to another inbox. An owner with no usable address is absent from the map rather
/// than mapped to null — "absent" is the delivery refusal.
/// </summary>
public sealed record GetOwnerDeliveryAddresses(IReadOnlyCollection<Guid> OwnerIds)
    : IQuery<IReadOnlyDictionary<Guid, string>>;

internal sealed class GetOwnerDeliveryAddressesHandler(DbContext db)
    : IQueryHandler<GetOwnerDeliveryAddresses, IReadOnlyDictionary<Guid, string>>
{
    public async Task<IReadOnlyDictionary<Guid, string>> Handle(GetOwnerDeliveryAddresses query, CancellationToken ct)
    {
        var rows = await db.Set<Owner>().AsNoTracking()
            .NotSystem()
            .Where(o => query.OwnerIds.Contains(o.Id) && o.ContactEmail != null)
            .Select(o => new { o.Id, o.ContactEmail })
            .ToListAsync(ct);

        return rows
            .Where(o => !string.IsNullOrWhiteSpace(o.ContactEmail))
            .ToDictionary(o => o.Id, o => o.ContactEmail!.Trim());
    }
}
