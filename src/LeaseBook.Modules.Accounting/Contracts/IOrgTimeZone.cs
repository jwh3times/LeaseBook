namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// The organization's time zone, the calendar its books keep (#642, ADR-037). The host reads it from
/// Directory's settings. Accounting combines it with its <see cref="TimeProvider"/> to say which day it
/// is for a default as-of and for the future-date refusal, so a test clock or a seeder's fixed clock
/// still decides the instant.
/// </summary>
public interface IOrgTimeZone
{
    Task<TimeZoneInfo> GetAsync(CancellationToken ct);
}
