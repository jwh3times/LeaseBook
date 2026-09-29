using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Owners;

/// <summary>
/// Minimal non-system owner identities for host-owned owner access, under ambient org RLS — the owner
/// counterpart of <c>GetResidentNames</c>. A system roll-up row ("All other owners") is absent from the
/// map, which is what keeps it from ever becoming a login.
/// </summary>
public sealed record GetOwnerNames(IReadOnlyCollection<Guid> OwnerIds) : IQuery<IReadOnlyDictionary<Guid, string>>;

internal sealed class GetOwnerNamesHandler(DbContext db)
    : IQueryHandler<GetOwnerNames, IReadOnlyDictionary<Guid, string>>
{
    public async Task<IReadOnlyDictionary<Guid, string>> Handle(GetOwnerNames query, CancellationToken ct) =>
        await db.Set<Owner>().AsNoTracking()
            .NotSystem()
            .Where(o => query.OwnerIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, ct);
}
