namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// The one public surface for posting business events (§C.3 catalog). Routes each
/// <see cref="AccountingEvent"/> to its template, runs the event's guards (advisory lock + balance
/// reads for the guarded events, P31), and posts the resulting entry through <see cref="IPostingService"/>.
/// Runs inside the ambient org transaction. Returns the posted entry id.
/// </summary>
public interface IAccountingEvents
{
    Task<Guid> PostAsync(AccountingEvent businessEvent, CancellationToken ct);
}
