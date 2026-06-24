using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

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

        using var reader = new StreamReader(csv);
        using var parser = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        });

        if (!parser.Read() || !parser.ReadHeader())
        {
            errors.Add(new RowError(0, "*", "file has no header row"));
            return new ImportResult<TRow>(rows, errors);
        }

        var resolved = profile.Resolve(parser.HeaderRecord ?? [], out var missing);
        errors.AddRange(missing);
        if (missing.Count > 0)
            return new ImportResult<TRow>(rows, errors); // missing required columns; cannot process rows

        var rowNumber = 0;
        while (parser.Read())
        {
            rowNumber++;
            var cells = resolved.ToDictionary(
                kv => kv.Key,
                kv => parser.GetField(kv.Value) ?? string.Empty,
                StringComparer.Ordinal);

            var ctx = new RowContext(cells, rowNumber, errors);
            var bound = bind(ctx);
            if (bound is not null) rows.Add(bound);
        }

        return new ImportResult<TRow>(rows, errors);
    }
}
