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
        // "cash" is passed explicitly and the port's `basis` is deliberately NOT forwarded, even
        // though GetOwnerBalances now accepts one (#230). Disbursement pays out this figure: accrual
        // equity counts rent that has been charged but not collected, so forwarding an accrual basis
        // here would disburse money the trust account has not received. The basis a report may be
        // *read* on is not the basis money may be *moved* on.
        var response = await sender.Query(new GetOwnerBalances("cash"), ct);
        var ownerSet = new HashSet<Guid>(ownerIds);
        return response.Rows
            .Where(r => ownerSet.Contains(r.OwnerId))
            .ToDictionary(r => r.OwnerId, r => r.Operating);
    }
}
