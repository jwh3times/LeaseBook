using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Reporting;
using QuestPDF.Fluent;
using Shouldly;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #474 part 3: a refund check printed on blank check-on-top stock, read back from the rendered bytes. The
/// check is the top 3.5 in of the Letter page, so its bottom edge is 252 pt from the top of the sheet and the
/// 5/8 in MICR clear band runs 207–252 pt from the top (PDF y 540–585). Nothing but E-13B may print there
/// (Std 006 §4.2; X9 TR 100 def. 185), and checks drawn on a trust account must bear the words
/// "Trust Account" (21 NCAC 58A .0117(b)). The MICR line expected in the band is part 2's renderer drawn on
/// its own, translated onto the check — not something this layout computes.
/// </summary>
public sealed class RefundCheckBlankStockPdfTests
{
    private const double BandBottom = 792 - 252; // PDF y of the check's bottom edge
    private const double BandTop = BandBottom + 45;
    private const double Tolerance = 0.05;

    private static readonly RefundCheckDocument Check = new(
        1043, new DateOnly(2026, 3, 2), "Jasmine Carter", 1450m,
        ["Jasmine Carter", "412 Oakmont Ave", "#2B", "Asheville, NC 28801"], "Deposit return", "Security deposit refund");

    private static readonly BlankCheckStock Stock = new(
        "Blue Ridge Property Management LLC", ["18 Haywood St, Suite 200", "Asheville, NC 28801", "(828) 555-0142"],
        "First Mountain Bank", new BankMicrPrint(new BankMicrNumbers("111000012", "123456789U"), 0m, 0m));

    private static readonly BlankCheckStock LongestStock = Stock with
    {
        OrganizationName = new string('W', 200),
        OrganizationAddress = [new string('W', 200), new string('W', 120), new string('W', 40)],
        BankName = new string('W', 120),
    };

    [Fact]
    public void Blank_stock_prints_what_pre_printed_stock_would_have_carried()
    {
        var face = FaceText(RefundCheckPdf.RenderBlank(Check, Stock));

        face.ShouldContain("Blue Ridge Property Management LLC");
        face.ShouldContain("TRUST ACCOUNT");
        face.ShouldContain("18 Haywood St, Suite 200");
        face.ShouldContain("First Mountain Bank");
        face.ShouldContain("1043"); // the check number on the face, matching the MICR serial
        face.ShouldContain("03/02/2026");
        face.ShouldContain("Jasmine Carter");
        face.ShouldContain("**1,450.00");
        face.ShouldContain("One thousand four hundred fifty and 00/100");
        face.ShouldContain("Deposit return");
        face.ShouldNotContain("SPECIMEN");
    }

    public static TheoryData<string, int, int> Corners() => new()
    {
        { "check", 0, 0 }, { "check", -18, -18 }, { "check", 18, 18 }, { "check", -18, 18 }, { "check", 18, -18 },
        { "longest", 0, 0 }, { "longest", 18, 18 }, { "longest", -18, -18 },
        { "specimen", 0, 0 }, { "specimen", 18, 18 }, { "specimen", -18, -18 },
    };

    [Theory]
    [MemberData(nameof(Corners))]
    public void Nothing_but_the_micr_line_prints_in_the_clear_band(string kind, int micrOffsetX, int micrOffsetY)
    {
        var check = kind switch
        {
            "longest" => Longest,
            "specimen" => RefundCheckPdf.AlignmentSample(new DateOnly(2026, 3, 2)),
            _ => Check,
        };
        var stock = (kind == "longest" ? LongestStock : Stock) with
        {
            Micr = Stock.Micr with { MicrOffsetXPoints = micrOffsetX, MicrOffsetYPoints = micrOffsetY },
        };
        var expected = kind == "specimen"
            ? MicrLine.Specimen("111000012", "123456789U")
            : MicrLine.Compose("111000012", "123456789U", check.CheckNumber);
        using var document = PdfDocument.Open(RefundCheckPdf.RenderBlank(check, stock));
        var page = document.GetPage(1);

        page.Letters.Where(l => Intersects(l.BoundingBox.Bottom, l.BoundingBox.Top)).Select(l => l.Value)
            .ShouldBeEmpty();
        page.NumberOfImages.ShouldBe(0);
        Same(BandPaths(page), Reference(expected, micrOffsetX, micrOffsetY))
            .ShouldBeTrue("the clear band holds exactly the MICR line");
    }

