using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Settings;

/// <summary>
/// The organization's time zone (#642), for the modules that keep its books. Reads the stored zone
/// without creating the settings row, so a portal persona, which holds no write grant on
/// <c>org_settings</c>, gets the default rather than a refusal.
/// </summary>
public sealed record GetOrgTimeZone : IQuery<string>;

internal sealed class GetOrgTimeZoneHandler(OrgCalendar calendar) : IQueryHandler<GetOrgTimeZone, string>
{
    public Task<string> Handle(GetOrgTimeZone query, CancellationToken ct) => calendar.ZoneIdAsync(ct);
}

/// <summary>
/// Which day it is for the organization (#642, ADR-037): the clock's instant in the organization's
/// zone. Directory's "current" reads (the lease in force, occupancy) and the ownership-transfer date
/// check use it in place of the UTC date.
/// </summary>
internal sealed class OrgCalendar(DbContext db, IOrgContext org, TimeProvider clock)
{
    private string? _zone;

    /// <summary>
    /// The stored zone, or the default when the organization has no settings row yet. Throws without an
    /// organization context: row security would hide every row and the default would answer for an
    /// organization nobody named.
    /// </summary>
    public async Task<string> ZoneIdAsync(CancellationToken ct)
    {
        if (_zone is not null)
        {
            return _zone;
        }

        _ = org.OrgId ?? throw new InvalidOperationException("The organization's time zone requires an ambient organization context.");
        _zone = await db.Set<OrgSettings>().AsNoTracking().Select(settings => settings.TimeZone).FirstOrDefaultAsync(ct)
            ?? OrgTimeZones.Default;
        return _zone;
    }

    public async Task<DateOnly> TodayAsync(CancellationToken ct) => clock.TodayIn(OrgTimeZones.Resolve(await ZoneIdAsync(ct)));
}
