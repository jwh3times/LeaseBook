using System.Globalization;
using System.Text;
using CsvHelper;
using LeaseBook.Modules.Reporting.Catalog;
using LeaseBook.SharedKernel.Csv;

namespace LeaseBook.Modules.Reporting.Rendering;

/// <summary>
/// Renders a generic report grid (the <c>preview</c> endpoint rows) as UTF-8 CSV (M5 WP-04).
/// Takes a <see cref="ReportDescriptor"/> for the header metadata and a row-major string table
/// so this class has no dependency on any module data layer — it only serializes what the
/// caller already assembled.
/// <para>
/// Money columns must already be formatted as decimal strings (never float) by the caller;
/// this renderer does not re-parse or re-format values.
/// </para>
/// </summary>
public static class ReportCsv
{
    // CsvHelper 34 rejects records of different lengths and no longer quotes a field for a leading or trailing
    // space. This export mixes title, header and data rows, and keeps the quoting CsvHelper 33 gave it: a
    // field with the separator, a quote or a line break, or one that starts or ends with a space.
    private static readonly CsvWriterOptions Options = new()
    {
        ValidateFieldCount = false,
        ShouldQuote = (field, _, _, _) => field.Length > 0 && (field[0] == ' ' || field[^1] == ' '),
    };

    /// <summary>
    /// Serializes <paramref name="rows"/> to UTF-8 CSV bytes.
    /// </summary>
    /// <param name="descriptor">Catalog entry — used to write a report-identity header.</param>
    /// <param name="columns">Column names (first row in the output after the identity header).</param>
    /// <param name="rows">Row-major data rows; each inner list must match <paramref name="columns"/> in length.</param>
    public static byte[] Write(
        ReportDescriptor descriptor,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<ReportCsvMetadata>? appliedFilters = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = CsvWriter.Create(buffer, Options, leaveOpen: true))
        {
            // Identity header row (two cells: report name, category)
            csv.WriteField(descriptor.Name);
            csv.WriteField(descriptor.Category);
            csv.EndRecord();

            foreach (var filter in appliedFilters ?? [])
            {
                csv.WriteField(filter.Name);
                csv.WriteField(CsvFormulaGuard.Neutralize(filter.Value));
                csv.EndRecord();
            }

            // Column header row
            foreach (var col in columns)
            {
                csv.WriteField(col);
            }

            EndRecord(csv, buffer, columns.Count);

            // Data rows — cells are generic report values (owner/tenant names, addresses, money).
            // Route each through the formula-injection guard; signed numbers are kept numeric.
            foreach (var row in rows)
            {
                foreach (var cell in row)
                {
                    csv.WriteField(CsvFormulaGuard.Neutralize(cell));
                }

                EndRecord(csv, buffer, row.Count);
            }
        }

        return Encoding.UTF8.GetBytes(buffer.ToString());
    }

    // A record with no cells is an empty line. CsvHelper 34 refuses to end a record with no fields, so the
    // writer is flushed and the line ending goes to the buffer itself, in the order the records were written.
    private static void EndRecord(CsvWriter csv, StringWriter buffer, int fields)
    {
        if (fields > 0)
        {
            csv.EndRecord();
            return;
        }

        csv.Flush();
        buffer.Write("\r\n");
    }
}

public sealed record ReportCsvMetadata(string Name, string Value);
