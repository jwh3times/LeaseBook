using System.Globalization;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// A report grid projected once from a typed row definition. The same declared columns drive the
/// JSON preview and CSV export, so empty results retain their schema and callers never inspect row
/// objects at runtime.
/// </summary>
public sealed record ReportTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object> Rows,
    IReadOnlyList<IReadOnlyList<string>> CsvRows)
{
    public static ReportTable Project<TRow>(
        IReadOnlyList<TRow> rows,
        params ReportColumn<TRow>[] columns)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);

        var names = columns.Select(column => column.Name).ToArray();
        var previewRows = new List<object>(rows.Count);
        var csvRows = new List<IReadOnlyList<string>>(rows.Count);

        foreach (var row in rows)
        {
            var preview = new Dictionary<string, object?>(columns.Length, StringComparer.Ordinal);
            var csv = new string[columns.Length];

            for (var index = 0; index < columns.Length; index++)
            {
                var column = columns[index];
                var value = column.Value(row);
                preview.Add(column.Name, value);
                csv[index] = FormatCsvCell(value);
            }

            previewRows.Add(preview);
            csvRows.Add(csv);
        }

        return new ReportTable(names, previewRows, csvRows);
    }

    private static string FormatCsvCell(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        decimal amount => amount.ToString("0.00########", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime timestamp => timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        Guid id => id.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

public sealed record ReportColumn<TRow>(string Name, Func<TRow, object?> Value);
