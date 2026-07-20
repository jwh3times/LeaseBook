using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / WP-4) for Operations' <see cref="IBankAccountInfo"/> port.
/// Dispatches Directory's <see cref="ListBankAccounts"/> query via <see cref="ISender"/> and
/// returns the first active trust bank account as the org's operating trust account.
/// <para>
/// Phase 1: assumes exactly one active trust account per org. Orgs with multiple trust accounts
/// will need a future selection UI; for now the first active trust account (by creation order,
/// newest first per <c>ListBankAccounts</c>) is used.
/// </para>
/// </summary>
internal sealed class BankAccountInfoAdapter(ISender sender) : IBankAccountInfo
{
    public async Task<(Guid OperatingBankId, string Display)> GetOperatingTrustAsync(CancellationToken ct)
    {
        var banks = await sender.Query(new ListBankAccounts(ActiveOnly: true), ct);
        var trust = banks.FirstOrDefault(b => b.Purpose == "trust")
            ?? throw new NoTrustAccountException();
        return (trust.Id, trust.Name);
    }
}
