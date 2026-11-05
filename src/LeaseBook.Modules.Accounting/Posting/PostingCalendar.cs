using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel.Time;

namespace LeaseBook.Modules.Accounting.Posting;

/// <summary>
/// Which day it is for the organization's books (#642): the current instant from the clock, read in
/// the organization's time zone. A void with no as-of is dated this day, and money moving in or out
/// cannot be dated after it.
/// </summary>
internal sealed class PostingCalendar(IOrgTimeZone zone, TimeProvider clock)
{
    public async Task<DateOnly> TodayAsync(CancellationToken ct) => clock.TodayIn(await zone.GetAsync(ct));

    /// <summary>
    /// Refuses a date after today with <see cref="PostingDateInFutureException"/>. For money received,
    /// paid out, applied or moved between banks: those have happened by the day they are recorded, so a
    /// later date is a mistake. A charge or a credit billed ahead does not come through here.
    /// </summary>
    public async Task EnsureNotAfterTodayAsync(DateOnly date, CancellationToken ct)
    {
        var today = await TodayAsync(ct);
        if (date > today)
        {
            throw new PostingDateInFutureException(date, today);
        }
    }
}
