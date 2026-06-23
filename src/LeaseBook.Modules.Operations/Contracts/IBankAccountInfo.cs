namespace LeaseBook.Modules.Operations.Contracts;

/// <summary>
/// Read-direction cross-module port (ADR-007 / WP-4). Operations declares the interface;
/// the host adapter (<c>BankAccountInfoAdapter</c>) implements it by dispatching
/// Directory's <c>ListBankAccounts</c> query via <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>.
/// <para>
/// Phase 1 assumes exactly one active trust bank account per org (the operating trust account
/// to which disbursement bank-withdrawal lines are posted). The adapter picks the first active
/// trust account; orgs with multiple trust accounts require a future selection UI.
/// </para>
/// </summary>
public interface IBankAccountInfo
{
    /// <summary>Returns the id and display name of the org's active operating trust bank account.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no active trust bank account exists.</exception>
    Task<(Guid OperatingBankId, string Display)> GetOperatingTrustAsync(CancellationToken ct);
}
