using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>Host adapter (ADR-007) for Directory's <see cref="IBankClearanceStatus"/> port. Delegates to
/// the Accounting <see cref="GetBankBalances"/> read model via <see cref="ISender"/> and returns a batch
/// map (bank id → uncleared count) for the deactivation guard.</summary>
internal sealed class BankClearanceStatusAdapter(ISender sender) : IBankClearanceStatus
{
    public async Task<IReadOnlyDictionary<Guid, int>> UnclearedCountsAsync(CancellationToken ct)
    {
        var response = await sender.Query(new GetBankBalances(), ct);
        return response.Rows.ToDictionary(r => r.BankAccountId, r => r.UnclearedCount);
    }
}
