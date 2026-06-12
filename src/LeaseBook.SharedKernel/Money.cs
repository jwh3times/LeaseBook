using System.Globalization;

namespace LeaseBook.SharedKernel;

/// <summary>
/// Money is <see cref="decimal"/>, never float/double (CLAUDE.md). Stored as NUMERIC(14,2) via
/// <see cref="MoneyConverter"/>. This is the minimal wrapper; the M1 accounting engine adds the
/// arithmetic, comparison, and rounding surface (with its own tests).
/// </summary>
public readonly record struct Money(decimal Amount)
{
    public static readonly Money Zero = new(0m);

    public override string ToString() => Amount.ToString("0.00", CultureInfo.InvariantCulture);
}
