using System.Globalization;
using System.IO.Compression;
using System.Text;
using CsvHelper;
using LeaseBook.SharedKernel.Csv;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Renders a <see cref="CompliancePack"/> to a ZIP of discrete audit documents (WP-8): a QuestPDF
/// cover/index that carries the trust-equation tie-out, one CSV per tabular artifact (every cell routed
/// through <see cref="CsvFormulaGuard"/>), the immutable reconciliation report snapshots verbatim as
/// JSON, and a CSV manifest. Pure formatting — it renders only what the assembler already composed and
/// reads no journal (ADR-016 composition root). Auditors get discrete files, not one mega-PDF.
/// </summary>
public static class CompliancePackZip
{
    public const string CoverFile = "cover.pdf";
    public const string LedgerFile = "trust-account-ledger.csv";
    public const string DepositFile = "security-deposit-register.csv";
    public const string AuditFile = "audit-log-extract.csv";
    public const string ManifestFile = "manifest.csv";

    public static byte[] Render(CompliancePack pack, string companyName, DateTime generatedAt)
    {
        ArgumentNullException.ThrowIfNull(pack);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, CoverFile, CoverPdf(pack, companyName, generatedAt));
            AddEntry(zip, LedgerFile, LedgerCsv(pack));
            AddEntry(zip, DepositFile, DepositCsv(pack));
            AddEntry(zip, AuditFile, AuditCsv(pack));

            foreach (var recon in pack.ReconciliationHistory)
            {
                AddEntry(
                    zip,
                    $"reconciliation-{recon.Year:D4}-{recon.Month:D2}.json",
                    Encoding.UTF8.GetBytes(recon.ReportJson ?? "{}"));
            }

