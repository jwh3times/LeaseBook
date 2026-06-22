using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Renders a <see cref="StatementView"/> as a print-grade PDF (M5 WP-04).
/// Mirrors the <c>screen-owner.jsx</c> layout: branded header, beginning balance,
/// sections with subtotals (tabular right-aligned figures), disbursement, highlighted ending
/// balance, the fiduciary panel (three integrity checks), the reconciliation line,
/// the <b>basis label</b> (Cash / Accrual), and page numbers.
/// <para>
/// Lives in the host because <see cref="StatementView"/> is host-owned (ADR-016 composition root).
/// Does <b>not</b> read the journal — it renders only what the assembler already built.
/// </para>
/// </summary>
public static class StatementPdf
{
    private static readonly TextStyle BrandStyle = TextStyle.Default.FontSize(18).Bold().FontColor(Colors.Grey.Darken3);
    private static readonly TextStyle SubStyle = TextStyle.Default.FontSize(10).FontColor(Colors.Grey.Medium);
    private static readonly TextStyle LabelStyle = TextStyle.Default.FontSize(10).FontColor(Colors.Grey.Darken2);
    private static readonly TextStyle ValueStyle = TextStyle.Default.FontSize(10).FontColor(Colors.Black);
    private static readonly TextStyle SectionHeaderStyle = TextStyle.Default.FontSize(9).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.05f);
    private static readonly TextStyle LineStyle = TextStyle.Default.FontSize(9.5f).FontColor(Colors.Black);
    private static readonly TextStyle EndingStyle = TextStyle.Default.FontSize(11).Bold().FontColor(Colors.Black);
    private static readonly TextStyle FidStyle = TextStyle.Default.FontSize(9).FontColor(Colors.Grey.Darken2);
    private static readonly TextStyle FidBoldStyle = TextStyle.Default.FontSize(9).Bold().FontColor(Colors.Black);

    /// <summary>
    /// Ensures the QuestPDF Community license is set before the first render.
    /// Idempotent — safe to call multiple times. <c>Program.cs</c> also sets it at host startup
    /// (belt-and-suspenders); this method covers direct/test callers that bypass the host pipeline.
    /// Static-field initializers run before any static constructor body, so calling this as the
    /// first statement in <see cref="Render"/> guarantees the license is set before QuestPDF
    /// accesses it, regardless of field-initializer ordering.
    /// LeaseBook qualifies for the free Community tier (under $1M annual revenue).
    /// </summary>
    private static void EnsureLicense()
    {
        if (QuestPDF.Settings.License != LicenseType.Community)
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }
    }

    /// <summary>
    /// Renders <paramref name="view"/> to PDF bytes. Thread-safe (stateless).
    /// The <c>%PDF</c> header at bytes 0–3 can be used for a fast integrity check.
    /// </summary>
    public static byte[] Render(StatementView view)
    {
        EnsureLicense();
        ArgumentNullException.ThrowIfNull(view);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(40);
                page.DefaultTextStyle(TextStyle.Default.FontSize(10).FontColor(Colors.Black));

                page.Header().Element(c => ComposeHeader(c, view));
                page.Content().Element(c => ComposeBody(c, view));
                page.Footer().AlignRight().Text(text =>
                {
                    text.Span("Page ").Style(SubStyle);
                    text.CurrentPageNumber().Style(SubStyle);
                    text.Span(" of ").Style(SubStyle);
                    text.TotalPages().Style(SubStyle);
                });
            });
        }).GeneratePdf();
    }

    // ─── Header: PM branding + statement identity ──────────────────────────────

    private static void ComposeHeader(IContainer container, StatementView v)
    {
        container.PaddingBottom(12).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Row(row =>
        {
            // Left: PM brand
            row.RelativeItem().Column(col =>
            {
                var pmName = v.Branding.CompanyName ?? "Property Manager";
                col.Item().Text(pmName).Style(BrandStyle);
                col.Item().Text("Owner Statement").Style(SubStyle);
            });

            // Right: statement metadata
            row.ConstantItem(240).AlignRight().Column(col =>
            {
                col.Item().Text(text =>
                {
                    text.Span("Owner: ").Style(LabelStyle);
                    text.Span(v.OwnerName).Style(ValueStyle.Bold());
                });
                if (v.PropertyAddress is not null)
                {
                    col.Item().Text(text =>
                    {
                        text.Span("Property: ").Style(LabelStyle);
                        text.Span(v.PropertyAddress).Style(ValueStyle);
                    });
                }
                col.Item().Text(text =>
                {
                    var monthName = new DateTime(v.Year, v.Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
                    text.Span("Period: ").Style(LabelStyle);
                    text.Span(monthName).Style(ValueStyle);
                });
                col.Item().Text(text =>
                {
                    text.Span("Basis: ").Style(LabelStyle);
                    // Capitalize for display: "cash" → "Cash"
                    var basisLabel = v.Basis.Length > 0
                        ? char.ToUpperInvariant(v.Basis[0]) + v.Basis[1..]
                        : v.Basis;
                    text.Span(basisLabel).Style(ValueStyle.Bold());
                });
            });
        });
    }

    // ─── Body: beginning, sections, disbursement, ending, fiduciary ──────────

    private static void ComposeBody(IContainer container, StatementView v)
    {
        container.PaddingTop(8).Column(col =>
        {
            // Beginning balance
            MoneyRow(col, "Beginning balance", v.Beginning, bold: false);

            col.Item().PaddingVertical(2).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            // Statement sections — disbursement is rendered separately below, not via the loop
            foreach (var section in v.Sections.Where(
                s => !s.Key.Equals("Disbursement", StringComparison.OrdinalIgnoreCase)))
            {
                ComposeSection(col, section, v.Branding.ParenthesizedNegatives);
            }

            // Disbursement: dim row between sections and ending balance (prototype: "Owner disbursement …")
            // Sourced from the "Disbursement" section (StatementSectionKey.Disbursement → "Disbursement").
            var disbursement = v.Sections
                .Where(s => s.Key.Equals("Disbursement", StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.Subtotal);

            if (disbursement != 0m)
            {
                col.Item().PaddingVertical(1).Row(row =>
                {
                    row.RelativeItem().Text("Owner disbursement").Style(SubStyle);
                    row.ConstantItem(120).AlignRight()
                        .Text(FormatMoney(disbursement, v.Branding.ParenthesizedNegatives))
                        .Style(SubStyle);
                });
            }

            col.Item().PaddingVertical(2).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            // Ending balance — highlighted
            col.Item().Background(Colors.Blue.Lighten4).Padding(8).Row(row =>
            {
                row.RelativeItem().Text("Ending balance").Style(EndingStyle);
                row.ConstantItem(120).AlignRight().Text(FormatMoney(v.Ending, v.Branding.ParenthesizedNegatives)).Style(EndingStyle);
            });

            col.Item().PaddingTop(16);

            // Fiduciary panel
            ComposeFiduciary(col, v);
        });
    }

    private static void ComposeSection(ColumnDescriptor col, StatementSectionView section, bool parens)
    {
        // Section header (uppercase label + subtotal)
        col.Item().PaddingTop(8).Row(row =>
        {
            row.RelativeItem().Text(section.Title.ToUpperInvariant()).Style(SectionHeaderStyle);
            row.ConstantItem(120).AlignRight().Text(FormatMoney(section.Subtotal, parens)).Style(SectionHeaderStyle);
        });

        // Line items
        foreach (var line in section.Lines)
        {
            col.Item().PaddingVertical(1).Row(row =>
            {
                row.RelativeItem().Column(inner =>
                {
                    inner.Item().Text(line.Description).Style(LineStyle);
                    if (line.PropertyAddress is not null)
                    {
                        inner.Item().Text(line.PropertyAddress).Style(SubStyle);
                    }
                });
                row.ConstantItem(120).AlignRight().Text(FormatMoney(line.Amount, parens)).Style(LineStyle);
            });
        }

        col.Item().PaddingVertical(2).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
    }

    // ─── Fiduciary panel (NC 58A .0116 transparency) ─────────────────────────

    private static void ComposeFiduciary(ColumnDescriptor col, StatementView v)
    {
        var fid = v.Fiduciary;

        col.Item().Background(Colors.Blue.Lighten5).Border(0.5f).BorderColor(Colors.Blue.Lighten3)
            .Padding(10).Column(inner =>
            {
                inner.Item().Text("Fiduciary Integrity").Style(FidBoldStyle.FontSize(10));
                inner.Item().PaddingTop(4);

                FiduciaryCheck(inner, "PM income excluded",
                    fid.PmIncomeExcluded,
                    "Management fees and tenant-sourced income never appear as owner income.");

                FiduciaryCheck(inner, "Deposits recognized on application",
                    fid.DepositsRecognizedOnApplication,
                    "Security deposits appear as income only in the period they are applied.");

                FiduciaryCheck(inner, "Statement balanced",
                    fid.Balanced,
                    fid.Balanced
                        ? $"Tie-out variance: $0.00"
                        : $"Tie-out variance: {FormatMoney(fid.Variance, false)} (review required)");

                // Reconciliation line (if available)
                if (fid.LatestReconciledBank is { } snap)
                {
                    inner.Item().PaddingTop(6).Text(text =>
                    {
                        text.Span("Reconciles to bank — ").Style(FidStyle);
                        text.Span($"{snap.Year}-{snap.Month:D2}").Style(FidStyle.Bold());
                        text.Span(" — $0.00 variance").Style(FidStyle);
                    });
                }
            });
    }

    private static void FiduciaryCheck(ColumnDescriptor col, string label, bool passed, string detail)
    {
        col.Item().PaddingTop(3).Row(row =>
        {
            row.ConstantItem(14).Text(passed ? "✓" : "✗")
                .Style(FidStyle.FontColor(passed ? Colors.Green.Darken2 : Colors.Red.Darken2).Bold());
            row.RelativeItem().Column(inner =>
            {
                inner.Item().Text(label).Style(FidBoldStyle);
                inner.Item().Text(detail).Style(FidStyle);
            });
        });
    }

    // ─── Money formatting ─────────────────────────────────────────────────────

    private static string FormatMoney(decimal value, bool parens)
    {
        // Tabular numerals via fixed 2-decimal formatting; never float.
        if (parens && value < 0)
        {
            return $"({(-value).ToString("N2", CultureInfo.InvariantCulture)})";
        }

        return value.ToString("N2", CultureInfo.InvariantCulture);
    }

    private static void MoneyRow(ColumnDescriptor col, string label, decimal value, bool bold)
    {
        col.Item().PaddingVertical(4).Row(row =>
        {
            row.RelativeItem().Text(label).Style(bold ? ValueStyle.Bold() : ValueStyle);
            row.ConstantItem(120).AlignRight().Text(FormatMoney(value, false)).Style(bold ? ValueStyle.Bold() : ValueStyle);
        });
    }
}
