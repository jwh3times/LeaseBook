using System.Collections.Concurrent;

namespace LeaseBook.Modules.Directory.Domain;

/// <summary>One time zone an organization's books may keep: its stored IANA id and its US name.</summary>
public sealed record SupportedTimeZone(string Id, string Name);

/// <summary>
/// The calendar an organization's books keep (#642, ADR-037). "Today" for a posting date, a default
/// as-of or the current month is the date in the organization's time zone, never the UTC date, which
/// an evening in a US time zone has already passed.
/// <para>
/// The zone is one of a fixed list of US zones, stored as its IANA id. Resolving an id must not depend
/// on what the host happens to ship: the chiseled runtime image has no tz database unless the
/// Dockerfile copies one in, and on Windows <c>InvariantGlobalization</c> removes the ICU that maps
/// IANA ids to Windows ones. So a Windows host falls back to the Windows id of the same zone, and
/// <see cref="EnsureResolvable"/> runs at startup so a host that cannot resolve one fails to start
/// rather than dating anything in UTC.
/// </para>
/// </summary>
public static class SupportedTimeZones
{
    /// <summary>The zone every organization starts with: North Carolina's.</summary>
    public const string Default = "America/New_York";

    // The Windows id of each zone is used only where the IANA lookup fails on a Windows host.
    private static readonly (SupportedTimeZone Zone, string WindowsId)[] Catalogue =
    [
        (new("America/New_York", "Eastern"), "Eastern Standard Time"),
        (new("America/Chicago", "Central"), "Central Standard Time"),
        (new("America/Denver", "Mountain"), "Mountain Standard Time"),
        (new("America/Phoenix", "Arizona"), "US Mountain Standard Time"),
        (new("America/Los_Angeles", "Pacific"), "Pacific Standard Time"),
        (new("America/Anchorage", "Alaska"), "Alaskan Standard Time"),
        (new("Pacific/Honolulu", "Hawaii"), "Hawaiian Standard Time"),
    ];

    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Resolved = new(StringComparer.Ordinal);

    /// <summary>The zones an organization may choose, in the order a picker shows them.</summary>
    public static IReadOnlyList<SupportedTimeZone> All { get; } = [.. Catalogue.Select(entry => entry.Zone)];

    /// <summary>Whether <paramref name="id"/> is exactly one of the listed IANA ids.</summary>
    public static bool IsSupported(string? id) => id is not null && Catalogue.Any(entry => entry.Zone.Id == id);

    /// <summary>
    /// The zone for a listed id. Throws <see cref="ArgumentException"/> for an id off the list, and
    /// <see cref="InvalidOperationException"/> when the host cannot resolve a listed one.
    /// </summary>
    public static TimeZoneInfo Resolve(string id)
    {
        if (!IsSupported(id))
        {
            throw new ArgumentException($"'{id}' is not a supported organization time zone.", nameof(id));
        }

        return Resolved.GetOrAdd(id, Lookup);
    }

    /// <summary>Resolves every listed zone, so a host without the data fails at startup, loudly.</summary>
    public static void EnsureResolvable()
    {
        foreach (var zone in All)
        {
            Resolve(zone.Id);
        }
    }

    private static TimeZoneInfo Lookup(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (OperatingSystem.IsWindows())
            {
                return TimeZoneInfo.FindSystemTimeZoneById(Catalogue.Single(entry => entry.Zone.Id == id).WindowsId);
            }

            throw new InvalidOperationException(
                $"The organization time zone '{id}' cannot be resolved on this host. The runtime needs a tz "
                + "database at /usr/share/zoneinfo (or TZDIR); the Dockerfile copies one into the runtime image.",
                e);
        }
    }
}
