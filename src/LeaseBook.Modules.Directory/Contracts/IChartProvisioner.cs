using LeaseBook.Modules.Directory.Domain;

namespace LeaseBook.Modules.Directory.Contracts;

/// <summary>
/// Consumer-owned port (ADR-007 / P49) for the cross-module <b>call</b> Directory makes when a bank
/// account is created: provision the matching chart-of-accounts account in Accounting. Directory depends
/// only on this abstraction — never on the Accounting assembly. The <b>host</b> implements it with a thin
/// adapter that delegates to <c>IChartOfAccounts.ProvisionAsync</c> (idempotent by code), running on the
/// request's ambient org transaction. Same shape as a read port, for a call.
/// </summary>
public interface IChartProvisioner
{
    Task ProvisionBankAccountAsync(Guid bankAccountId, string name, BankPurpose purpose, CancellationToken ct);
}
