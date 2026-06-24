using System.Globalization;

namespace LeaseBook.SharedKernel.Csv;

/// <summary>
/// Neutralizes CSV formula injection (OWASP "CSV Injection" / CWE-1236). A spreadsheet evaluates a
/// cell whose first character is a formula trigger (<c>= + - @</c>, tab, or CR), so an exported value
/// like <c>=cmd|'/c calc'!A1</c> can run on open. <see cref="Neutralize"/> prefixes such a value with
/// an apostrophe so the spreadsheet treats it as text. Server-formatted signed numbers (a negative
/// balance such as <c>-250.00</c>) are recognized and left intact so exported money still imports and
/// sums as a number — only genuine text payloads are escaped. Every LeaseBook CSV export routes its
/// cell values through this guard.
/// </summary>
public static class CsvFormulaGuard
{
    private const NumberStyles SignedDecimal =
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;

    public static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var first = value[0];
        if (first is not ('=' or '+' or '-' or '@' or '\t' or '\r'))
        {
            return value;
        }

        // A server-formatted signed number (money like "-250.00") is not a formula — keep it numeric
        // so the exported value still imports and sums as a number. Only +/- can begin a real number,
        // and only when the whole cell parses cleanly; "=…", "@…", tab/CR, or "-2+cmd" get the guard.
        if (first is '-' or '+'
            && decimal.TryParse(value, SignedDecimal, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        return "'" + value;
    }
}
