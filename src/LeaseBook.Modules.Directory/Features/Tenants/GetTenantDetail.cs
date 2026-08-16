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

/// <summary>
/// The tenant's lease effective today. Carries <see cref="Id"/> and <see cref="UnitId"/> as well as the
/// editable fields because <c>UpdateLease</c> replaces the whole lease: a client editing one field
/// has to send the rest back unchanged, and it can only do that if the read returned them (WP-6).
/// <para>
/// The five <c>*Override</c> fields are nullable by design — null means "inherit the org default"
/// for that field, so null is a meaningful value here and must not be conflated with zero.
/// </para>
/// </summary>
public sealed record TenantLeaseInfo(
    Guid Id, Guid UnitId,
    DateOnly? StartDate, DateOnly? EndDate, decimal Rent, decimal DepositRequired, string Status,
    int? LateFeeRentDueDayOverride,
    int? LateFeeGraceDaysOverride,
    string? LateFeeKindOverride,
    decimal? LateFeeAmountOverride,
    int? LateFeeRateBpsOverride);

public sealed record TenantDetail(
    Guid Id, string DisplayName, TenantContact Contact, string Status,
    TenantLeaseInfo? Lease, string? UnitLabel, string? PropertyAddress,
    Guid? OwnerId, string? OwnerName, decimal Balance, decimal DepositHeld);

internal sealed class GetTenantDetailHandler(
    DbContext db,
    ITenantFinancials tenantFinancials,
    TimeProvider clock)
    : IQueryHandler<GetTenantDetail, TenantDetail?>
{
    public async Task<TenantDetail?> Handle(GetTenantDetail query, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var tenant = await db.Set<Tenant>().AsNoTracking()
            .NotSystem().FirstOrDefaultAsync(t => t.Id == query.Id, ct);
        if (tenant is null)
        {
            return null;
        }

        // The lease effective today → unit → property → owner chain (may be absent).
        var context = await (
            from l in db.Set<LeaseLite>().AsNoTracking().EffectiveOn(today)
            join u in db.Set<Unit>().AsNoTracking() on l.UnitId equals u.Id
            join p in db.Set<Property>().AsNoTracking() on u.PropertyId equals p.Id
            join o in db.Set<Owner>().AsNoTracking() on p.OwnerId equals o.Id
            where l.TenantId == tenant.Id
            select new
            {
                LeaseId = l.Id,
                l.UnitId,
                l.StartDate,
                l.EndDate,
                LeaseRent = l.Rent,
                l.DepositRequired,
                l.Status,
                l.LateFeeRentDueDayOverride,
                l.LateFeeGraceDaysOverride,
                l.LateFeeKindOverride,
                l.LateFeeAmountOverride,
                l.LateFeeRateBpsOverride,
                UnitLabel = u.Label,
                PropertyAddress = p.Address,
                OwnerId = o.Id,
                OwnerName = o.Name,
            }).SingleOrDefaultAsync(ct);

        var balances = await tenantFinancials.BalancesAsync(ct);
        var deposits = await tenantFinancials.DepositsHeldAsync(ct);

        TenantLeaseInfo? lease = context is null
            ? null
            : new TenantLeaseInfo(
                context.LeaseId, context.UnitId,
                context.StartDate, context.EndDate, context.LeaseRent.Amount, context.DepositRequired.Amount,
                LeaseStatusConverter.ToDb(context.Status),
                context.LateFeeRentDueDayOverride,
                context.LateFeeGraceDaysOverride,
                context.LateFeeKindOverride is null
                    ? null
                    : LateFeeKindConverter.ToDb(context.LateFeeKindOverride.Value),
                context.LateFeeAmountOverride,
                context.LateFeeRateBpsOverride);

        return new TenantDetail(
            tenant.Id, tenant.DisplayName, new TenantContact(tenant.ContactEmail, tenant.ContactPhone),
            TenantStatusConverter.ToDb(tenant.Status), lease, context?.UnitLabel, context?.PropertyAddress,
            context?.OwnerId, context?.OwnerName,
            balances.GetValueOrDefault(tenant.Id), deposits.GetValueOrDefault(tenant.Id));
    }
}
