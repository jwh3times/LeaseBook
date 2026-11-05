namespace LeaseBook.SharedKernel.Time;

/// <summary>The calendar date of a clock's instant in a time zone (#642).</summary>
public static class CalendarDate
{
    /// <summary>
    /// Today's date in <paramref name="zone"/>, from this clock's current instant. The books keep the
    /// organization's calendar, so a module asks its zone port for <paramref name="zone"/> and its own
    /// <see cref="TimeProvider"/> for the instant; a test clock then decides the instant as before.
    /// </summary>
    public static DateOnly TodayIn(this TimeProvider clock, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
}
