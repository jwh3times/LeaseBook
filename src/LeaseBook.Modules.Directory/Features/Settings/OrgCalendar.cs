using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Settings;

/// <summary>
/// Which day it is for the organization (#642, ADR-037): the clock's instant in the organization's
/// zone. Directory's "current" reads (the lease in force, occupancy) use it in place of the UTC date,
/// and <see cref="GetOrgTimeZone"/> hands the zone to the other modules.
/// </summary>
internal sealed class OrgCalendar(DbContext db, IOrgContext org, TimeProvider clock)
{
    private TimeZoneInfo? _zone;

    /// <summary>
    /// The stored zone, or the default when the organization has no settings row yet. Throws without an
    /// organization context: row security would hide every row and the default would answer for an
    /// organization nobody named.
    /// </summary>
    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
    {
        if (_zone is not null)
        {
            return _zone;
        }

        _ = org.OrgId ?? throw new InvalidOperationException("The organization's time zone requires an ambient organization context.");
        var id = await db.Set<OrgSettings>().AsNoTracking().Select(settings => settings.TimeZone).FirstOrDefaultAsync(ct);
        _zone = SupportedTimeZones.Resolve(id ?? SupportedTimeZones.Default);
        return _zone;
    }

    public async Task<DateOnly> TodayAsync(CancellationToken ct) => clock.TodayIn(await ZoneAsync(ct));
}
