using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Properties;

/// <summary>
/// A lightweight id → address lookup over <b>non-system</b> properties. Mirrors <c>GetOwnerLookup</c>
/// (same batch-map shape, ADR-007). Used by the Reporting host adapter to resolve property display
/// names for statement enrichment without the Reporting module touching Directory's tables directly.
/// </summary>
public sealed record GetPropertyLookup : IQuery<IReadOnlyList<PropertyLookupRow>>;

public sealed record PropertyLookupRow(Guid Id, string Address);

internal sealed class GetPropertyLookupHandler(DbContext db)
    : IQueryHandler<GetPropertyLookup, IReadOnlyList<PropertyLookupRow>>
{
    public async Task<IReadOnlyList<PropertyLookupRow>> Handle(GetPropertyLookup query, CancellationToken ct)
    {
        var rows = await db.Set<Property>().AsNoTracking()
            .NotSystem()
            .Select(p => new PropertyLookupRow(p.Id, p.Address))
            .ToListAsync(ct);
        return rows;
    }
}
