namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// Lifecycle of a monthly <see cref="AccountingPeriod"/> (P32). The posting engine rejects writes
/// into a <see cref="Closed"/> period; M1 ships close (testable now) but no reopen — unlock-with-reason
/// is M4 reconciliation scope. Stored as snake_case text with a DB CHECK backstop.
/// </summary>
public enum PeriodStatus
{
    /// <summary>Accepting postings.</summary>
    Open,

    /// <summary>Locked: postings into this period are rejected. Corrections post into the open period.</summary>
    Closed,
}