    [Fact]
    public void The_micr_line_carries_this_check_s_number()
    {
        using var document = PdfDocument.Open(RefundCheckPdf.RenderBlank(Check with { CheckNumber = 1044 }, Stock));

        var page = document.GetPage(1);
        Same(BandPaths(page), Reference(MicrLine.Compose("111000012", "123456789U", 1044), 0, 0)).ShouldBeTrue();
        Same(BandPaths(page), Reference(MicrLine.Compose("111000012", "123456789U", 1043), 0, 0)).ShouldBeFalse();
    }

    [Fact]
    public void The_alignment_page_on_blank_stock_is_a_non_negotiable_specimen_with_the_account_s_micr_line()
    {
        var sample = RefundCheckPdf.AlignmentSample(new DateOnly(2026, 3, 2));
        using var document = PdfDocument.Open(RefundCheckPdf.RenderBlank(sample, Stock));
        var page = document.GetPage(1);

        var face = string.Join(" ", page.GetWords().Select(w => w.Text));
        face.ShouldContain("SPECIMEN");
        face.ShouldContain("NON-NEGOTIABLE");
        face.ShouldContain("VOID");
        face.ShouldContain("No. 0000");
        page.GetWords().Count(w => w.Text == "#0000").ShouldBe(2); // both stubs match the face
        Same(BandPaths(page), Reference(MicrLine.Specimen("111000012", "123456789U"), 0, 0)).ShouldBeTrue();
    }

    [Fact]
    public void The_numbers_never_appear_as_text()
    {
        // The MICR line is paths; a routing or account number as text would be a second, unchecked copy.
        var text = string.Join("", PdfDocument.Open(RefundCheckPdf.RenderBlank(Check, Stock)).GetPage(1).Letters
            .Select(l => l.Value));

        text.ShouldNotContain("111000012");
        text.ShouldNotContain("123456789");
        Stock.ToString().ShouldNotContain("111000012");
        Stock.ToString().ShouldNotContain("123456789");
    }

    private static readonly RefundCheckDocument Longest = Check with
    {
        PayeeName = new string('W', 120),
        AddressLines = [new string('W', 120), new string('W', 120), new string('W', 120), "Asheville, NC 28801-1234"],
        Memo = new string('M', 60),
        Amount = 777_777.77m,
    };

    private static bool Intersects(double bottom, double top) => top > BandBottom + Tolerance && bottom < BandTop - Tolerance;

    private static string FaceText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join(" ", document.GetPage(1).GetWords().Where(w => w.BoundingBox.Bottom > BandBottom).Select(w => w.Text));
    }

    /// <summary>Every path that reaches into the clear band, left to right.</summary>
    private static List<Box> BandPaths(Page page) => Boxes(page, 0).Where(b => Intersects(b.Bottom, b.Top)).ToList();

    private static bool Same(List<Box> actual, List<Box> expected) =>
        actual.Count > 0 && actual.Count == expected.Count && actual.Zip(expected).All(p =>
            Math.Abs(p.First.Left - p.Second.Left) < Tolerance && Math.Abs(p.First.Right - p.Second.Right) < Tolerance
            && Math.Abs(p.First.Bottom - p.Second.Bottom) < Tolerance && Math.Abs(p.First.Top - p.Second.Top) < Tolerance);

    /// <summary>Part 2's MICR line drawn alone along the bottom of a check-sized page, moved onto the Letter sheet.</summary>
    private static List<Box> Reference(MicrLine line, decimal micrOffsetX, decimal micrOffsetY)
    {
        QuestPdfSetup.Ensure();
        var bytes = Document.Create(container => container.Page(page =>
        {
            page.Size(new QuestPDF.Helpers.PageSize(612, 252));
            page.Margin(0);
            page.Content().AlignBottom().MicrLine(line, micrOffsetX, micrOffsetY);
        })).GeneratePdf();
        using var document = PdfDocument.Open(bytes);
        return Boxes(document.GetPage(1), BandBottom).ToList();
    }

    private sealed record Box(double Left, double Bottom, double Right, double Top);

    private static IEnumerable<Box> Boxes(Page page, double shiftY) =>
        page.Paths.Select(p => p.GetBoundingRectangle()).Where(r => r.HasValue)
            .Select(r => new Box(r!.Value.Left, r.Value.Bottom + shiftY, r.Value.Right, r.Value.Top + shiftY))
            .OrderBy(b => b.Left).ThenBy(b => b.Bottom);
}
