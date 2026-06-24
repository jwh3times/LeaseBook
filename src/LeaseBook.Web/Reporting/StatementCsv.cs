using System.Globalization;
using System.Text;
using CsvHelper;
using LeaseBook.SharedKernel.Csv;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Renders a <see cref="StatementView"/> as UTF-8 CSV (M5 WP-04).
/// Mirrors the <c>TenantLedgerCsv</c> pattern: columns per line item, with subtotal rows at section
/// boundaries and an ending-balance summary row. Money columns are fixed 2-decimal invariant strings
/// (never float). Numbers are stored as decimal strings so spreadsheet tools import them correctly.
/// <para>
/// Lives in the host because <see cref="StatementView"/> is host-owned (ADR-016 composition root).
/// </para>
/// </summary>
public static class StatementCsv
{
    // Column headers match the screen-owner.jsx columns visible on the PDF.
    private static readonly string[] Headers = ["Section", "Date", "Description", "Property", "Amount"];

    /// <summary>
    /// Serializes <paramref name="view"/> to UTF-8 CSV bytes.
    /// Rows: one per <see cref="StatementLineView"/>; subtotal rows follow each section;
    /// a trailing summary block contains the beginning, ending, and basis.
    /// </summary>
    public static byte[] Write(StatementView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = new CsvWriter(buffer, CultureInfo.InvariantCulture))
        {
            // Header row
            foreach (var h in Headers)
            {
                csv.WriteField(h);
            }

            csv.NextRecord();

            // Summary header
            WriteRow(csv, "STATEMENT", "", $"Owner: {view.OwnerName}", "", "");
            WriteRow(csv, "STATEMENT", "", $"Period: {view.Year}-{view.Month:D2}", "", "");
            WriteRow(csv, "STATEMENT", "", $"Basis: {view.Basis}", "", "");
            WriteRow(csv, "STATEMENT", "", "Beginning balance", "", FormatMoney(view.Beginning));
            csv.NextRecord();

            // Sections
            foreach (var section in view.Sections)
            {
                foreach (var line in section.Lines)
                {
                    WriteRow(
                        csv,
                        section.Title,
                        line.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        line.Description,
                        line.PropertyAddress ?? string.Empty,
                        FormatMoney(line.Amount));
                }

                // Subtotal row for the section
                WriteRow(csv, section.Title, "", $"Subtotal — {section.Title}", "", FormatMoney(section.Subtotal));
                csv.NextRecord();
            }

            // Ending balance summary
            WriteRow(csv, "ENDING", "", "Ending balance", "", FormatMoney(view.Ending));

            // Fiduciary flags
            csv.NextRecord();
            WriteRow(csv, "FIDUCIARY", "", "PM income excluded", "", view.Fiduciary.PmIncomeExcluded ? "true" : "false");
            WriteRow(csv, "FIDUCIARY", "", "Deposits recognized on application", "", view.Fiduciary.DepositsRecognizedOnApplication ? "true" : "false");
            WriteRow(csv, "FIDUCIARY", "", "Statement balanced", "", view.Fiduciary.Balanced ? "true" : "false");
            WriteRow(csv, "FIDUCIARY", "", "Tie-out variance", "", FormatMoney(view.Fiduciary.Variance));
        }

        return Encoding.UTF8.GetBytes(buffer.ToString());
    }

    // Single chokepoint for every data/summary row — routes each cell through the formula-injection
    // guard. Owner names, descriptions and property addresses are free text (and, via M7, imported);
    // server-formatted dates and money are kept intact by the guard's number-aware rule.
    private static void WriteRow(CsvWriter csv, string section, string date, string desc, string property, string amount)
    {
        csv.WriteField(CsvFormulaGuard.Neutralize(section));
        csv.WriteField(CsvFormulaGuard.Neutralize(date));
        csv.WriteField(CsvFormulaGuard.Neutralize(desc));
        csv.WriteField(CsvFormulaGuard.Neutralize(property));
        csv.WriteField(CsvFormulaGuard.Neutralize(amount));
        csv.NextRecord();
    }

    private static string FormatMoney(decimal value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);
}
