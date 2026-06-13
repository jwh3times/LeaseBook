using LeaseBook.Modules.Directory.Contracts;
using AccountingContracts = LeaseBook.Modules.Accounting.Contracts;
using DirBankPurpose = LeaseBook.Modules.Directory.Domain.BankPurpose;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter for the Directory→Accounting provisioning seam (ADR-007 / P49 / §C.8). Implements
/// Directory's consumer-owned <see cref="IChartProvisioner"/> port by delegating to the Accounting
/// module's <see cref="AccountingContracts.IChartOfAccounts"/> — the cross-module reference lives here in
/// the host, never in Directory. DI-scoped, so it shares the request's ambient DbContext + org
/// transaction; provisioning is idempotent by account code, so a re-create is harmless.
/// </summary>
internal sealed class ChartProvisionerAdapter(AccountingContracts.IChartOfAccounts chartOfAccounts) : IChartProvisioner
{
    public Task ProvisionBankAccountAsync(Guid bankAccountId, string name, DirBankPurpose purpose, CancellationToken ct) =>
        chartOfAccounts.ProvisionAsync([new AccountingContracts.BankAccountSpec(bankAccountId, name, Map(purpose))], ct);

    private static AccountingContracts.BankPurpose Map(DirBankPurpose purpose) => purpose switch
    {
        DirBankPurpose.Trust => AccountingContracts.BankPurpose.Trust,
        DirBankPurpose.Deposit => AccountingContracts.BankPurpose.Deposit,
        DirBankPurpose.Operating => AccountingContracts.BankPurpose.Operating,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown bank purpose."),
    };
}
