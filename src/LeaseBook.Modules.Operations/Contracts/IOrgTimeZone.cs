namespace LeaseBook.Modules.Operations.Contracts;

/// <summary>
/// The organization's time zone, the calendar its books keep (#642, ADR-037). The host reads it from
/// Directory's settings; this module combines it with its <see cref="TimeProvider"/> to say which day
/// it is, so a test clock or a seeder's fixed clock still decides the instant.
/// </summary>
public interface IOrgTimeZone
{
    Task<TimeZoneInfo> GetAsync(CancellationToken ct);
}
