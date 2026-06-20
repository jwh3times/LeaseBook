namespace LeaseBook.Modules.Accounting.Posting;

/// <summary>
/// The per-account reconciliation lock (M4 / §B.3 / P63). A finalized reconciliation for an
/// (account, year, month) bars further bank postings into that account-month. The posting service
/// consults this for every bank-account line before persisting, alongside the per-org period check.
/// </summary>
internal interface IReconciliationLock
{
    /// <summary>
    /// Throws <see cref="Contracts.AccountPeriodLockedException"/> (409) if a finalized reconciliation
    /// exists for (<paramref name="bankAccountId"/>, <paramref name="date"/>'s year+month); otherwise no-op.
    /// </summary>
    Task EnsureOpenAsync(Guid bankAccountId, DateOnly date, CancellationToken ct);
}
