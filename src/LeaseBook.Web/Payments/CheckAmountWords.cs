using System.Globalization;

namespace LeaseBook.Web.Payments;

/// <summary>
/// The legal amount line of a check (#473): whole dollars in words, cents as a fraction —
/// <c>1450.00</c> → <c>One thousand four hundred fifty and 00/100</c>. No "and" inside the dollar words,
/// so the only "and" on the line is the one before the cents. Supports up to 999,999,999.99.
/// </summary>
internal static class CheckAmountWords
{
    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] Tens =
        ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    public static string Format(decimal amount)
    {
        if (amount < 0m || amount >= 1_000_000_000m || decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A check amount must be 0–999,999,999.99 in whole cents.");
        }

        var dollars = (long)decimal.Truncate(amount);
        var cents = (int)((amount - dollars) * 100m);
        var words = Words(dollars);
        return char.ToUpper(words[0], CultureInfo.InvariantCulture) + words[1..]
            + " and " + cents.ToString("00", CultureInfo.InvariantCulture) + "/100";
    }

    private static string Words(long n)
    {
        if (n == 0)
        {
            return Ones[0];
        }

        var parts = new List<string>();
        foreach (var (scale, name) in new[] { (1_000_000L, "million"), (1_000L, "thousand"), (1L, "") })
        {
            var chunk = (int)(n / scale % 1000);
            if (chunk == 0)
            {
                continue;
            }

            parts.Add(name.Length == 0 ? Hundreds(chunk) : $"{Hundreds(chunk)} {name}");
        }

        return string.Join(' ', parts);
    }

    private static string Hundreds(int n)
    {
        var parts = new List<string>();
        if (n >= 100)
        {
            parts.Add($"{Ones[n / 100]} hundred");
            n %= 100;
        }

        if (n >= 20)
        {
            parts.Add(n % 10 == 0 ? Tens[n / 10] : $"{Tens[n / 10]}-{Ones[n % 10]}");
        }
        else if (n > 0)
        {
            parts.Add(Ones[n]);
        }

        return string.Join(' ', parts);
    }
}
