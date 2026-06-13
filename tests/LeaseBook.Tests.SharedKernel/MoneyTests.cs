using LeaseBook.SharedKernel;
using Shouldly;

namespace LeaseBook.Tests.SharedKernel;

/// <summary>
/// The Money arithmetic surface the accounting engine builds on (P28). The load-bearing rule is the
/// rounding gate: a value that cannot be represented in NUMERIC(14,2) without rounding is rejected at
/// construction, so no rounded amount can ever enter the journal.
/// </summary>
public sealed class MoneyTests
{
    [Theory]
    [InlineData("1234.567")] // genuine third decimal
    [InlineData("0.001")]
    [InlineData("-0.005")]
    [InlineData("99.999")]
    public void Construction_rejects_more_than_two_decimal_places(string value)
    {
        var raw = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Should.Throw<ArgumentOutOfRangeException>(() => new Money(raw));
        Should.Throw<ArgumentOutOfRangeException>(() => Money.Of(raw));
    }

    [Theory]
    [InlineData("1234.56")]
    [InlineData("1450")]    // scale 0
    [InlineData("1450.5")]  // scale 1
    [InlineData("1.230")]   // scale 3 but trailing zero — numerically two-place, accepted
    [InlineData("-420")]    // owner equity can go negative
    [InlineData("0")]
    public void Construction_accepts_amounts_representable_in_two_places(string value)
    {
        var raw = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Should.NotThrow(() => new Money(raw));
    }

    [Fact]
    public void Equality_is_numeric_ignoring_decimal_scale()
    {
        new Money(1450m).ShouldBe(new Money(1450.00m));
        (new Money(1450m) == new Money(1450.00m)).ShouldBeTrue();
        new Money(1450m).GetHashCode().ShouldBe(new Money(1450.00m).GetHashCode());
    }

    [Fact]
    public void Addition_and_subtraction_are_correct()
    {
        (new Money(1450.00m) + new Money(25.00m)).ShouldBe(new Money(1475.00m));
        (new Money(1475.00m) - new Money(25.00m)).ShouldBe(new Money(1450.00m));
        (new Money(2150.00m) - new Money(2225.00m)).ShouldBe(new Money(-75.00m));
    }

    [Fact]
    public void Addition_is_commutative()
    {
        (new Money(12.34m) + new Money(56.78m)).ShouldBe(new Money(56.78m) + new Money(12.34m));
    }

    [Fact]
    public void Unary_minus_negates()
    {
        (-new Money(1450.00m)).ShouldBe(new Money(-1450.00m));
        (-new Money(-75.00m)).ShouldBe(new Money(75.00m));
        (-Money.Zero).ShouldBe(Money.Zero);
    }

    [Fact]
    public void Comparisons_order_by_amount()
    {
        (new Money(10m) < new Money(20m)).ShouldBeTrue();
        (new Money(20m) > new Money(10m)).ShouldBeTrue();
        (new Money(10m) <= new Money(10m)).ShouldBeTrue();
        (new Money(10m) >= new Money(10m)).ShouldBeTrue();
        (new Money(-75m) < Money.Zero).ShouldBeTrue();
    }

    [Fact]
    public void Sum_totals_the_sequence()
    {
        Money.Sum([new Money(1450.00m), new Money(1380.00m), new Money(1620.00m)])
            .ShouldBe(new Money(4450.00m));
    }

    [Fact]
    public void Sum_of_empty_is_zero()
    {
        Money.Sum([]).ShouldBe(Money.Zero);
    }

    [Fact]
    public void Sign_predicates_classify_amount()
    {
        Money.Zero.IsZero.ShouldBeTrue();
        new Money(0.01m).IsPositive.ShouldBeTrue();
        new Money(-0.01m).IsNegative.ShouldBeTrue();
        new Money(0.01m).IsNegative.ShouldBeFalse();
    }

    [Theory]
    [InlineData("1450", "1450.00")]
    [InlineData("1450.5", "1450.50")]
    [InlineData("-75", "-75.00")]
    [InlineData("0", "0.00")]
    public void ToString_always_renders_two_decimals(string value, string expected)
    {
        var raw = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        new Money(raw).ToString().ShouldBe(expected);
    }
}
