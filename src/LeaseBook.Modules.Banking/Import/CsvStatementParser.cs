using System.Globalization;
using CsvHelper;

namespace LeaseBook.Modules.Banking.Import;

/// <summary>
/// CSV implementation of <see cref="IStatementParser"/> (CsvHelper, P66 / ADR-015). Reads by the mapped
/// header names, normalizes each row to a signed amount (single <c>Amount</c> column, or
/// <c>Credit − Debit</c>), and tolerates per-row failures — an unreadable row is collected as a
/// <see cref="RowError"/> with its 1-based data-row index, never aborting the import. A map naming a column
/// the header lacks is the one whole-file outcome: <see cref="ParsedStatement.MissingColumns"/>, no rows.
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

        // CsvHelper 34 guesses the separator from the first lines unless one is named, and matches a header
        // name ignoring case when there is no exact match. A statement is comma separated, and its columns are
        // looked up exactly (see Field).
        var options = new CsvReaderOptions
        {
            HasHeader = true,
            Separator = ',',
            MalformedData = CsvMalformedDataHandling.Recover, // a stray quote never aborts the import
        };

        using var csv = CsvReader.Create(new StringReader(content ?? string.Empty), options);

        if (csv.Headers.Count == 0)
        {
            return new ParsedStatement(rows, errors); // empty file
        }

        // A mapped column the header lacks would read as an empty field on every row — a missing amount
        // column imports each row at 0.00 — so a map that does not fit the file is refused whole (#645).
        var missing = MissingColumns(csv, map);
        if (missing.Count > 0)
        {
            return new ParsedStatement(rows, errors, missing);
        }

        var rowNumber = 0;
        while (csv.Read())
        {
            rowNumber++;
            try
            {
                var date = ParseDate(Field(csv, map.Date), map.DateFormat);
                var description = (Field(csv, map.Description) ?? string.Empty).Trim();
                var amount = ParseAmount(csv, map);
                rows.Add(new ParsedRow(rowNumber, date, description, amount));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                errors.Add(new RowError(rowNumber, ex.Message));
            }
        }

        return new ParsedStatement(rows, errors);
    }

    private static List<MissingColumn> MissingColumns(CsvReader csv, ColumnMap map)
    {
        (string Field, string? Name)[] mapped =
        [
            (nameof(ColumnMap.Date), map.Date),
            (nameof(ColumnMap.Description), map.Description),
            (nameof(ColumnMap.Amount), map.Amount),
            (nameof(ColumnMap.Debit), map.Debit),
            (nameof(ColumnMap.Credit), map.Credit),
        ];

        return mapped
            .Where(m => IsMapped(m.Name) && IndexOf(csv, m.Name!) < 0)
            .Select(m => new MissingColumn(m.Field, m.Name!))
            .ToList();
    }

    // MissingColumns runs first, so a mapped column is always found; the null is for a reordering, not a case.
    private static string? Field(CsvReader csv, string name)
    {
        var index = IndexOf(csv, name);
        return index < 0 ? null : csv.GetString(index);
    }

    // The first column whose header is the mapped name. Case counts — a header differing only in case is
    // absent, and the refusal names it — but surrounding spaces on either side do not: the import wizard
    // offers header names trimmed, so "Date, Amount" maps as "Amount".
    private static int IndexOf(CsvReader csv, string name)
    {
        var wanted = name.Trim();
        for (var i = 0; i < csv.Headers.Count; i++)
        {
            if (string.Equals(csv.Headers[i].Trim(), wanted, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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

    // A blank name maps nothing, the same as an absent one: the validator accepts "amount": "" beside a
    // debit/credit pair.
    private static bool IsMapped(string? name) => !string.IsNullOrWhiteSpace(name);

    private static decimal ParseAmount(CsvReader csv, ColumnMap map)
    {
        if (IsMapped(map.Amount))
        {
            return ParseDecimal(Field(csv, map.Amount!));
        }

        // Customer-account convention: credit increases the balance (+), debit decreases it (−).
        var credit = IsMapped(map.Credit) ? ParseDecimal(Field(csv, map.Credit!)) : 0m;
        var debit = IsMapped(map.Debit) ? ParseDecimal(Field(csv, map.Debit!)) : 0m;
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
