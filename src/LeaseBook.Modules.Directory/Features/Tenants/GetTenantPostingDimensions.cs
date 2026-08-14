using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Tenants;

/// <summary>
/// Resolves a tenant's posting dimensions from its single <b>active lease</b> (P58): the owner/property/unit a
/// ledger posting carries. A thin intra-Directory LINQ read of Directory's own tables — the host's
/// <c>ITenantPostingDimensions</c> adapter dispatches it for the Accounting composer (ADR-007). Returns
/// <see langword="null"/> when the tenant has no active lease (or is a system row), so the Accounting
/// command rejects rather than mis-attributing a post (M3-E3).
/// </summary>
public sealed record GetTenantPostingDimensions(Guid TenantId, DateOnly Date) : IQuery<TenantPostingDimensionsView?>;

public sealed record TenantPostingDimensionsView(Guid OwnerId, Guid PropertyId, Guid? UnitId);

public sealed class GetTenantPostingDimensionsValidator : AbstractValidator<GetTenantPostingDimensions>
{
    public GetTenantPostingDimensionsValidator() => RuleFor(q => q.TenantId).NotEmpty();
}

internal sealed class GetTenantPostingDimensionsHandler(DbContext db)
    : IQueryHandler<GetTenantPostingDimensions, TenantPostingDimensionsView?>
{
    public async Task<TenantPostingDimensionsView?> Handle(GetTenantPostingDimensions query, CancellationToken ct)
    {
        var current = await (
            from t in db.Set<Tenant>().AsNoTracking().NotSystem()
            join l in db.Set<LeaseLite>().AsNoTracking() on t.Id equals l.TenantId
            join u in db.Set<Unit>().AsNoTracking() on l.UnitId equals u.Id
            join p in db.Set<Property>().AsNoTracking() on u.PropertyId equals p.Id
            where t.Id == query.TenantId && l.Status == LeaseStatus.Active
            select new TenantPostingDimensionsView(p.OwnerId, p.Id, u.Id))
            .SingleOrDefaultAsync(ct);
        if (current is null)
        {
            return null;
        }

        // Hold a shared property-row lock until this posting transaction commits. Ownership transfer
        // takes the matching exclusive lock, so attribution is resolved wholly before or wholly after
        // the transfer instead of racing its current-owner/history write.
        await db.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM properties WHERE id = {current.PropertyId} FOR SHARE""")
            .SingleAsync(ct);

        var effectiveOwner = await db.Set<PropertyOwnershipTransfer>().AsNoTracking()
            .Where(transfer => transfer.PropertyId == current.PropertyId
                && transfer.EffectiveDate <= query.Date)
            .OrderByDescending(transfer => transfer.EffectiveDate)
            .Select(transfer => (Guid?)transfer.ToOwnerId)
            .FirstOrDefaultAsync(ct);
        if (effectiveOwner is null)
        {
            effectiveOwner = await db.Set<PropertyOwnershipTransfer>().AsNoTracking()
                .Where(transfer => transfer.PropertyId == current.PropertyId)
                .OrderBy(transfer => transfer.EffectiveDate)
                .Select(transfer => (Guid?)transfer.FromOwnerId)
                .FirstOrDefaultAsync(ct);
        }

        return current with { OwnerId = effectiveOwner ?? current.OwnerId };
    }
}
