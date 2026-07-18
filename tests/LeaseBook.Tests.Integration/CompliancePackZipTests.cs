using System.IO.Compression;
using System.Text;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Audit;
using LeaseBook.Web.Reporting;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-8: the compliance-pack ZIP renderer. Pure formatting — asserts the ZIP contains the discrete
/// audit documents (cover PDF, per-artifact CSVs, immutable reconciliation JSON, manifest), that CSV
/// cells are formula-injection-guarded, and that the reconciliation snapshot is byte-preserved.
/// </summary>
public sealed class CompliancePackZipTests
{
    [Fact]
    public void Render_produces_discrete_guarded_documents()
    {
        var bankId = UuidV7.NewId();
        var equation = new TrustEquationRow(bankId, Book: 1200m, OwnerEquity: 1000m,
            DepositLiabilities: 0m, Prepayments: 200m, HeldPmFees: 0m, Variance: 0m);
        var pack = new CompliancePack(
            new CompliancePackCover(bankId, "Operating Trust", "trust",
                new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), OpeningBook: 0m, equation),
            TrustLedger:
            [
                // A formula-injection payload in a free-text cell must be neutralized on export.
                new RegisterRow(UuidV7.NewId(), new DateOnly(2026, 3, 15), "=cmd|' /C calc'!A0", null, 1200.00m, null, default),
            ],
            DepositRegister: [new DepositRegisterRow(UuidV7.NewId(), "prepayment", 200.00m)],
            ReconciliationHistory:
            [
                new CompliancePackReconciliation(UuidV7.NewId(), 2026, 3, 5000.00m, DateTime.UtcNow, "{\"variance\":0}"),
            ],
            AuditTrail:
            [
                new AuditExtractRow(DateTime.UtcNow, "journal_entries", UuidV7.NewId(), "insert", "Renée Calloway", "r@example.com"),
            ]);

        var bytes = CompliancePackZip.Render(pack, "Tarheel PM", DateTime.UtcNow);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        names.ShouldBe(
            [
                "cover.pdf", "trust-account-ledger.csv", "security-deposit-register.csv",
                "audit-log-extract.csv", "reconciliation-2026-03.json", "manifest.csv",
            ],
            ignoreOrder: true);

        // Cover is a real PDF.
        var cover = Bytes(archive, "cover.pdf");
        Encoding.ASCII.GetString(cover, 0, 4).ShouldBe("%PDF");
        cover.Length.ShouldBeGreaterThan(1_000);

        // Formula injection neutralized (apostrophe-prefixed), money left intact.
        var ledger = Text(archive, "trust-account-ledger.csv");
        ledger.ShouldContain("'=cmd");
        ledger.ShouldNotContain(",=cmd");
        ledger.ShouldContain("1200.00");

        // Immutable reconciliation snapshot is byte-preserved.
        Text(archive, "reconciliation-2026-03.json").ShouldBe("{\"variance\":0}");

        // Manifest indexes the discrete artifacts; audit CSV carries the resolved actor.
        var manifest = Text(archive, "manifest.csv");
        manifest.ShouldContain("trust-account-ledger.csv");
        manifest.ShouldContain("security-deposit-register.csv");
        manifest.ShouldContain("audit-log-extract.csv");
        Text(archive, "audit-log-extract.csv").ShouldContain("Renée Calloway");
    }

    private static string Text(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static byte[] Bytes(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
