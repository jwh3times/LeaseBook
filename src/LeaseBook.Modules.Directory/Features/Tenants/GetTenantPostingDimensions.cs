using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Tenants;

/// <summary>
/// Resolves a tenant's posting dimensions from its <b>active lease</b> (P58): the owner/property/unit a
/// ledger posting carries. A thin intra-Directory LINQ read of Directory's own tables — the host's
/// <c>ITenantPostingDimensions</c> adapter dispatches it for the Accounting composer (ADR-007). Returns
/// <see langword="null"/> when the tenant has no active lease (or is a system row), so the Accounting
/// command rejects rather than mis-attributing a post (M3-E3).
/// </summary>
public sealed record GetTenantPostingDimensions(Guid TenantId) : IQuery<TenantPostingDimensionsView?>;

public sealed record TenantPostingDimensionsView(Guid OwnerId, Guid PropertyId, Guid? UnitId);

public sealed class GetTenantPostingDimensionsValidator : AbstractValidator<GetTenantPostingDimensions>
{
    public GetTenantPostingDimensionsValidator() => RuleFor(q => q.TenantId).NotEmpty();
}

internal sealed class GetTenantPostingDimensionsHandler(DbContext db)
    : IQueryHandler<GetTenantPostingDimensions, TenantPostingDimensionsView?>
{
    public Task<TenantPostingDimensionsView?> Handle(GetTenantPostingDimensions query, CancellationToken ct) =>
        (
            from t in db.Set<Tenant>().AsNoTracking()
            join l in db.Set<LeaseLite>().AsNoTracking() on t.Id equals l.TenantId
            join u in db.Set<Unit>().AsNoTracking() on l.UnitId equals u.Id
            join p in db.Set<Property>().AsNoTracking() on u.PropertyId equals p.Id
            join o in db.Set<Owner>().AsNoTracking() on p.OwnerId equals o.Id
            where t.Id == query.TenantId && !t.IsSystem && l.Status == LeaseStatus.Active
            select new TenantPostingDimensionsView(o.Id, p.Id, u.Id))
        .FirstOrDefaultAsync(ct);
}
