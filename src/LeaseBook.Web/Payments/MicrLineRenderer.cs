using System.Globalization;
using System.Text;
using LeaseBook.Modules.Payments.Domain;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace LeaseBook.Web.Payments;

/// <summary>
/// Draws a check's MICR line as vector paths (#474, ADR-051). The drawing is the whole 5/8 in clear band
/// along the check's bottom edge, as wide as the check: character n's right edge sits
/// 5/16 in + (n − 1) × 1/8 in from the check's right edge (Payments Canada Std 006 §4.4.1, §4.5.3), and a
/// full character is centred on 5/16 in above the bottom edge, inside the 3/16–7/16 in print band (§4.4).
/// The bank's MICR offset moves the whole line; nothing else does — the print offsets that align the other
/// fields with the stock never reach it.
/// </summary>
public static class MicrLineRenderer
{
    /// <summary>The MICR clear band: 5/8 in, measured from the bottom edge.</summary>
    public const double ClearBandInches = 0.625;

    private const double PositionOneRightEdgeInches = 5.0 / 16;
    private const double PitchInches = 1.0 / 8;
    private const double CharacterCentreInches = 5.0 / 16;
    private const double PointsPerUnit = E13BGlyphs.UnitInches * 72;

    /// <summary>
    /// The clear band as SVG, <paramref name="widthPoints"/> wide. Its viewBox is in E-13B grid units, so the
    /// glyph outlines are placed without scaling. Offsets are in points; positive moves right and down.
    /// </summary>
    public static string Svg(MicrLine line, double widthPoints, double offsetXPoints, double offsetYPoints)
    {
        var widthUnits = widthPoints / PointsPerUnit;
        var heightUnits = ClearBandInches / E13BGlyphs.UnitInches;
        var frameTop = heightUnits - CharacterCentreInches / E13BGlyphs.UnitInches - E13BGlyphs.HeightUnits / 2.0
            + offsetYPoints / PointsPerUnit;

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{SvgNumber.Format(widthPoints)}\" height=\"{SvgNumber.Format(ClearBandInches * 72)}\" ");
        svg.Append(CultureInfo.InvariantCulture, $"viewBox=\"0 0 {SvgNumber.Format(widthUnits)} {SvgNumber.Format(heightUnits)}\">");
        foreach (var (position, character) in line.Characters)
        {
            var rightEdge = widthUnits
                - (PositionOneRightEdgeInches + (position - 1) * PitchInches) / E13BGlyphs.UnitInches
                + offsetXPoints / PointsPerUnit;
            var left = rightEdge - E13BGlyphs.WidthUnits(character);
            svg.Append(CultureInfo.InvariantCulture,
                $"<path fill=\"#000\" fill-rule=\"evenodd\" d=\"{E13BGlyphs.OutlinePath(character, left, frameTop)}\"/>");
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>
    /// Fills the container's full width with the MICR clear band (5/8 in tall). Place it so its bottom edge
    /// is the check's bottom edge and its width the check's width.
    /// </summary>
    public static void MicrLine(this IContainer container, MicrLine line, decimal micrOffsetXPoints, decimal micrOffsetYPoints) =>
        container.Height((float)(ClearBandInches * 72))
            .Svg(size => Svg(line, size.Width, (double)micrOffsetXPoints, (double)micrOffsetYPoints));
}
