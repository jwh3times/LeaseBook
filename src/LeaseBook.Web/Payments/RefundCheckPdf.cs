using System.Globalization;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.Web.Reporting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LeaseBook.Web.Payments;

/// <summary>What one printed refund check carries. The alignment page uses the same shape with sample data.</summary>
public sealed record RefundCheckDocument(
    int CheckNumber, DateOnly Date, string PayeeName, decimal Amount, IReadOnlyList<string> AddressLines,
    string? Memo, string Purpose, bool AlignmentTest = false);

/// <summary>
/// What blank stock needs that pre-printed stock already carries (#474): the organization's name and address,
/// the bank's name, the account's MICR numbers and the bank's MICR line offsets (points).
/// </summary>
public sealed record BlankCheckStock(
    string OrganizationName, IReadOnlyList<string> OrganizationAddress, string BankName, BankMicrNumbers Micr,
    decimal MicrOffsetXPoints, decimal MicrOffsetYPoints);

/// <summary>
/// Renders a refund check onto check-on-top voucher stock (US Letter: the check in the top 3.5 inches, two
/// stubs below; #473). Pre-printed stock already carries the bank, the organization, the check number and
/// the MICR line, so its face prints only the variable fields — date, payee, amount in figures and words,
/// the window-envelope address block and the memo. Blank stock carries nothing, so LeaseBook prints all of
/// it, MICR line included (#474, ADR-051). Each stub repeats the record for the file copy and the payee.
/// <para>
/// Every field is placed absolutely in its own fixed box, shrinking to fit rather than overflowing. On
/// pre-printed stock the whole page is shifted by the bank account's calibration offsets (points), so one
/// adjustment moves every field onto the stock's boxes together.
/// </para>
/// </summary>
public static class RefundCheckPdf
{
    private const float PageWidth = 612f;
    private const float PageHeight = 792f;
    private const float CheckHeight = 252f;
    private const float ClearBandHeight = (float)(MicrLineRenderer.ClearBandInches * 72);
    // The host runs with invariant globalization (as does the chiseled image), so en-US is unavailable;
    // invariant formats amounts as 1,450.00 and dates as 03/02/2026, exactly as a US check reads.
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly TextStyle FieldStyle = TextStyle.Default.FontSize(11).FontColor(Colors.Black);
    private static readonly TextStyle StubStyle = TextStyle.Default.FontSize(9.5f).FontColor(Colors.Black);
    private static readonly TextStyle StubTitleStyle = TextStyle.Default.FontSize(10.5f).Bold().FontColor(Colors.Black);
    private static readonly TextStyle LabelStyle = TextStyle.Default.FontSize(7).FontColor(Colors.Black);
    private static readonly TextStyle HeaderStyle = TextStyle.Default.FontSize(9).FontColor(Colors.Black);

