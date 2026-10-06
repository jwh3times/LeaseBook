namespace LeaseBook.Modules.Banking.Contracts;

/// <summary>
/// Consumer-owned read port (ADR-007 / P49 / P68) for the uncleared bank-register lines Accounting owns.
/// The host adapter delegates to Accounting's <c>GetBankRegister</c> via <c>ISender</c> and returns the
/// uncleared candidates for one account over a date window. <b>Batch/windowed only</b> — never per-id.
/// Banking never reads <c>journal_lines</c> / <c>bank_line_status</c> directly.
/// </summary>
public interface IBankRegister
{
    Task<IReadOnlyList<RegisterCandidate>> GetUnclearedAsync(
        Guid bankAccountId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>
    /// The group each of the given register lines belongs to; a line in no group is absent from the map.
    /// Unlike <see cref="GetUnclearedAsync"/> this is not bounded by a date window or by clearance, so it
    /// answers for a line the candidate read never returned.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetGroupRefsAsync(
        IReadOnlyCollection<Guid> journalLineIds, CancellationToken ct);
}

/// <summary>An uncleared register line a statement line can match. <see cref="Amount"/> is signed: deposit +, withdrawal −.</summary>
/// <param name="GroupRef">
/// Set when the line is one of several the bank shows as a single statement line: today, the lines one
/// processor payout posted (ADR-053). Lines sharing a group match and clear as a whole, against the
/// group's signed sum, and none of them is ever matched alone.
/// </param>
public sealed record RegisterCandidate(
    Guid JournalLineId, DateOnly Date, decimal Amount, string Description, string? GroupRef = null);
