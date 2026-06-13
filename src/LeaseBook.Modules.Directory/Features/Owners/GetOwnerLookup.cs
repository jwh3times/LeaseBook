using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Owners;

/// <summary>
/// A lightweight id → (name, isSystem) lookup over <b>all</b> owners (system rows included). Unlike
/// <c>ListOwners</c> it does not exclude system rows — the dashboard composer (WP-05) needs them so it can
/// name the hero rows and identify the <c>AggregateOwners</c> roll-up to relabel "All other owners" (P40)
/// and exclude it from <c>ownersPayable</c> (P41).
/// </summary>
public sealed record GetOwnerLookup : IQuery<IReadOnlyList<OwnerLookupRow>>;

public sealed record OwnerLookupRow(Guid Id, string Name, bool IsSystem);

internal sealed class GetOwnerLookupHandler(DbContext db) : IQueryHandler<GetOwnerLookup, IReadOnlyList<OwnerLookupRow>>
{
    public async Task<IReadOnlyList<OwnerLookupRow>> Handle(GetOwnerLookup query, CancellationToken ct)
    {
        var rows = await db.Set<Owner>().AsNoTracking()
            .Select(o => new OwnerLookupRow(o.Id, o.Name, o.IsSystem))
            .ToListAsync(ct);
        return rows;
    }
}
