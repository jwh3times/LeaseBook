using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Reporting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Shouldly;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #474 part 2: a MICR line rendered into a real PDF and read back as geometry. Expected positions come
/// from the inch rules, not from the renderer: character n's right edge sits 5/16 in + (n − 1) × 1/8 in
/// from the check's right edge (Std 006 §4.4.1, §4.5.3; the 1/8 in pitch is X9 TR 100 def. 57), and every
/// character lies in the 1/4 in print band 3/16–7/16 in above the bottom edge (Std 006 §4.4), with a full
/// 0.117 in character centred on 5/16 in. No pure layout test can see magnetic signal, toner or the
/// printer's registration; bank testing is the only check of those.
/// </summary>
public sealed class MicrLineRenderingTests
{
    private const double CheckWidthPoints = 8.5 * 72;
    private const double CheckHeightPoints = 3.5 * 72;
    private const double Tolerance = 0.05; // points: rounding of path coordinates, far inside ±0.038 mm (0.108 pt)
    private static readonly MicrLine Line = MicrLine.Compose("111000012", "123456789U", 1043);

    [Fact]
    public void Every_character_is_a_filled_vector_path_with_its_right_edge_on_its_position()
    {
        var page = RenderPage(0m, 0m);

        page.Letters.ShouldBeEmpty();
        page.NumberOfImages.ShouldBe(0);

        var glyphs = Glyphs(page);
        var expected = Line.Characters.OrderBy(c => c.Position).ToList();
        glyphs.Count.ShouldBe(expected.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            var (position, character) = expected[i];
            var box = glyphs[i];
            var expectedRight = CheckWidthPoints - 72 * (5.0 / 16 + (position - 1) / 8.0);
            box.Right.ShouldBe(expectedRight, Tolerance, $"right edge of position {position} ('{character}')");
            box.Width.ShouldBe(E13BGlyphs.WidthUnits(character) * 0.0065 * 72, Tolerance, $"width of '{character}'");
        }
    }

    [Fact]
    public void Every_character_sits_in_the_print_band_and_digits_share_one_baseline()
    {
        var glyphs = Glyphs(RenderPage(0m, 0m));
        var digitBottom = 72 * (5.0 / 16 - 0.117 / 2); // a full-height character centred on 5/16 in

        foreach (var box in glyphs)
        {
            box.Bottom.ShouldBeGreaterThanOrEqualTo(72 * 3.0 / 16 - Tolerance);
            box.Top.ShouldBeLessThanOrEqualTo(72 * 7.0 / 16 + Tolerance);
        }

        foreach (var (box, (_, character)) in glyphs.Zip(Line.Characters.OrderBy(c => c.Position)))
        {
            if (char.IsAsciiDigit(character))
            {
                box.Bottom.ShouldBe(digitBottom, Tolerance, $"bottom of '{character}'");
                box.Height.ShouldBe(72 * 0.117, Tolerance);
            }
        }
    }

    [Fact]
    public void The_bank_s_micr_offset_moves_the_whole_line_and_nothing_else()
    {
        var nominal = Glyphs(RenderPage(0m, 0m));
        var moved = Glyphs(RenderPage(4.5m, -3m)); // right 4.5 pt, up 3 pt (positive y moves down, as in print settings)

        moved.Count.ShouldBe(nominal.Count);
        foreach (var (before, after) in nominal.Zip(moved))
        {
            (after.Left - before.Left).ShouldBe(4.5, Tolerance);
            (after.Bottom - before.Bottom).ShouldBe(3, Tolerance);
            after.Width.ShouldBe(before.Width, Tolerance);
        }
    }

    private sealed record Box(double Left, double Bottom, double Right, double Top)
    {
        public double Width => Right - Left;
        public double Height => Top - Bottom;
    }

    /// <summary>One box per filled path, right-most (position 1 side) first.</summary>
    private static List<Box> Glyphs(Page page) =>
        page.Paths.Where(p => p.IsFilled)
            .Select(p => p.GetBoundingRectangle())
            .Where(r => r.HasValue)
            .Select(r => new Box(r!.Value.Left, r.Value.Bottom, r.Value.Right, r.Value.Top))
            .OrderByDescending(b => b.Right)
            .ToList();

    /// <summary>A check-sized page with the MICR clear band along its bottom edge.</summary>
    private static Page RenderPage(decimal micrOffsetX, decimal micrOffsetY)
    {
        QuestPdfSetup.Ensure();
        var bytes = Document.Create(container => container.Page(page =>
        {
            page.Size(new QuestPDF.Helpers.PageSize((float)CheckWidthPoints, (float)CheckHeightPoints));
            page.Margin(0);
            page.Content().AlignBottom().MicrLine(Line, micrOffsetX, micrOffsetY);
        })).GeneratePdf();

        using var document = PdfDocument.Open(bytes);
        return document.GetPage(1);
    }
}
