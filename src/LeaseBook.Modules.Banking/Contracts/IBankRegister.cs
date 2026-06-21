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
}

/// <summary>An uncleared register line a statement line can match. <see cref="Amount"/> is signed: deposit +, withdrawal −.</summary>
public sealed record RegisterCandidate(Guid JournalLineId, DateOnly Date, decimal Amount, string Description);
