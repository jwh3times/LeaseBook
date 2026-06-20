using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Posting;

/// <summary>
/// Reads <c>bank_reconciliations</c> on the ambient scoped (RLS) connection to enforce the per-account
/// lock (P63). A finalized row for (account, year, month) means that account-month is reconciled and
/// closed to new bank postings.
/// </summary>
internal sealed class ReconciliationLock(DbContext db) : IReconciliationLock
{
    public async Task EnsureOpenAsync(Guid bankAccountId, DateOnly date, CancellationToken ct)
    {
        var locked = await db.Set<BankReconciliation>().AsNoTracking().AnyAsync(
            r => r.BankAccountId == bankAccountId
                && r.PeriodYear == date.Year
                && r.PeriodMonth == date.Month
                && r.Status == ReconciliationStatus.Finalized,
            ct);

        if (locked)
        {
            throw new AccountPeriodLockedException(bankAccountId, date.Year, date.Month);
        }
    }
}
