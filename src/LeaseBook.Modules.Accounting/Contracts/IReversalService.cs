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

    /// <summary>
    /// As <see cref="ReverseAsync(Guid, string, DateOnly, CancellationToken)"/>, carrying the UI-supplied
    /// idempotency key on the reversal (P54): a double-submitted void maps to <c>duplicate_source_ref</c>
    /// (409, with the existing reversal id) rather than re-posting. The M3 void command uses this overload;
    /// the internal-correction callers (seeder, month-sim) keep the keyless one.
    /// </summary>
    Task<Guid> ReverseAsync(Guid entryId, string reason, DateOnly asOfDate, string? sourceRef, CancellationToken ct);
}
