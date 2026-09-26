using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Tenants;

/// <summary>Minimal non-system identities for host-owned resident access, under ambient org RLS.</summary>
public sealed record GetResidentNames(IReadOnlyCollection<Guid> TenantIds) : IQuery<IReadOnlyDictionary<Guid, string>>;

internal sealed class GetResidentNamesHandler(DbContext db)
    : IQueryHandler<GetResidentNames, IReadOnlyDictionary<Guid, string>>
{
    public async Task<IReadOnlyDictionary<Guid, string>> Handle(GetResidentNames query, CancellationToken ct) =>
        await db.Set<Tenant>().AsNoTracking()
            .Where(t => query.TenantIds.Contains(t.Id) && !t.IsSystem)
            .ToDictionaryAsync(t => t.Id, t => t.DisplayName, ct);
}
