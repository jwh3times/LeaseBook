using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007) for Accounting's <see cref="IPropertyOwnership"/> port. Delegates to the
/// Directory <see cref="GetPropertyOwnerOn"/> query via <see cref="ISender"/>, so the cross-module
/// reference lives in the host. DI-scoped, so the read rides the request's ambient org transaction and
/// its property-row lock is held until the posting commits.
/// </summary>
internal sealed class PropertyOwnershipAdapter(ISender sender) : IPropertyOwnership
{
    public async Task<Guid?> GetOwnerOnAsync(Guid propertyId, DateOnly date, CancellationToken ct) =>
        (await sender.Query(new GetPropertyOwnerOn(propertyId, date), ct))?.OwnerId;
}
