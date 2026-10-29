using CsvHelper;

namespace LeaseBook.Migrator.Csv;

/// <summary>
/// Per-row context passed to the <c>bind</c> delegate. Exposes the canonical cell values for the
/// current row and a <see cref="Reject{TRow}"/> helper that records an error and returns null so the
/// row is dropped from <see cref="ImportResult{TRow}.Rows"/> without throwing.
/// </summary>
public sealed class RowContext
{
    internal RowContext(IReadOnlyDictionary<string, string> cells, int rowNumber, List<RowError> errors)
    {
        Cells = cells;
        RowNumber = rowNumber;
        _errors = errors;
    }

    /// <summary>Canonical field name → raw cell value for the current row.</summary>
    public IReadOnlyDictionary<string, string> Cells { get; }

    /// <summary>1-based data row number (header excluded).</summary>
    public int RowNumber { get; }

    private readonly List<RowError> _errors;

    /// <summary>
    /// Records a field-level error on the current row and returns null, causing the row to be
    /// excluded from <see cref="ImportResult{TRow}.Rows"/>. Use in the <c>bind</c> delegate when
    /// a cell value is invalid.
    /// </summary>
    public TRow? Reject<TRow>(string field, string reason) where TRow : class
    {
        _errors.Add(new RowError(RowNumber, field, reason));
        return null;
    }
}

/// <summary>
/// Tolerant, mapping-profile-driven CSV reader (spec §6). Collect-and-continue: header problems and
/// per-row binding failures are recorded as <see cref="RowError"/>s; one bad row never sinks the batch.
/// Pure — no DB, no domain types. The <c>bind</c> delegate receives a <see cref="RowContext"/> that
/// carries canonical cells and an explicit rejection sink (no static state, no AsyncLocal).
/// </summary>
public static class CsvImporter
{
    public static ImportResult<TRow> Read<TRow>(
        Stream csv,
        ColumnMappingProfile profile,
        Func<RowContext, TRow?> bind)
        where TRow : class
    {
        var rows = new List<TRow>();
        var errors = new List<RowError>();
        var rowNumber = 0;

        // CsvHelper 34 guesses the separator from the first lines unless one is named (a one-column file whose
        // values hold semicolons reads as semicolon separated), and trims tabs as well as spaces. An export is
        // comma separated, and only spaces are trimmed. A row with malformed quoting is skipped and
        // reported rather than read: recovering would piece a value together ("1"2 reads as 12), and no
        // imported amount may come from a guess. The reader reports a skipped row while it reads, in file
        // order, so it takes its own row number.
        using var parser = CsvReader.Create(new StreamReader(csv), new CsvReaderOptions
        {
            HasHeader = true,
            Separator = ',',
            TrimWhitespace = true,
            WhitespaceCharacters = " ",
            MalformedData = CsvMalformedDataHandling.Skip,
            OnMalformedRecord = record =>
            {
                rowNumber++;
                errors.Add(new RowError(rowNumber, "*", $"row skipped: {Malformed(record.Reason)}"));
            },
        });

        if (parser.Headers.Count == 0)
        {
            errors.Add(new RowError(0, "*", "file has no header row"));
            return new ImportResult<TRow>(rows, errors);
        }

        var resolved = profile.Resolve(parser.Headers, out var missing);
        errors.AddRange(missing);
        if (missing.Count > 0)
            return new ImportResult<TRow>(rows, errors); // missing required columns; cannot process rows

        while (parser.Read())
        {
            rowNumber++;
            var cells = resolved.ToDictionary(
                kv => kv.Key,
                kv => parser.GetString(kv.Value),
                StringComparer.Ordinal);

            var ctx = new RowContext(cells, rowNumber, errors);
            var bound = bind(ctx);
            if (bound is not null) rows.Add(bound);
        }

        return new ImportResult<TRow>(rows, errors);
    }

    private static string Malformed(CsvMalformedReason reason) => reason switch
    {
        CsvMalformedReason.StrayQuote => "a quote inside an unquoted value",
        CsvMalformedReason.TextAfterClosingQuote => "text after the closing quote of a value",
        CsvMalformedReason.UnterminatedQuote => "a quoted value that is never closed",
        _ => "malformed quoting",
    };
}
