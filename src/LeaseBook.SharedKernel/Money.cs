using System.Globalization;

namespace LeaseBook.SharedKernel;

/// <summary>
/// Money is <see cref="decimal"/>, never float/double (CLAUDE.md). Stored as NUMERIC(14,2) via
/// <see cref="MoneyConverter"/>.
/// <para>
/// The accounting engine (M1) builds on this surface: addition/subtraction/comparison plus
/// <see cref="Sum"/>. Construction is the single rounding gate (P28): a value carrying more than two
/// decimal places cannot be represented in NUMERIC(14,2) without silent rounding, which is a
/// correctness bug in trust accounting — so it throws at construction. No amount that reaches the
/// journal was ever silently rounded. Multiplication/percentage math (fee computation) is M6's
/// problem and gets its own rounding ADR; M1 takes every amount as given.
/// </para>
/// </summary>
public readonly record struct Money
{
    public static readonly Money Zero = new(0m);

    public Money(decimal amount)
    {
        // decimal.Round is numeric, so a trailing-zero scale (e.g. 1.230) is accepted: it equals its
        // 2-place rounding. Only a genuine third significant decimal (1.235) changes under rounding
        // and is rejected. No MidpointRounding choice can matter here — we reject, never round.
        if (decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount,
                "Money must have at most 2 decimal places (NUMERIC(14,2)); silent rounding is forbidden (P28).");
        }

        Amount = amount;
    }

    public decimal Amount { get; init; }

    /// <summary>Explicit factory mirroring the constructor — the rounding gate is the same.</summary>
    public static Money Of(decimal amount) => new(amount);

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);

    public static Money operator -(Money left, Money right) => new(left.Amount - right.Amount);

    public static Money operator -(Money value) => new(-value.Amount);

    public static bool operator <(Money left, Money right) => left.Amount < right.Amount;

    public static bool operator <=(Money left, Money right) => left.Amount <= right.Amount;

    public static bool operator >(Money left, Money right) => left.Amount > right.Amount;

    public static bool operator >=(Money left, Money right) => left.Amount >= right.Amount;

    /// <summary>Sums money in decimal space (associative, no intermediate rounding) then re-gates once.</summary>
    public static Money Sum(IEnumerable<Money> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var total = 0m;
        foreach (var value in values)
        {
            total += value.Amount;
        }

        return new Money(total);
    }

    public override string ToString() => Amount.ToString("0.00", CultureInfo.InvariantCulture);
}
