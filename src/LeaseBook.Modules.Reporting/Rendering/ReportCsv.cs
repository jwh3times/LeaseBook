using System.Globalization;
using System.Text;
using CsvHelper;
using LeaseBook.Modules.Reporting.Catalog;

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
    /// <summary>
    /// Serializes <paramref name="rows"/> to UTF-8 CSV bytes.
    /// </summary>
    /// <param name="descriptor">Catalog entry — used to write a report-identity header.</param>
    /// <param name="columns">Column names (first row in the output after the identity header).</param>
    /// <param name="rows">Row-major data rows; each inner list must match <paramref name="columns"/> in length.</param>
    public static byte[] Write(
        ReportDescriptor descriptor,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = new CsvWriter(buffer, CultureInfo.InvariantCulture))
        {
            // Identity header row (two cells: report name, category)
            csv.WriteField(descriptor.Name);
            csv.WriteField(descriptor.Category);
            csv.NextRecord();

            // Column header row
            foreach (var col in columns)
            {
                csv.WriteField(col);
            }

            csv.NextRecord();

            // Data rows
            foreach (var row in rows)
            {
                foreach (var cell in row)
                {
                    csv.WriteField(cell);
                }

                csv.NextRecord();
            }
        }

        return Encoding.UTF8.GetBytes(buffer.ToString());
    }
}
