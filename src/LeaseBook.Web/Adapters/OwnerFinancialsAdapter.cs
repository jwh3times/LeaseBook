using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / P49) for Directory's <see cref="IOwnerFinancials"/> port. Delegates to the
/// Accounting <see cref="GetOwnerBalances"/> read model via <see cref="ISender"/> and returns a batch map
/// (owner id → operating/deposits) the consumer merges in memory (M2-E12).
/// </summary>
internal sealed class OwnerFinancialsAdapter(ISender sender) : IOwnerFinancials
{
    public async Task<IReadOnlyDictionary<Guid, OwnerFinancialsRow>> BalancesAsync(CancellationToken ct)
    {
        var response = await sender.Query(new GetOwnerBalances(), ct);
        return response.Rows.ToDictionary(r => r.OwnerId, r => new OwnerFinancialsRow(r.Operating, r.Deposits));
    }
}
