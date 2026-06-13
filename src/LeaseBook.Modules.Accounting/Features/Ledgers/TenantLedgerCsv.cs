using System.Globalization;
using System.Text;
using CsvHelper;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Renders a <see cref="TenantLedgerResponse"/> as CSV (P55) — columns mirroring the on-screen ledger:
/// date, category, description, charge, payment, balance, status. Built from the existing ledger
/// projection (reused, not re-queried). The general report/CSV catalog is M5; this is the one focused
/// ledger export. Money columns are fixed 2-decimal invariant strings (never float).
/// </summary>
public static class TenantLedgerCsv
{
    public static byte[] Write(TenantLedgerResponse ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = new CsvWriter(buffer, CultureInfo.InvariantCulture))
        {
            foreach (var header in new[] { "Date", "Category", "Description", "Charge", "Payment", "Balance", "Status" })
            {
                csv.WriteField(header);
            }

            csv.NextRecord();

            foreach (var row in ledger.Rows)
            {
                csv.WriteField(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                csv.WriteField(row.Category);
                csv.WriteField(row.Description ?? string.Empty);
                csv.WriteField(row.Charge.ToString("0.00", CultureInfo.InvariantCulture));
                csv.WriteField(row.Payment.ToString("0.00", CultureInfo.InvariantCulture));
                csv.WriteField(row.Balance.ToString("0.00", CultureInfo.InvariantCulture));
                csv.WriteField(Status(row));
                csv.NextRecord();
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
