namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Voids a posted entry by posting a linked reversal (CLAUDE.md append-only contract): a mirror entry
/// with debit/credit swapped, <c>reverses_entry_id</c> set, posted <b>through the posting service</b>
/// (no second write path). The reversal lands in the open period at <c>asOfDate</c> — corrections never
/// post into a locked period. An entry can be reversed at most once, and a reversal cannot be reversed.
/// </summary>
public interface IReversalService
{
    Task<Guid> ReverseAsync(Guid entryId, string reason, DateOnly asOfDate, CancellationToken ct);
}
