using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Time;

namespace LeaseBook.Web.Calendar;

/// <summary>
/// The organization's calendar for the host (#642, ADR-037). It is the adapter behind Accounting's,
/// Operations' and Payments' <c>IOrgTimeZone</c> ports, all three over Directory's setting, and it
/// answers the host's own defaults: a statement, run preview or report with no period or as-of named
/// opens on the organization's day and month, not the UTC ones, which a US evening at a month's end has
/// already left. Runs inside the request's organization transaction, like every other read.
/// </summary>
public sealed class HostOrgCalendar(ISender sender, TimeProvider clock)
    : LeaseBook.Modules.Accounting.Contracts.IOrgTimeZone,
        LeaseBook.Modules.Operations.Contracts.IOrgTimeZone,
        LeaseBook.Modules.Payments.Contracts.IOrgTimeZone
{
    public Task<TimeZoneInfo> GetAsync(CancellationToken ct) => sender.Query(new GetOrgTimeZone(), ct);

    public async Task<DateOnly> TodayAsync(CancellationToken ct) => clock.TodayIn(await GetAsync(ct));

    /// <summary>The period asked for, with whatever part is missing taken from the organization's today.</summary>
    public async Task<(int Year, int Month)> PeriodAsync(int? year, int? month, CancellationToken ct)
    {
        if (year is { } y && month is { } m)
        {
            return (y, m);
        }

        var today = await TodayAsync(ct);
        return (year ?? today.Year, month ?? today.Month);
    }
}
