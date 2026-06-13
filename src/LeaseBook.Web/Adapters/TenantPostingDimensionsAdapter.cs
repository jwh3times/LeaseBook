using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / P49 / P58) for Accounting's <see cref="ITenantPostingDimensions"/> port.
/// Delegates to the Directory <see cref="GetTenantPostingDimensions"/> query via <see cref="ISender"/> —
/// the cross-module reference lives here in the host, never in Accounting. DI-scoped, so the dispatched
/// read rides the request's ambient org transaction (RLS applies); a tenant the caller's org can't see
/// resolves to <see langword="null"/>, which the command turns into a validation rejection (M3-E3).
/// </summary>
internal sealed class TenantPostingDimensionsAdapter(ISender sender) : ITenantPostingDimensions
{
    public async Task<TenantPostingDimensions?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        var view = await sender.Query(new GetTenantPostingDimensions(tenantId), ct);
        return view is null ? null : new TenantPostingDimensions(view.OwnerId, view.PropertyId, view.UnitId);
    }
}
