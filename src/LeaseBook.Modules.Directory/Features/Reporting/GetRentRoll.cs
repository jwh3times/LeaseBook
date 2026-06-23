using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Reporting;

/// <summary>
/// Rent roll: all non-system units with their property address, current tenant name (if occupied), lease
/// rent, and unit status (§M5 report #6). Pure Directory data — no Accounting tables touched (ADR-007).
/// EF LINQ over Directory's own DbSets (org query filter applies for free).
/// </summary>
public sealed record GetRentRoll : IQuery<RentRollResponse>;

public sealed record RentRollResponse(IReadOnlyList<RentRollRow> Rows);

/// <summary>One row per non-system unit.</summary>
/// <param name="UnitId">The unit's stable id.</param>
/// <param name="Property">Property address string.</param>
/// <param name="Tenant">Current tenant display name, or null when vacant/unavailable.</param>
/// <param name="Rent">Lease rent for occupied units; unit scheduled rent for vacant/unavailable units.</param>
/// <param name="Status">Unit status as a lowercase string (e.g. "occupied", "vacant", "unavailable").</param>
public sealed record RentRollRow(Guid UnitId, string Property, string? Tenant, decimal Rent, string Status);

internal sealed class GetRentRollHandler(DbContext db) : IQueryHandler<GetRentRoll, RentRollResponse>
{
    public async Task<RentRollResponse> Handle(GetRentRoll query, CancellationToken ct)
    {
        // Left-join unit → active lease → tenant. Non-system units/properties only:
        // NotSystem() applied to Unit, Property, and Tenant queryables.
        var rows = await (
            from u in db.Set<Unit>().AsNoTracking().NotSystem()
            join p in db.Set<Property>().AsNoTracking().NotSystem() on u.PropertyId equals p.Id
            join l in db.Set<LeaseLite>().AsNoTracking()
                            .Where(l => l.Status == LeaseStatus.Active)
                        on u.Id equals l.UnitId into leases
            from l in leases.DefaultIfEmpty()
            join t in db.Set<Tenant>().AsNoTracking().NotSystem()
                        on l.TenantId equals t.Id into tenants
            from t in tenants.DefaultIfEmpty()
            orderby p.Address, u.Label
            select new RentRollRow(
                u.Id,
                p.Address,
                t == null ? null : t.DisplayName,
                l == null ? u.Rent.Amount : l.Rent.Amount,
                UnitStatusConverter.ToDb(u.Status)))
            .ToListAsync(ct);

        return new RentRollResponse(rows);
    }
}
