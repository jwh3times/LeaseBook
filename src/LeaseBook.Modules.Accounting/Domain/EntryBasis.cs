namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// The accounting basis a journal line participates in (dual-basis posting, CLAUDE.md). Each basis
/// is a <i>query</i>, not a transformation: a <see cref="Both"/> line counts in <b>each</b> basis when
/// balancing or reporting, so summing across all three tags double-counts (pitfall M-E2) — balance
/// queries always filter <c>basis IN (@basis, 'both')</c> for exactly one requested basis.
/// </summary>
public enum EntryBasis
{
    /// <summary>Cash-basis only: the line moves money in/out of a bank.</summary>
    Cash,

    /// <summary>Accrual-basis only: a receivable/recognition with no cash movement yet.</summary>
    Accrual,

    /// <summary>Counts in both bases (e.g. a deposit collection that is simultaneously cash and a liability).</summary>
    Both,
}
