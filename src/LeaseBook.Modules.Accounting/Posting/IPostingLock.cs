namespace LeaseBook.Modules.Accounting.Posting;

/// <summary>
/// Serializes guarded postings for one org (P31). Guarded events — the <c>PaymentReceived</c>
/// auto-split, deposit/prepayment over-application checks, and the disbursement reserve floor — read a
/// balance and then post against it; without serialization two concurrent transactions could both read
/// a stale balance (pitfall M-E7). Callers (WP-05 templates) take this transaction-level advisory lock
/// <b>before</b> the balance read; it is released automatically when the transaction ends.
/// </summary>
internal interface IPostingLock
{
    Task AcquireAsync(CancellationToken ct);
}
