using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Banking.Import;

/// <summary>
/// Serializes one bank account's statement imports and confirmations for the rest of the transaction
/// (#671). An import reads which lines are undecided and a confirmation decides them, so each must see what
/// the other has committed: without this, two confirmations could both find a line undecided.
/// </summary>
internal static class StatementImportLock
{
    public static Task AcquireAsync(DbContext db, Guid bankAccountId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('lb:statement-import:' || {bankAccountId.ToString()}, 0))", ct);
}