            AddEntry(zip, ManifestFile, ManifestCsv(pack, generatedAt));
        }

        return buffer.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    // ─── Tabular artifacts (CSV; every cell formula-guarded) ──────────────────────

    private static byte[] LedgerCsv(CompliancePack pack) => Csv(
        "Trust account ledger",
        ["Date", "Description", "Property", "Deposit", "Withdrawal", "Status"],
        pack.TrustLedger.Select(r => new[]
        {
            r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.Description ?? string.Empty,
            r.PropertyId?.ToString() ?? string.Empty,
            Money(r.Deposit),
            Money(r.Withdrawal),
            r.Status.ToString(),
        }));

    private static byte[] DepositCsv(CompliancePack pack) => Csv(
        "Security-deposit liability register",
        ["Tenant", "Kind", "Held"],
        pack.DepositRegister.Select(r => new[]
        {
            r.TenantId.ToString(),
            r.Kind,
            Money(r.Held),
        }));

    private static byte[] AuditCsv(CompliancePack pack) => Csv(
        "Audit-log extract",
        ["OccurredAt", "EntityType", "EntityId", "Action", "Actor", "ActorEmail"],
        pack.AuditTrail.Select(r => new[]
        {
            r.OccurredAt.ToString("u", CultureInfo.InvariantCulture),
            r.EntityType,
            r.EntityId.ToString(),
            r.Action,
            r.ActorName,
            r.ActorEmail ?? string.Empty,
        }));

    private static byte[] ManifestCsv(CompliancePack pack, DateTime generatedAt)
    {
        var files = new List<string[]>
        {
            new[] { CoverFile, "Cover / index with the trust-equation tie-out" },
            new[] { LedgerFile, $"Trust account ledger ({pack.TrustLedger.Count} rows)" },
            new[] { DepositFile, $"Security-deposit liability register ({pack.DepositRegister.Count} rows)" },
            new[] { AuditFile, $"Money-touching audit-log extract ({pack.AuditTrail.Count} rows)" },
        };
        files.AddRange(pack.ReconciliationHistory.Select(r => new[]
        {
            $"reconciliation-{r.Year:D4}-{r.Month:D2}.json",
            $"Finalized reconciliation report {r.Year}-{r.Month:D2} (immutable snapshot)",
        }));
        files.Add(new[] { ManifestFile, $"This manifest — generated {generatedAt.ToString("u", CultureInfo.InvariantCulture)}" });

        return Csv("Contents", ["File", "Description"], files);
    }

    private static byte[] Csv(string title, string[] columns, IEnumerable<IReadOnlyList<string>> rows)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteField(title);
            csv.NextRecord();
            foreach (var col in columns)
            {
                csv.WriteField(col);
            }

            csv.NextRecord();
            foreach (var row in rows)
            {
                foreach (var cell in row)
                {
                    csv.WriteField(CsvFormulaGuard.Neutralize(cell));
                }

                csv.NextRecord();
            }
        }

        return Encoding.UTF8.GetBytes(writer.ToString());
    }

    private static string Money(decimal? value) =>
        value?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;

    // ─── Cover / index PDF ────────────────────────────────────────────────────────

    private static readonly TextStyle Brand = TextStyle.Default.FontSize(18).Bold().FontColor(Colors.Grey.Darken3);
    private static readonly TextStyle Sub = TextStyle.Default.FontSize(10).FontColor(Colors.Grey.Medium);
    private static readonly TextStyle Label = TextStyle.Default.FontSize(10).FontColor(Colors.Grey.Darken2);
    private static readonly TextStyle Value = TextStyle.Default.FontSize(10).FontColor(Colors.Black);
    private static readonly TextStyle SectionHead = TextStyle.Default.FontSize(9).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.05f);
    private static readonly TextStyle Ending = TextStyle.Default.FontSize(11).Bold().FontColor(Colors.Black);

    private static byte[] CoverPdf(CompliancePack pack, string companyName, DateTime generatedAt)
    {
        if (QuestPDF.Settings.License != LicenseType.Community)
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        var cover = pack.Cover;
        var eq = cover.ClosingEquation;
        var movement = eq.Book - cover.OpeningBook;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(40);
                page.DefaultTextStyle(TextStyle.Default.FontSize(10).FontColor(Colors.Black));

                page.Header().PaddingBottom(12).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(string.IsNullOrWhiteSpace(companyName) ? "Property Manager" : companyName).Style(Brand);
                        col.Item().Text("Trust Compliance Pack").Style(Sub);
                    });
                    row.ConstantItem(240).AlignRight().Column(col =>
                    {
                        col.Item().Text(t => { t.Span("Trust account: ").Style(Label); t.Span(cover.BankName).Style(Value.Bold()); });
                        col.Item().Text(t =>
                        {
                            t.Span("Period: ").Style(Label);
                            t.Span($"{cover.PeriodStart:yyyy-MM-dd} – {cover.PeriodEnd:yyyy-MM-dd}").Style(Value);
                        });
                        col.Item().Text(t =>
                        {
                            t.Span("Generated: ").Style(Label);
                            t.Span(generatedAt.ToString("u", CultureInfo.InvariantCulture)).Style(Value);
                        });
                    });
                });

                page.Content().PaddingTop(8).Column(col =>
                {
                    MoneyRow(col, "Opening balance", cover.OpeningBook);
                    MoneyRow(col, "Period activity", movement);
                    col.Item().PaddingVertical(2).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                    col.Item().Background(Colors.Blue.Lighten4).Padding(8).Row(row =>
                    {
                        row.RelativeItem().Text("Ending balance (period end)").Style(Ending);
                        row.ConstantItem(140).AlignRight().Text(Money(eq.Book)).Style(Ending);
                    });

                    col.Item().PaddingTop(16).Text("TRUST EQUATION — AS OF PERIOD END").Style(SectionHead);
                    MoneyRow(col, "Owner equity", eq.OwnerEquity);
                    MoneyRow(col, "Deposit liabilities", eq.DepositLiabilities);
                    MoneyRow(col, "Prepayments", eq.Prepayments);
                    MoneyRow(col, "Held PM fees", eq.HeldPmFees);
                    col.Item().PaddingVertical(2).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                    MoneyRow(col, "= Book balance", eq.Book, bold: true);
                    MoneyRow(col, "Variance", eq.Variance, bold: true);

                    col.Item().PaddingTop(16).Text("CONTENTS").Style(SectionHead);
                    foreach (var (file, desc) in Contents(pack))
                    {
                        col.Item().PaddingVertical(1).Row(row =>
                        {
                            row.ConstantItem(220).Text(file).Style(Value);
                            row.RelativeItem().Text(desc).Style(Sub);
                        });
                    }
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.Span("Page ").Style(Sub);
                    t.CurrentPageNumber().Style(Sub);
                    t.Span(" of ").Style(Sub);
                    t.TotalPages().Style(Sub);
                });
            });
        }).GeneratePdf();
    }

    private static IEnumerable<(string File, string Description)> Contents(CompliancePack pack)
    {
        yield return (LedgerFile, "Trust account ledger");
        yield return (DepositFile, "Security-deposit liability register");
        yield return (AuditFile, "Money-touching audit-log extract");
        foreach (var r in pack.ReconciliationHistory)
        {
            yield return ($"reconciliation-{r.Year:D4}-{r.Month:D2}.json", $"Reconciliation {r.Year}-{r.Month:D2} (immutable)");
        }

        yield return (ManifestFile, "Manifest");
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static void MoneyRow(ColumnDescriptor col, string label, decimal value, bool bold = false)
    {
        col.Item().PaddingVertical(3).Row(row =>
        {
            row.RelativeItem().Text(label).Style(bold ? Value.Bold() : Value);
            row.ConstantItem(140).AlignRight().Text(Money(value)).Style(bold ? Value.Bold() : Value);
        });
    }
}
