namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Consumer-owned read port (ADR-007) for what each bank account is used for. The chart of accounts
/// keeps only a bank's class, and an operating trust and a security-deposit trust are both
/// <c>trust_bank</c>; a template that must tell them apart, such as the owner contribution's (#744),
/// asks Directory, which keeps the purpose. The <b>host</b> adapter delegates to a Directory query via
/// <c>ISender</c> on the ambient org transaction, so an id the caller's org cannot see is absent from the
/// map rather than an error.
/// </summary>
public interface IBankAccountPurposes
{
    Task<IReadOnlyDictionary<Guid, BankPurpose>> GetAsync(IReadOnlyCollection<Guid> bankAccountIds, CancellationToken ct);
}
