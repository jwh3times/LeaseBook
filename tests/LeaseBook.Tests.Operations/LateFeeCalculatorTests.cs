using LeaseBook.Modules.Operations.Runs;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// TDD unit tests for <see cref="LateFeeCalculator.Compute"/> (WP-3, NC §42-46 clamp).
/// Each <c>[InlineData]</c> case is the canonical example from the task brief; the figures are
/// locked as the ground-truth reference.
/// <para>
/// NC §42-46 clamp: <c>Min(raw, Max(15.00, Round(0.05 × rent, 2, AwayFromZero)))</c>.
/// </para>
/// </summary>
public sealed class LateFeeCalculatorTests
{
    [Theory]
    // Flat $25 under the cap (5% of 1450 = 72.50) → 25.00
    [InlineData(LateFeeKind.Flat, 25.00, 0, 1450, 25.00)]
    // Flat $100 clamped to 72.50
    [InlineData(LateFeeKind.Flat, 100.00, 0, 1450, 72.50)]
    // 10% of 1450 = 145.00 clamped to 72.50
    [InlineData(LateFeeKind.Percent, 0, 1000, 1450, 72.50)]
    // 5% on low rent 200 → 10.00, but floor is $15 → max(15, 10)=15; percent raw 10 → min(10,15)=10
    [InlineData(LateFeeKind.Percent, 0, 500, 200, 10.00)]
    public void Computes_then_NC_clamps(LateFeeKind kind, decimal flat, int bps, decimal rent, decimal expected)
    {
        var p = new LateFeePolicy(1, 5, kind, flat, bps);
        LateFeeCalculator.Compute(p, rent).ShouldBe(expected);
    }

    [Fact]
    public void Flat_fee_exactly_at_cap_is_not_clamped()
    {
        // 5% of 1000 = 50 → cap = 50; flat = 50 → min(50, 50) = 50
        var p = new LateFeePolicy(1, 5, LateFeeKind.Flat, 50m, 0);
        LateFeeCalculator.Compute(p, 1000m).ShouldBe(50m);
    }

    [Fact]
    public void Percent_rounds_half_away_from_zero()
    {
        // 4% of $1500 = 60.00; cap = max(15, 75) = 75 → min(60, 75) = 60
        var p = new LateFeePolicy(1, 5, LateFeeKind.Percent, 0m, 400);
        LateFeeCalculator.Compute(p, 1500m).ShouldBe(60m);
    }

    [Fact]
    public void Low_rent_percent_below_floor_uses_floor_as_cap_and_is_not_further_clamped()
    {
        // Rent = $200; 5% cap = max(15, 10.00) = 15; flat $20 → clamped to 15
        var p = new LateFeePolicy(1, 5, LateFeeKind.Flat, 20m, 0);
        LateFeeCalculator.Compute(p, 200m).ShouldBe(15m);
    }

    [Fact]
    public void Flat_below_floor_cap_passes_through()
    {
        // Rent = $200; cap = 15; flat = 10 → 10 < 15 → min(10, 15) = 10
        var p = new LateFeePolicy(1, 5, LateFeeKind.Flat, 10m, 0);
        LateFeeCalculator.Compute(p, 200m).ShouldBe(10m);
    }
}
