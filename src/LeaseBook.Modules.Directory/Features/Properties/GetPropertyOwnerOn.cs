using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Properties;

/// <summary>
/// The owner of one non-system property on one date (#744): the owner after every ownership transfer
/// effective on or before it. The host's <c>IPropertyOwnership</c> adapter dispatches it for an Accounting
/// command that names an owner and a property, so the posting lands on the owner the property belonged
/// to that day. A property the org cannot see, or a system property, answers <see langword="null"/>.
/// </summary>
public sealed record GetPropertyOwnerOn(Guid PropertyId, DateOnly Date) : IQuery<PropertyOwnerView?>;

public sealed record PropertyOwnerView(Guid OwnerId);

public sealed class GetPropertyOwnerOnValidator : AbstractValidator<GetPropertyOwnerOn>
{
    public GetPropertyOwnerOnValidator() => RuleFor(q => q.PropertyId).NotEmpty();
}

internal sealed class GetPropertyOwnerOnHandler(DbContext db) : IQueryHandler<GetPropertyOwnerOn, PropertyOwnerView?>
{
    public async Task<PropertyOwnerView?> Handle(GetPropertyOwnerOn query, CancellationToken ct)
    {
        var currentOwner = await db.Set<Property>().AsNoTracking().NotSystem()
            .Where(p => p.Id == query.PropertyId)
            .Select(p => (Guid?)p.OwnerId)
            .SingleOrDefaultAsync(ct);
        return currentOwner is { } owner
            ? new PropertyOwnerView(await PropertyOwnerHistory.OwnerOnAsync(db, query.PropertyId, query.Date, owner, ct))
            : null;
    }
}
