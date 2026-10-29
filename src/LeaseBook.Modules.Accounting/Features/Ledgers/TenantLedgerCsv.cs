using System.Globalization;
using System.Text;
using CsvHelper;
using LeaseBook.SharedKernel.Csv;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Renders a <see cref="TenantLedgerResponse"/> as CSV (P55) — columns mirroring the on-screen ledger:
/// date, category, description, internal note (staff-only, #468), charge, payment, balance, status. Built from the existing ledger
/// projection (reused, not re-queried). The general report/CSV catalog is M5; this is the one focused
/// ledger export. Money columns are fixed 2-decimal invariant strings (never float).
/// </summary>
public static class TenantLedgerCsv
{
    // CsvHelper 34 no longer quotes a field for a leading or trailing space. This export keeps the quoting
    // CsvHelper 33 gave it: a field with the separator, a quote or a line break, or one that starts or ends
    // with a space.
    private static readonly CsvWriterOptions Options = new()
    {
        ShouldQuote = (field, _, _, _) => field.Length > 0 && (field[0] == ' ' || field[^1] == ' '),
    };

    public static byte[] Write(TenantLedgerResponse ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = CsvWriter.Create(buffer, Options, leaveOpen: true))
        {
            foreach (var header in new[] { "Date", "Category", "Description", "Internal note", "Charge", "Payment", "Balance", "Status" })
            {
                csv.WriteField(header);
            }

            csv.EndRecord();

            foreach (var row in ledger.Rows)
            {
                // Free-text / derived-string cells route through the formula-injection guard; the
                // server-formatted date and money cells are safe by construction (Neutralize keeps
                // signed numbers numeric anyway).
                csv.WriteField(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                csv.WriteField(CsvFormulaGuard.Neutralize(row.Category));
                csv.WriteField(CsvFormulaGuard.Neutralize(row.Description));
                csv.WriteField(CsvFormulaGuard.Neutralize(row.InternalNote));
                csv.WriteField(row.Charge.ToString("0.00", CultureInfo.InvariantCulture));
                csv.WriteField(row.Payment.ToString("0.00", CultureInfo.InvariantCulture));
                csv.WriteField(row.Balance.ToString("0.00", CultureInfo.InvariantCulture));
                csv.WriteField(CsvFormulaGuard.Neutralize(Status(row)));
                csv.EndRecord();
            }
        }

        return Encoding.UTF8.GetBytes(buffer.ToString());
    }

    /// <summary>Status mirrors the on-screen badge: a voided original, a reversal row, or a live posting.</summary>
    private static string Status(TenantLedgerEntry row) =>
        row.IsVoided ? "Voided"
        : row.ReversesEntryId is not null ? "Reversal"
        : "Posted";
}
