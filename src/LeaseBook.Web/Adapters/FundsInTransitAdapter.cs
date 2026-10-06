using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007) for the Operations module's <see cref="IFundsInTransit"/> port. Dispatches
/// the Payments <see cref="GetFundsInTransit"/> query on the ambient organization transaction and maps
/// it to Operations' own type. An organization with no online payments simply has no rows.
/// </summary>
internal sealed class FundsInTransitAdapter(ISender sender) : IFundsInTransit
{
    public async Task<IReadOnlyDictionary<Guid, TenantFundsInTransit>> GetAsync(
        IReadOnlyList<Guid> tenantIds, CancellationToken ct) =>
        (await sender.Query(new GetFundsInTransit(tenantIds), ct)).ToDictionary(
            x => x.TenantId, x => new TenantFundsInTransit(x.Amount, DateOnly.FromDateTime(x.PaidAt)));
}
