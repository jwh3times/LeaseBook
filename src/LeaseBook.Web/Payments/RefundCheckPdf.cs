using System.Globalization;
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
/// Renders a refund check onto pre-printed, check-on-top voucher stock (US Letter: the check in the top
/// 3.5 inches, two stubs below; #473). The stock already carries the bank, the organization, the check
/// number and the MICR line, so the face prints only the variable fields — date, payee, amount in figures
/// and words, the window-envelope address block and the memo. Each stub repeats the record for the file
/// copy and the payee.
/// <para>
/// Every field is placed absolutely and shifted by the bank account's calibration offsets (points), so
/// one adjustment moves the whole page onto the stock's boxes. Positions are clamped inside the page.
/// </para>
/// </summary>
public static class RefundCheckPdf
{
    private const float PageWidth = 612f;
    private const float PageHeight = 792f;
    // The host runs with invariant globalization (as does the chiseled image), so en-US is unavailable;
    // invariant formats amounts as 1,450.00 and dates as 03/02/2026, exactly as a US check reads.
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly TextStyle FieldStyle = TextStyle.Default.FontSize(11).FontColor(Colors.Black);
    private static readonly TextStyle StubStyle = TextStyle.Default.FontSize(9.5f).FontColor(Colors.Black);
    private static readonly TextStyle StubTitleStyle = TextStyle.Default.FontSize(10.5f).Bold().FontColor(Colors.Black);

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
            page.Content().Layers(layers =>
            {
                layers.PrimaryLayer().Height(PageHeight).Width(PageWidth);

                void Place(float x, float y, float width, Action<IContainer> content)
                {
                    var left = Math.Clamp(x + dx, 0f, PageWidth - 20f);
                    var top = Math.Clamp(y + dy, 0f, PageHeight - 20f);
                    content(layers.Layer().PaddingLeft(left).PaddingTop(top).Width(Math.Min(width, PageWidth - left)));
                }

                if (check.AlignmentTest)
                {
                    Place(150, 18, 320, c => c.Text("ALIGNMENT TEST — NOT A CHECK").Bold().FontSize(12));
                }

                // The check face.
                Place(468, 48, 120, c => c.Text(date));
                Place(72, 86, 380, c => c.Text(check.PayeeName));
                Place(468, 86, 120, c => c.Text(amount).Bold());
                Place(36, 118, 430, c => c.Text(words));
                Place(72, 150, 300, c => c.Column(col =>
                {
                    foreach (var line in check.AddressLines)
                    {
                        col.Item().Text(line);
                    }
                }));
                if (!string.IsNullOrWhiteSpace(check.Memo))
                {
                    Place(54, 212, 280, c => c.Text(check.Memo));
                }

                // Two identical stubs: the file copy and the payee's copy.
                foreach (var top in new[] { 270f, 540f })
                {
                    Place(36, top, 540, c => c.Column(col =>
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
            });
        })).GeneratePdf();
    }

    /// <summary>A sample check for lining up the stock: same layout, obviously non-negotiable content.</summary>
    public static RefundCheckDocument AlignmentSample(DateOnly today) => new(
        0, today, "PAYEE NAME", 1234.56m, ["PAYEE NAME", "123 ADDRESS LINE", "CITY, ST 00000"],
        "MEMO", "Alignment test", AlignmentTest: true);
}
