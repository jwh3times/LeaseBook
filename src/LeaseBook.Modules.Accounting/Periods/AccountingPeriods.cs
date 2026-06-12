using LeaseBook.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Periods;

/// <summary>
/// Lazy get-or-create / close of monthly accounting periods (P32), org-scoped via the ambient context.
/// </summary>
internal sealed class AccountingPeriods(DbContext db) : IAccountingPeriods
{
    public async Task<AccountingPeriod> GetOpenPeriodAsync(DateOnly date, CancellationToken ct)
    {
        var year = date.Year;
        var month = date.Month;

        var existing = await db.Set<AccountingPeriod>()
            .FirstOrDefaultAsync(p => p.Year == year && p.Month == month, ct);
        if (existing is not null)
        {
            return existing;
        }

        var period = AccountingPeriod.Open(year, month);
        db.Set<AccountingPeriod>().Add(period);
        try
        {
            await db.SaveChangesAsync(ct);
            return period;
        }
        catch (DbUpdateException)
        {
            // Lost the race for this month's (org_id, year, month) unique slot. EF rolls back to its
            // SaveChanges savepoint, so the surrounding unit-of-work transaction is intact and the
            // winner's row is now visible (READ COMMITTED). Detach our doomed entity and read it back
            // (P32); if there is no winner this was a different failure — surface it.
            db.Entry(period).State = EntityState.Detached;
            var winner = await db.Set<AccountingPeriod>()
                .FirstOrDefaultAsync(p => p.Year == year && p.Month == month, ct);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    public async Task<AccountingPeriod> CloseAsync(int year, int month, CancellationToken ct)
    {
        var period = await GetOpenPeriodAsync(new DateOnly(year, month, 1), ct);
        if (period.Status == PeriodStatus.Open)
        {
            period.Close(DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
        }

        return period;
    }
}
