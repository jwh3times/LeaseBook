namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// The <b>only</b> write path to the journal (CLAUDE.md). Validates org context, account existence,
/// per-line shape, per-basis balance, the PM-income dimension rule, open-period, and source_ref
/// idempotency — each failure a typed <see cref="AccountingDomainException"/>, never a silent fix-up —
/// then persists the header + lines atomically inside the ambient org transaction. Returns the new
/// entry id.
/// </summary>
public interface IPostingService
{
    Task<Guid> PostAsync(PostEntryRequest request, CancellationToken ct);
}
