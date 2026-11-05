using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Directory.Features.Settings;

/// <summary>
/// The organization's time zone, resolved (#642), for the modules that keep its books. Reads the stored
/// zone without creating the settings row, so a portal persona, which holds no write grant on
/// <c>org_settings</c>, gets the default rather than a refusal.
/// </summary>
public sealed record GetOrgTimeZone : IQuery<TimeZoneInfo>;

internal sealed class GetOrgTimeZoneHandler(OrgCalendar calendar) : IQueryHandler<GetOrgTimeZone, TimeZoneInfo>
{
    public Task<TimeZoneInfo> Handle(GetOrgTimeZone query, CancellationToken ct) => calendar.ZoneAsync(ct);
}
