using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Dashboard;

/// <summary>
/// The Directory-owned dashboard KPIs (§C.6 / P45): vacancy = Σ vacant units; collectedTarget = Σ
/// scheduled rent over active leases (the month's billing target). Reads Directory's own tables; the
/// host dashboard composer dispatches it via <c>ISender</c> (no cross-module SQL).
/// </summary>
public sealed record GetDirectoryKpis : IQuery<DirectoryKpis>;

public sealed record DirectoryKpis(int Vacancy, decimal CollectedTarget);

internal sealed class GetDirectoryKpisHandler(DbContext db) : IQueryHandler<GetDirectoryKpis, DirectoryKpis>
{
    public async Task<DirectoryKpis> Handle(GetDirectoryKpis query, CancellationToken ct)
    {
        var vacancy = await db.Set<Unit>().AsNoTracking()
            .CountAsync(u => !u.IsSystem && u.Status == UnitStatus.Vacant, ct);

        // collectedTarget = Σ active-lease rent — the scheduled monthly billing. Money sums in decimal.
        var activeRents = await db.Set<LeaseLite>().AsNoTracking()
            .Where(l => l.Status == LeaseStatus.Active)
            .Select(l => l.Rent)
            .ToListAsync(ct);
        var collectedTarget = Money.Sum(activeRents).Amount;

        return new DirectoryKpis(vacancy, collectedTarget);
    }
}
