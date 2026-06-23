namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// Actual-days proration calculator (ADR-017). Day count is <b>inclusive</b> of both move-in and
/// move-out day. A single division avoids double-rounding; the result is rounded half-away-from-zero
/// to 2 decimal places (the convention used throughout the trust-accounting engine).
/// </summary>
public static class Proration
{
    /// <summary>
    /// Returns the prorated rent amount for the given calendar month.
    /// <para>
    /// If <paramref name="start"/> falls in the month: <c>daysOccupied = daysInMonth − start.Day + 1</c>.<br/>
    /// If <paramref name="end"/> falls in the month: <c>daysOccupied = end.Day</c>.<br/>
    /// If both fall in the month: <c>daysOccupied = end.Day − start.Day + 1</c>.<br/>
    /// If neither falls in the month (lease spans the whole period): returns <paramref name="monthlyRent"/> unchanged.
    /// </para>
    /// </summary>
    /// <param name="monthlyRent">Full calendar-month rent.</param>
    /// <param name="year">Period year.</param>
    /// <param name="month">Period month (1–12).</param>
    /// <param name="start">Lease start date (move-in); null = started before this period.</param>
    /// <param name="end">Lease end date (move-out); null = ends after this period.</param>
    public static decimal Charge(decimal monthlyRent, int year, int month, DateOnly? start, DateOnly? end)
    {
        int daysInMonth = DateTime.DaysInMonth(year, month);
        var periodStart = new DateOnly(year, month, 1);
        var periodEnd = new DateOnly(year, month, daysInMonth);

        // Does start fall inside this period?
        bool startInPeriod = start.HasValue && start.Value >= periodStart && start.Value <= periodEnd;
        // Does end fall inside this period?
        bool endInPeriod = end.HasValue && end.Value >= periodStart && end.Value <= periodEnd;

        if (!startInPeriod && !endInPeriod)
        {
            // Lease spans the whole period (or has no dates) — full month.
            return monthlyRent;
        }

        int daysOccupied;
        if (startInPeriod && endInPeriod)
        {
            // Both boundaries fall in this month.
            daysOccupied = end!.Value.Day - start!.Value.Day + 1;
        }
        else if (startInPeriod)
        {
            // Move-in during this month; lease continues past month end.
            daysOccupied = daysInMonth - start!.Value.Day + 1;
        }
        else
        {
            // Move-out during this month; lease started before this month.
            daysOccupied = end!.Value.Day;
        }

        // Single division — no intermediate rounding.
        return Math.Round(monthlyRent * daysOccupied / daysInMonth, 2, MidpointRounding.AwayFromZero);
    }
}
