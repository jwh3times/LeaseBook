namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// Lifecycle of a bank reconciliation (M4 / ADR-014). A <see cref="Finalized"/> row for an
/// (account, month) <b>is</b> the per-account lock that the posting guard reads; <see cref="Reopened"/>
/// releases the lock (PMAdmin + reason).
/// </summary>
public enum ReconciliationStatus
{
    /// <summary>Open: the PM is ticking items toward a zero difference; nothing is locked.</summary>
    InProgress,

    /// <summary>Finalized at zero difference: items are reconciled and the (account, month) is locked.</summary>
    Finalized,

    /// <summary>Unlocked by a PMAdmin (with reason); the lock is released, items stay reconciled until re-finalized.</summary>
    Reopened,
}
