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
    /// Pass/fail marks for the fiduciary panel, drawn as vectors rather than typed as text.
    /// <para>
    /// These are the same paths the SPA's icon set uses for <c>check</c> and <c>alert</c>
    /// (<c>web/src/design/Icon.tsx</c>), on the same 24×24 viewBox, so the printed statement and
    /// the on-screen one carry the identical mark. Drawing them means the mark depends on no font:
    /// the previous <c>"✓"</c> / <c>"✗"</c> were Dingbats (U+2713 / U+2717) that the bundled Lato
    /// does not cover and the chiseled runtime image had no system font to supply, so they printed
    /// as blanks (issue #396). Shape — a tick versus a warning triangle — is what keeps pass/fail
    /// legible without relying on the green/red coloring.
    /// </para>
    /// </summary>
    private const string CheckPath = "M5 13l4 4L19 7";

    private const string AlertPath =
        "M12 9v4m0 4h.01M10.3 3.9L2 18a2 2 0 001.7 3h16.6a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z";

    /// <summary>
    /// Wraps one of the paths above in a standalone SVG document. <c>Icon.tsx</c> strokes with
    /// <c>currentColor</c>, which has no meaning outside a DOM, so the color is resolved here from
    /// the same palette entries the text marks used, rather than restated as a literal that could
    /// drift away from them.
    /// </summary>
    private static string MarkSvg(bool passed)
    {
        var color = passed ? Colors.Green.Darken2 : Colors.Red.Darken2;
        var stroke = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        var path = passed ? CheckPath : AlertPath;

        return $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="{stroke}" stroke-width="2.25" stroke-linecap="round" stroke-linejoin="round"><path d="{path}"/></svg>""";
    }

    /// <summary>
    /// Renders <paramref name="view"/> to PDF bytes. Thread-safe (stateless).
    /// The <c>%PDF</c> header at bytes 0–3 can be used for a fast integrity check.
    /// </summary>
    public static byte[] Render(StatementView view)
    {
        // Idempotent, and deliberately the first statement: static-field initializers run before any
        // static constructor body, so this guarantees the settings are applied before QuestPDF reads
        // them, regardless of field-initializer ordering. Covers CLI verbs, seeders and tests that
        // reach the renderer without going through the host pipeline.
        QuestPdfSetup.Ensure();
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
            // Beginning balance — carried forward from the issued prior statement when one exists (ADR-045)
            if (v.CarryForward is { } carryForward)
            {
                ComposeCarryForward(col, v, carryForward);
            }
            else
            {
                MoneyRow(col, "Beginning balance", v.Beginning, bold: false);
            }

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

    /// <summary>
    /// The prior-period adjustments block (ADR-045): the balance the owner's previous statement closed at,
    /// each entry posted into that period or earlier after it was issued — with both its accounting date
    /// and its posted date — and the adjusted beginning balance the rest of the statement builds on.
    /// Meaning is carried by labels and signs, never by colour.
    /// </summary>
    private static void ComposeCarryForward(ColumnDescriptor col, StatementView v, CarryForwardView cf)
    {
        var parens = v.Branding.ParenthesizedNegatives;
        var issuedPeriod = new DateTime(cf.IssuedYear, cf.IssuedMonth, 1)
            .ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var issuedOn = cf.IssuedAt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

        MoneyRow(col, $"Beginning balance, as issued for {issuedPeriod} (issued {issuedOn})", cf.IssuedEnding, bold: false);

        col.Item().PaddingTop(4).Row(row =>
        {
            row.RelativeItem().Text("PRIOR-PERIOD ADJUSTMENTS").Style(SectionHeaderStyle);
            row.ConstantItem(120).AlignRight().Text(FormatMoney(cf.Total, parens)).Style(SectionHeaderStyle);
        });

        foreach (var line in cf.Lines)
        {
            col.Item().PaddingVertical(1).Row(row =>
            {
                row.RelativeItem().Column(inner =>
                {
                    inner.Item().Text(
                        $"{line.Date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}  {line.Description}")
                        .Style(LineStyle);
                    var posted = $"Posted {line.PostedAt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";
                    inner.Item().Text(line.PropertyAddress is null ? posted : $"{posted} · {line.PropertyAddress}")
                        .Style(SubStyle);
                });
                row.ConstantItem(120).AlignRight().Text(FormatMoney(line.Amount, parens)).Style(LineStyle);
            });
        }

        if (cf.Unitemized != 0m)
        {
            col.Item().PaddingVertical(1).Row(row =>
            {
                row.RelativeItem().Column(inner =>
                {
                    inner.Item().Text("Unitemized adjustments").Style(LineStyle);
                    inner.Item().Text("Not attributable to a single entry posted after the prior statement")
                        .Style(SubStyle);
                });
                row.ConstantItem(120).AlignRight().Text(FormatMoney(cf.Unitemized, parens)).Style(LineStyle);
            });
        }

        col.Item().PaddingVertical(2).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
        MoneyRow(col, "Adjusted beginning balance", v.Beginning, bold: true);
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

    // ─── Fiduciary panel (NC 21 NCAC 58A .0117 transparency) ─────────────────

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
            row.ConstantItem(14).PaddingTop(1).Width(10).Height(10).Svg(MarkSvg(passed));
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
