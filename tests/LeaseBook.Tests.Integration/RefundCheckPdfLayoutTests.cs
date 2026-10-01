using LeaseBook.Web.Payments;
using Shouldly;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #473: the refund-check PDF's layout, read back from the rendered bytes. Fields land at fixed
/// positions on pre-printed stock, and a bank account's calibration offsets move every field by exactly
/// that much — right for +x, down the page for +y (PDF y grows upward, so the word's y falls).
/// No database: the renderer is pure.
/// </summary>
public sealed class RefundCheckPdfLayoutTests
{
    private static readonly RefundCheckDocument Check = new(
        1043, new DateOnly(2026, 3, 2), "Jasmine Carter", 1450m,
        ["Jasmine Carter", "412 Oakmont Ave", "#2B", "Asheville, NC 28801"], "Deposit return", "Security deposit refund");

    [Fact]
    public void The_face_prints_every_variable_field_and_both_stubs_repeat_the_record()
    {
        var words = Words(RefundCheckPdf.Render(Check, 0m, 0m));
        var text = string.Join(" ", words.Select(w => w.Text));

        text.ShouldContain("03/02/2026");
        text.ShouldContain("**1,450.00");
        text.ShouldContain("One thousand four hundred fifty and 00/100");
        text.ShouldContain("Asheville, NC 28801");
        text.ShouldContain("Deposit return");
        words.Count(w => w.Text == "#1043").ShouldBe(2); // one per stub; the stock carries the face number
        text.ShouldNotContain("ALIGNMENT");
    }

    [Fact]
    public void Calibration_offsets_move_the_fields_by_exactly_the_offset()
    {
        var baseline = Payee(Words(RefundCheckPdf.Render(Check, 0m, 0m)));
        var shifted = Payee(Words(RefundCheckPdf.Render(Check, 9m, 6m)));

        (shifted.Left - baseline.Left).ShouldBe(9, tolerance: 0.01);
        (baseline.Bottom - shifted.Bottom).ShouldBe(6, tolerance: 0.01);
    }

    [Fact]
    public void The_payee_sits_on_the_pay_to_line_of_the_check_in_the_top_three_and_a_half_inches()
    {
        var payee = Payee(Words(RefundCheckPdf.Render(Check, 0m, 0m)));

        payee.Left.ShouldBe(72, tolerance: 0.5);
        (792 - payee.Top).ShouldBeInRange(86, 100);
    }

    [Fact]
    public void Maximum_length_fields_stay_inside_their_own_boxes()
    {
        var longest = string.Join(' ', Enumerable.Repeat("Wwwwwwwwwwwwww", 9))[..120];
        var check = Check with
        {
            PayeeName = longest,
            AddressLines = [longest, longest, longest, "Asheville, NC 28801-1234"],
            Memo = new string('M', 60),
            Amount = 777_777.77m,
        };

        var words = Words(RefundCheckPdf.Render(check, 0m, 0m));

        // Each face field is confined to its box (top-of-page coordinates): payee 86–102, amount words
        // 118–134, address 150–208, memo 212–226; the first stub starts at 270.
        static double FromTop(double pdfY) => 792 - pdfY;
        var payee = words.Where(w => w.Text.StartsWith("Wwww", StringComparison.Ordinal)
            && FromTop(w.BoundingBox.Top) < 110).ToList();
        payee.ShouldNotBeEmpty();
        payee.All(w => FromTop(w.BoundingBox.Top) >= 85 && FromTop(w.BoundingBox.Bottom) <= 103).ShouldBeTrue();
        words.Where(w => w.Text == "Asheville,").Min(w => FromTop(w.BoundingBox.Bottom)).ShouldBeLessThanOrEqualTo(209);
        words.Where(w => w.Text.StartsWith("MMMM", StringComparison.Ordinal) && FromTop(w.BoundingBox.Top) < 260)
            .All(w => FromTop(w.BoundingBox.Top) >= 211 && FromTop(w.BoundingBox.Bottom) <= 227).ShouldBeTrue();
    }

    [Fact]
    public void The_largest_offsets_move_every_field_together()
    {
        var baseline = Words(RefundCheckPdf.Render(Check, 0m, 0m));
        var shifted = Words(RefundCheckPdf.Render(Check, -72m, -72m));

        // The payee (x=72) lands on the sheet edge; the amount-words line (x=36) moves past it and is
        // clipped like the printer would — it is never pinned at the edge out of step with the rest.
        (Payee(baseline).Left - Payee(shifted).Left).ShouldBe(72, tolerance: 0.01);
        shifted.Where(w => w.Text == "One").All(w => Math.Abs(w.BoundingBox.Left) > 1).ShouldBeTrue();
    }

    [Fact]
    public void The_alignment_page_is_marked_as_not_a_check()
    {
        var sample = RefundCheckPdf.AlignmentSample(new DateOnly(2026, 3, 2));
        var text = string.Join(" ", Words(RefundCheckPdf.Render(sample, 0m, 0m)).Select(w => w.Text));
        text.ShouldContain("ALIGNMENT TEST — NOT A CHECK");
    }

    // The face's payee is the first "Jasmine" on the page (the address block and stubs come later).
    private static PdfRectangle Payee(IReadOnlyList<Word> words) =>
        words.Where(w => w.Text == "Jasmine").OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left)
            .First().BoundingBox;

    private static IReadOnlyList<Word> Words(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        document.NumberOfPages.ShouldBe(1);
        return document.GetPage(1).GetWords().ToList();
    }
}
