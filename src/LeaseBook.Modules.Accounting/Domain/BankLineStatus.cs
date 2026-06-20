namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// The clearance state of a bank journal line in the register (M4 / ADR-014). Mutable operational
/// metadata layered over the append-only journal via <see cref="BankLineState"/> — never a journal-row
/// change. Absence of a state row is equivalent to <see cref="Uncleared"/>.
/// </summary>
public enum BankLineStatus
{
    /// <summary>Posted to the book but not yet seen on a bank statement.</summary>
    Uncleared,

    /// <summary>Matched to a statement line (manually or by import), not yet part of a finalized reconciliation.</summary>
    Cleared,

    /// <summary>Locked into a finalized reconciliation (carries its <c>reconciliation_id</c>).</summary>
    Reconciled,
}
