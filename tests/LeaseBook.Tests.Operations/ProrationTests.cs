using LeaseBook.Modules.Operations.Runs;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// TDD unit tests for <see cref="Proration.Charge"/> (ADR-017). Each <c>[InlineData]</c> case is
/// the canonical example from the ADR; the figures are locked as the ground-truth reference.
/// </summary>
public sealed class ProrationTests
{
    [Theory]
    // Full month — no proration.
    [InlineData(1450, 2026, 6, null, null, 1450.00)]
    // Move-in Jun 14 (30-day month): days 14..30 inclusive = 17 → 1450*17/30 = 821.6667 → 821.67
    [InlineData(1450, 2026, 6, "2026-06-14", null, 821.67)]
    // Move-in Feb 14 (28-day month): days 14..28 inclusive = 15 → 1450*15/28 = 776.7857 → 776.79
    [InlineData(1450, 2026, 2, "2026-02-14", null, 776.79)]
    // Move-out Jun 10: days 1..10 = 10 → 1450*10/30 = 483.3333 → 483.33
    [InlineData(1450, 2026, 6, null, "2026-06-10", 483.33)]
    public void Computes_actual_days_proration(decimal rent, int y, int m, string? start, string? end, decimal expected)
    {
        var s = start is null ? (DateOnly?)null : DateOnly.Parse(start);
        var e = end is null ? (DateOnly?)null : DateOnly.Parse(end);
        Proration.Charge(rent, y, m, s, e).ShouldBe(expected);
    }

    [Theory]
    // Both start and end fall within the same month: start=Jun 5, end=Jun 20 → days 5..20 = 16 → 1450*16/30 = 773.33
    [InlineData(1450, 2026, 6, "2026-06-05", "2026-06-20", 773.33)]
    // Start before period, end before period → full month (edge: end = last day)
    [InlineData(1450, 2026, 6, "2026-01-01", "2026-06-30", 1450.00)]
    // Zero rent → always 0 regardless of proration
    [InlineData(0, 2026, 6, "2026-06-14", null, 0.00)]
    // February leap year 2024 (29 days), move-in Feb 15: days 15..29 = 15 → 1450*15/29 = 750.00
    [InlineData(1450, 2024, 2, "2024-02-15", null, 750.00)]
    public void Additional_proration_edge_cases(decimal rent, int y, int m, string? start, string? end, decimal expected)
    {
        var s = start is null ? (DateOnly?)null : DateOnly.Parse(start);
        var e = end is null ? (DateOnly?)null : DateOnly.Parse(end);
        Proration.Charge(rent, y, m, s, e).ShouldBe(expected);
    }
}
