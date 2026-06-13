using System.Text;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// M3 / P55: the focused ledger CSV. A pure render of the existing projection — header row + the seven
/// on-screen columns, money as fixed 2-decimal strings, and the status mirroring the on-screen badge
/// (posted / voided original / reversal row). No DB needed.
/// </summary>
public sealed class TenantLedgerCsvTests
{
    [Fact]
    public void Write_emits_the_header_then_one_row_per_entry_with_the_right_status()
    {
        var voided = Guid.NewGuid();
        var ledger = new TenantLedgerResponse(
            Guid.NewGuid(), 250m,
            [
                new TenantLedgerEntry(
                    voided, new DateOnly(2026, 2, 1), "RentCharged", null, "Rent", "Feb rent",
                    1000m, 0m, 1000m, IsVoided: true, ReversesEntryId: null),
                new TenantLedgerEntry(
                    Guid.NewGuid(), new DateOnly(2026, 2, 2), "EntryVoided", null, "EntryVoided", "VOID: typo",
                    0m, 1000m, 0m, IsVoided: false, ReversesEntryId: voided),
                new TenantLedgerEntry(
                    Guid.NewGuid(), new DateOnly(2026, 2, 3), "PaymentReceived", "ACH", "Payment", null,
                    0m, 250m, -250m, IsVoided: false, ReversesEntryId: null),
            ]);

        var csv = Encoding.UTF8.GetString(TenantLedgerCsv.Write(ledger));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines[0].ShouldBe("Date,Category,Description,Charge,Payment,Balance,Status");
        lines[1].ShouldBe("2026-02-01,Rent,Feb rent,1000.00,0.00,1000.00,Voided");
        lines[2].ShouldBe("2026-02-02,EntryVoided,VOID: typo,0.00,1000.00,0.00,Reversal");
        lines[3].ShouldBe("2026-02-03,Payment,,0.00,250.00,-250.00,Posted");
    }
}
