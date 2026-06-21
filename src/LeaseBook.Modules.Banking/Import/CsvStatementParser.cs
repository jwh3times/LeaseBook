using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace LeaseBook.Modules.Banking.Import;

/// <summary>
/// CSV implementation of <see cref="IStatementParser"/> (CsvHelper, P66 / ADR-015). Reads by the mapped
/// header names, normalizes each row to a signed amount (single <c>Amount</c> column, or
/// <c>Credit − Debit</c>), and tolerates per-row failures — an unreadable row is collected as a
/// <see cref="RowError"/> with its 1-based data-row index, never aborting the import.
/// </summary>
public sealed class CsvStatementParser : IStatementParser
{
    // Tried in order when the column map gives no explicit DateFormat.
    private static readonly string[] FallbackDateFormats =
        ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "yyyy/MM/dd", "dd/MM/yyyy"];

    public ParsedStatement Parse(string content, ColumnMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var rows = new List<ParsedRow>();
        var errors = new List<RowError>();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null, // a mapped column absent on a row → null field, handled per-row
            BadDataFound = null,
        };

        using var reader = new StringReader(content ?? string.Empty);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader())
        {
            return new ParsedStatement(rows, errors); // empty or header-only file
        }

        var rowNumber = 0;
        while (csv.Read())
        {
            rowNumber++;
            try
            {
                var date = ParseDate(csv.GetField(map.Date), map.DateFormat);
                var description = (csv.GetField(map.Description) ?? string.Empty).Trim();
                var amount = ParseAmount(csv, map);
                rows.Add(new ParsedRow(rowNumber, date, description, amount));
            }
            catch (Exception ex) when (ex is FormatException or CsvHelperException or InvalidOperationException)
            {
                errors.Add(new RowError(rowNumber, ex.Message));
            }
        }

        return new ParsedStatement(rows, errors);
    }

    private static DateOnly ParseDate(string? raw, string? explicitFormat)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new FormatException("date is empty.");
        }

        if (explicitFormat is not null)
        {
            return DateOnly.TryParseExact(value, explicitFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
                ? exact
                : throw new FormatException($"date '{value}' does not match format '{explicitFormat}'.");
        }

        return DateOnly.TryParseExact(value, FallbackDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new FormatException($"date '{value}' is not in a recognized format.");
    }

    private static decimal ParseAmount(CsvReader csv, ColumnMap map)
    {
        if (map.Amount is not null)
        {
            return ParseDecimal(csv.GetField(map.Amount));
        }

        // Customer-account convention: credit increases the balance (+), debit decreases it (−).
        var credit = map.Credit is null ? 0m : ParseDecimal(csv.GetField(map.Credit));
        var debit = map.Debit is null ? 0m : ParseDecimal(csv.GetField(map.Debit));
        return credit - debit;
    }

    private static decimal ParseDecimal(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return 0m; // an empty money cell (e.g. the unused side of a debit/credit pair) is zero
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException($"amount '{value}' is not a number.");
        }

        // Money is NUMERIC(14,2); reject a third significant decimal at the boundary (P28) rather than
        // letting it round silently when the row is later wrapped in Money.
        if (decimal.Round(parsed, 2) != parsed)
        {
            throw new FormatException($"amount '{value}' has more than two decimal places.");
        }

        return parsed;
    }
}
