using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007) for Accounting's <see cref="IBankAccountPurposes"/> port. Delegates to the
/// Directory <see cref="GetBankAccountPurposes"/> query via <see cref="ISender"/> on the request's ambient
/// org transaction, and maps Directory's purpose to Accounting's the way chart provisioning does.
/// </summary>
internal sealed class BankAccountPurposesAdapter(ISender sender) : IBankAccountPurposes
{
    public async Task<IReadOnlyDictionary<Guid, BankPurpose>> GetAsync(
        IReadOnlyCollection<Guid> bankAccountIds, CancellationToken ct)
    {
        var purposes = await sender.Query(new GetBankAccountPurposes(bankAccountIds), ct);
        return purposes.ToDictionary(p => p.Key, p => ChartProvisionerAdapter.Map(p.Value));
    }
}