    public static byte[] Render(RefundCheckDocument check, decimal offsetXPoints, decimal offsetYPoints)
    {
        QuestPdfSetup.Ensure();
        var dx = (float)offsetXPoints;
        var dy = (float)offsetYPoints;
        var amount = "**" + check.Amount.ToString("N2", Invariant);
        var words = CheckAmountWords.Format(check.Amount) + " *****";
        var date = check.Date.ToString("MM/dd/yyyy", Invariant);

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(0);
            page.DefaultTextStyle(FieldStyle);
            // One offset moves the whole page, so every field shifts by exactly the calibration
            // offset; anything pushed past the sheet edge is simply clipped.
            page.Content().OffsetX(dx).OffsetY(dy).Layers(layers =>
            {
                layers.PrimaryLayer().Height(PageHeight).Width(PageWidth);

                // Each field owns a fixed box and shrinks to fit it, so a long but valid payee or address
                // can never run into the next field on the stock.
                void Place(float x, float y, float width, float height, Action<IContainer> content) =>
                    content(layers.Layer().PaddingLeft(x).PaddingTop(y).Width(width).Height(height).ScaleToFit());

                if (check.AlignmentTest)
                {
                    Place(150, 18, 320, 18, c => c.Text("ALIGNMENT TEST — NOT A CHECK").Bold().FontSize(12));
                }

                // The check face.
                Place(468, 48, 120, 16, c => c.Text(date));
                Place(72, 86, 380, 16, c => c.Text(check.PayeeName));
                Place(468, 86, 120, 16, c => c.Text(amount).Bold());
                Place(36, 118, 430, 16, c => c.Text(words));
                Place(72, 150, 300, 58, c => c.Column(col =>
                {
                    foreach (var line in check.AddressLines)
                    {
                        col.Item().Text(line);
                    }
                }));
                if (!string.IsNullOrWhiteSpace(check.Memo))
                {
                    Place(54, 212, 280, 14, c => c.Text(check.Memo));
                }

                Stubs(Place, check, date);
            });
        })).GeneratePdf();
    }

    /// <summary>
    /// Renders a refund check onto blank check-on-top stock (#474, ADR-051). LeaseBook prints everything the
    /// pre-printed stock would have carried: the organization with the words "Trust Account" that every check
    /// drawn on a trust account must bear (21 NCAC 58A .0117(b); refund checks draw only on trust accounts),
    /// the bank, the check number, the field labels and lines, and the MICR line along the check's bottom
    /// edge, composed from the account's numbers and this check's own number — or, on the alignment page, an
    /// all-zero specimen serial.
    /// <para>
    /// The 5/8 in clear band holds nothing but the MICR line: every face field ends at least 12 pt above it.
    /// The print offsets do not apply, because they exist to line fields up with pre-printed boxes and blank
    /// stock has none; were they applied, a field could be pushed into the band. Only the bank's MICR offset
    /// moves anything, and it moves only the MICR line.
    /// </para>
    /// </summary>
    public static byte[] RenderBlank(RefundCheckDocument check, BlankCheckStock stock)
    {
        QuestPdfSetup.Ensure();
        var micr = check.AlignmentTest
            ? MicrLine.Specimen(stock.Micr.RoutingNumber, stock.Micr.OnUsField)
            : MicrLine.Compose(stock.Micr.RoutingNumber, stock.Micr.OnUsField, check.CheckNumber);
        var amount = "**" + check.Amount.ToString("N2", Invariant);
        var words = CheckAmountWords.Format(check.Amount) + " *****";
        var date = check.Date.ToString("MM/dd/yyyy", Invariant);
        var number = check.AlignmentTest ? "0000" : check.CheckNumber.ToString(Invariant);

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(0);
            page.DefaultTextStyle(FieldStyle);
            page.Content().Layers(layers =>
            {
                layers.PrimaryLayer().Height(PageHeight).Width(PageWidth);

                void Place(float x, float y, float width, float height, Action<IContainer> content) =>
                    content(layers.Layer().PaddingLeft(x).PaddingTop(y).Width(width).Height(height).ScaleToFit());
                void Rule(float x, float y, float width) =>
                    layers.Layer().PaddingLeft(x).PaddingTop(y).Width(width).Height(0.75f).Background(Colors.Black);

                // What the stock would have carried: who draws the check, on what account, at which bank.
                Place(36, 14, 260, 54, c => c.Column(col =>
                {
                    col.Item().Text(stock.OrganizationName).Bold();
                    col.Item().Text("TRUST ACCOUNT").Style(HeaderStyle).Bold();
                    foreach (var line in stock.OrganizationAddress)
                    {
                        col.Item().Text(line).Style(HeaderStyle);
                    }
                }));
                Place(306, 14, 156, 28, c => c.Text(stock.BankName).Style(HeaderStyle));
                Place(468, 14, 108, 16, c => c.AlignRight().Text($"No. {number}").Bold());

                // The check face, with the labels and lines the stock would have printed.
                Place(440, 42, 26, 10, c => c.Text("DATE").Style(LabelStyle));
                Place(468, 38, 108, 16, c => c.Text(date));
                Rule(468, 55, 108);
                Place(36, 76, 64, 18, c => c.Column(col =>
                {
                    col.Item().Text("PAY TO THE").Style(LabelStyle);
                    col.Item().Text("ORDER OF").Style(LabelStyle);
                }));
                Place(104, 76, 348, 16, c => c.Text(check.PayeeName));
                Rule(104, 93, 348);
                Place(458, 78, 8, 14, c => c.Text("$").Bold());
                Place(468, 76, 108, 16, c => c.Text(amount).Bold());
                Rule(468, 93, 108);
                Place(36, 100, 480, 16, c => c.Text(words));
                Place(522, 104, 54, 10, c => c.AlignRight().Text("DOLLARS").Style(LabelStyle));
                Rule(36, 117, 540);
                Place(72, 122, 300, 54, c => c.Column(col =>
                {
                    foreach (var line in check.AddressLines)
                    {
                        col.Item().Text(line);
                    }
                }));
                Place(36, 184, 30, 10, c => c.Text("MEMO").Style(LabelStyle));
                if (!string.IsNullOrWhiteSpace(check.Memo))
                {
                    Place(68, 180, 232, 14, c => c.Text(check.Memo));
                }

                Rule(68, 195, 232);
                Rule(340, 180, 236);
                Place(340, 184, 236, 10, c => c.AlignCenter().Text("AUTHORIZED SIGNATURE").Style(LabelStyle));

                if (check.AlignmentTest)
                {
                    Place(380, 124, 196, 40, c => c.Column(col =>
                    {
                        col.Item().AlignCenter().Text("SPECIMEN").Bold().FontSize(16);
                        col.Item().AlignCenter().Text("NON-NEGOTIABLE — VOID").Bold().FontSize(12);
                    }));
                }

                // The MICR clear band: the check's bottom 5/8 in, holding the MICR line and nothing else.
                layers.Layer().PaddingTop(CheckHeight - ClearBandHeight).Width(PageWidth)
                    .MicrLine(micr, stock.MicrOffsetXPoints, stock.MicrOffsetYPoints);

                Stubs(Place, check, date);
            });
        })).GeneratePdf();
    }

    /// <summary>Two identical stubs below the check: the file copy and the payee's copy.</summary>
    private static void Stubs(Action<float, float, float, float, Action<IContainer>> place, RefundCheckDocument check, string date)
    {
        foreach (var top in new[] { 270f, 540f })
        {
            place(36, top, 540, 220, c => c.Column(col =>
            {
                col.Spacing(3);
                col.Item().Text($"Refund check #{check.CheckNumber.ToString(CultureInfo.InvariantCulture)}").Style(StubTitleStyle);
                col.Item().Text($"Date: {date}").Style(StubStyle);
                col.Item().Text($"Payee: {check.PayeeName}").Style(StubStyle);
                col.Item().Text($"Amount: {check.Amount.ToString("N2", Invariant)}").Style(StubStyle);
                col.Item().Text($"For: {check.Purpose}").Style(StubStyle);
                if (!string.IsNullOrWhiteSpace(check.Memo))
                {
                    col.Item().Text($"Memo: {check.Memo}").Style(StubStyle);
                }
            }));
        }
    }

    /// <summary>A sample check for lining up the stock: same layout, obviously non-negotiable content.</summary>
    public static RefundCheckDocument AlignmentSample(DateOnly today) => new(
        0, today, "PAYEE NAME", 1234.56m, ["PAYEE NAME", "123 ADDRESS LINE", "CITY, ST 00000"],
        "MEMO", "Alignment test", AlignmentTest: true);
}
