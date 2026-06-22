using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Tenants;

/// <summary>
/// Tenant detail (§C.3): identity + lease + unit/property/owner context, with balance and deposit-held
/// (the "liability · not income" framing) via the Accounting port. Read-only in M2 — the inline ledger
/// composer is M3.
/// </summary>
public sealed record GetTenantDetail(Guid Id) : IQuery<TenantDetail?>;

public sealed record TenantContact(string? Email, string? Phone);

public sealed record TenantLeaseInfo(
    DateOnly? StartDate, DateOnly? EndDate, decimal Rent, decimal DepositRequired, string Status);

public sealed record TenantDetail(
    Guid Id, string DisplayName, TenantContact Contact, string Status,
    TenantLeaseInfo? Lease, string? UnitLabel, string? PropertyAddress,
    Guid? OwnerId, string? OwnerName, decimal Balance, decimal DepositHeld);

internal sealed class GetTenantDetailHandler(DbContext db, ITenantFinancials tenantFinancials)
    : IQueryHandler<GetTenantDetail, TenantDetail?>
{
    public async Task<TenantDetail?> Handle(GetTenantDetail query, CancellationToken ct)
    {
        var tenant = await db.Set<Tenant>().AsNoTracking()
            .NotSystem().FirstOrDefaultAsync(t => t.Id == query.Id, ct);
        if (tenant is null)
        {
            return null;
        }

        // The active lease → unit → property → owner chain (may be absent for a tenant with no lease).
        var context = await (
            from l in db.Set<LeaseLite>().AsNoTracking()
            join u in db.Set<Unit>().AsNoTracking() on l.UnitId equals u.Id
            join p in db.Set<Property>().AsNoTracking() on u.PropertyId equals p.Id
            join o in db.Set<Owner>().AsNoTracking() on p.OwnerId equals o.Id
            where l.TenantId == tenant.Id && l.Status == LeaseStatus.Active
            select new
            {
                l.StartDate,
                l.EndDate,
                LeaseRent = l.Rent,
                l.DepositRequired,
                l.Status,
                UnitLabel = u.Label,
                PropertyAddress = p.Address,
                OwnerId = o.Id,
                OwnerName = o.Name,
            }).FirstOrDefaultAsync(ct);

        var balances = await tenantFinancials.BalancesAsync(ct);
        var deposits = await tenantFinancials.DepositsHeldAsync(ct);

        TenantLeaseInfo? lease = context is null
            ? null
            : new TenantLeaseInfo(
                context.StartDate, context.EndDate, context.LeaseRent.Amount, context.DepositRequired.Amount,
                LeaseStatusConverter.ToDb(context.Status));

        return new TenantDetail(
            tenant.Id, tenant.DisplayName, new TenantContact(tenant.ContactEmail, tenant.ContactPhone),
            TenantStatusConverter.ToDb(tenant.Status), lease, context?.UnitLabel, context?.PropertyAddress,
            context?.OwnerId, context?.OwnerName,
            balances.GetValueOrDefault(tenant.Id), deposits.GetValueOrDefault(tenant.Id));
    }
}
