using System.Globalization;

namespace LeaseBook.Web.Payments;

/// <summary>
/// A number as SVG path and attribute text: invariant culture (the host runs invariant globalization,
/// and SVG only ever reads a dot), four decimals, no trailing zeros.
/// </summary>
internal static class SvgNumber
{
    public static string Format(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
