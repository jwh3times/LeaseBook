using System.Text.RegularExpressions;
using LeaseBook.Modules.Directory.Domain;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The Settings select offers the organization time zones from a list in the SPA (#642), and the server
/// validates every save against its own (<see cref="SupportedTimeZones"/>). The two must be one list: a
/// zone only the SPA has is refused on save, and one only the server has can never be chosen.
/// </summary>
public sealed partial class OrgTimeZoneListDriftTests
{
    private const string SpaList = "web/src/lib/orgCalendar.ts";

    [GeneratedRegex(@"\{\s*id:\s*'(?<id>[^']+)',\s*name:\s*'(?<name>[^']+)'\s*\}")]
    private static partial Regex Entry();

    [Fact]
    public void The_spa_offers_exactly_the_zones_the_server_accepts_in_the_same_order_and_names()
    {
        var text = RepositorySource.Current.File(SpaList).Text;
        var start = text.IndexOf("ORG_TIME_ZONES", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, $"{SpaList} no longer declares ORG_TIME_ZONES");
        var end = text.IndexOf("] as const", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"{SpaList}'s ORG_TIME_ZONES is no longer a const array");

        var spa = Entry().Matches(text[start..end])
            .Select(match => (match.Groups["id"].Value, match.Groups["name"].Value))
            .ToList();

        spa.ShouldBe([.. SupportedTimeZones.All.Select(zone => (zone.Id, zone.Name))]);
    }
}
