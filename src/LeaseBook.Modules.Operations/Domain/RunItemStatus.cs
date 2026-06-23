namespace LeaseBook.Modules.Operations.Domain;

/// <summary>The outcome of a single <see cref="BulkRunItem"/> after the posting attempt.</summary>
public enum RunItemStatus
{
    /// <summary>The posting succeeded.</summary>
    Posted,

    /// <summary>
    /// A <c>DuplicateSourceRefException</c> was caught — the entry already exists (idempotent repeat).
    /// </summary>
    Skipped,

    /// <summary>
    /// A <c>PeriodLockedException</c> (or equivalent period-closed exception) was caught — the
    /// target period is locked and the item was intentionally not posted.
    /// </summary>
    Excluded,
}
