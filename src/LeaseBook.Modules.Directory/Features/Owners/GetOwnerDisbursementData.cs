using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Owners;

/// <summary>
/// Returns all non-system owners with the fields the M6 disbursement run (WP-4) needs:
/// reserve amount, default management-fee bps, and display name.
/// </summary>
public sealed record GetOwnerDisbursementData : IQuery<IReadOnlyList<DirOwnerDisbursementRow>>;

/// <summary>One owner row for the disbursement run. Owned by Directory; host adapter maps to the Operations DTO.</summary>
public sealed record DirOwnerDisbursementRow(
    Guid OwnerId,
    string Name,
    decimal ReserveAmount,
    int? DefaultMgmtFeeBps);

internal sealed class GetOwnerDisbursementDataHandler(DbContext db)
    : IQueryHandler<GetOwnerDisbursementData, IReadOnlyList<DirOwnerDisbursementRow>>
{
    public async Task<IReadOnlyList<DirOwnerDisbursementRow>> Handle(
        GetOwnerDisbursementData query, CancellationToken ct)
    {
        return await db.Set<Owner>().AsNoTracking().NotSystem()
            .OrderBy(o => o.Name)
            .Select(o => new DirOwnerDisbursementRow(
                o.Id,
                o.Name,
                o.ReserveAmount.Amount,
                o.DefaultMgmtFeeBps))
            .ToListAsync(ct);
    }
}
