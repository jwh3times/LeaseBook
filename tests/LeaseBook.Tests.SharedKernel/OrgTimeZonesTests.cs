using LeaseBook.SharedKernel.Time;
using Shouldly;

namespace LeaseBook.Tests.SharedKernel;

/// <summary>
/// The books' calendar (#642). Every expected date below is worked out by hand from the zone's UTC
/// offset on that day, never by converting the instant the way the code does.
/// </summary>
public sealed class OrgTimeZonesTests
{
    private sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DateOnly TodayAt(string utc, string zone) =>
        new StoppedClock(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture))
            .TodayIn(OrgTimeZones.Resolve(zone));

    [Fact]
    public void The_default_is_eastern_and_the_list_is_the_seven_us_zones_in_order()
    {
        OrgTimeZones.Default.ShouldBe("America/New_York");
        OrgTimeZones.Supported.Select(z => z.Id).ShouldBe(
        [
            "America/New_York", "America/Chicago", "America/Denver", "America/Phoenix",
            "America/Los_Angeles", "America/Anchorage", "Pacific/Honolulu",
        ]);
        OrgTimeZones.Supported.Select(z => z.Name).ShouldBe(
            ["Eastern", "Central", "Mountain", "Arizona", "Pacific", "Alaska", "Hawaii"]);
    }

    [Fact]
    public void Every_supported_zone_resolves_on_this_host()
    {
        // The startup check: on Windows under InvariantGlobalization and in a container with no tz
        // database the IANA lookup throws, and every one of these must still resolve.
        Should.NotThrow(OrgTimeZones.EnsureResolvable);
        foreach (var zone in OrgTimeZones.Supported)
        {
            OrgTimeZones.Resolve(zone.Id).ShouldNotBeNull();
        }
    }

    [Theory]
    // 21:00 EDT on 31 Oct 2026 (UTC-4) is 01:00 UTC on 1 Nov.
    [InlineData("2026-11-01T01:00:00Z", "America/New_York", "2026-10-31")]
    // 23:30 PDT on 31 Oct 2026 (UTC-7) is 06:30 UTC on 1 Nov.
    [InlineData("2026-11-01T06:30:00Z", "America/Los_Angeles", "2026-10-31")]
    // 19:30 EST on 4 Nov 2026 (UTC-5, after the 1 Nov change) is 00:30 UTC on 5 Nov.
    [InlineData("2026-11-05T00:30:00Z", "America/New_York", "2026-11-04")]
    // Phoenix keeps UTC-7 all year: 06:59 UTC on 1 Jul is 23:59 on 30 Jun; 07:00 UTC is midnight.
    [InlineData("2026-07-01T06:59:00Z", "America/Phoenix", "2026-06-30")]
    [InlineData("2026-07-01T07:00:00Z", "America/Phoenix", "2026-07-01")]
    // Denver in July is UTC-6, so the same 06:59 UTC is already 00:59 on 1 Jul there.
    [InlineData("2026-07-01T06:59:00Z", "America/Denver", "2026-07-01")]
    // Honolulu is UTC-10: 09:59 UTC on 1 Jan is 23:59 on 31 Dec.
    [InlineData("2027-01-01T09:59:00Z", "Pacific/Honolulu", "2026-12-31")]
    // Anchorage in January is UTC-9; Chicago in January is UTC-6.
    [InlineData("2027-01-01T08:30:00Z", "America/Anchorage", "2026-12-31")]
    [InlineData("2027-01-01T05:30:00Z", "America/Chicago", "2026-12-31")]
    // A morning instant is the same date everywhere.
    [InlineData("2026-10-31T15:00:00Z", "America/New_York", "2026-10-31")]
    public void Today_is_the_calendar_date_in_the_zone(string utc, string zone, string expected) =>
        TodayAt(utc, zone).ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("America/New_York", true)]
    [InlineData("Pacific/Honolulu", true)]
    [InlineData("Europe/London", false)] // a real IANA id, but not on the list
    [InlineData("Eastern Standard Time", false)] // a Windows id is never a stored value
    [InlineData("america/new_york", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_listed_ids_are_supported(string? id, bool supported) =>
        OrgTimeZones.IsSupported(id).ShouldBe(supported);

    [Fact]
    public void An_unlisted_id_is_refused_rather_than_resolved() =>
        Should.Throw<ArgumentException>(() => OrgTimeZones.Resolve("Europe/London"));
}
