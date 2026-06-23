using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / WP-4) for Operations' <see cref="IOwnerEquityBalances"/> port.
/// Dispatches Accounting's <see cref="GetOwnerBalances"/> query via <see cref="ISender"/> and
/// returns the cash-basis operating equity (the <c>Operating</c> field = owner_equity cash+both)
/// for the requested owner ids, filtered in memory (the Accounting query returns all owners).
/// </summary>
internal sealed class OwnerEquityBalancesAdapter(ISender sender) : IOwnerEquityBalances
{
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetAsync(
        IReadOnlyList<Guid> ownerIds, string basis, CancellationToken ct)
    {
        var response = await sender.Query(new GetOwnerBalances(), ct);
        var ownerSet = new HashSet<Guid>(ownerIds);
        return response.Rows
            .Where(r => ownerSet.Contains(r.OwnerId))
            .ToDictionary(r => r.OwnerId, r => r.Operating);
    }
}
