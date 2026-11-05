using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Time;

namespace LeaseBook.Web.Calendar;

/// <summary>
/// Which day it is for the organization, for the host's own defaults (#642, ADR-037): the clock's
/// instant in the organization's time zone. A statement, a run preview or a report with no period or
/// as-of named opens on this day's month, not the UTC month, which a US evening at a month's end has
/// already left. Runs inside the request's organization transaction, like every other read.
/// </summary>
public static class OrgToday
{
    public static async Task<DateOnly> ReadAsync(ISender sender, TimeProvider clock, CancellationToken ct) =>
        clock.TodayIn(OrgTimeZones.Resolve(await sender.Query(new GetOrgTimeZone(), ct)));
}
