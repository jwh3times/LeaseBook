using System.Globalization;
using System.Text;

namespace LeaseBook.Web.Payments;

/// <summary>
/// The 14 E-13B characters as LeaseBook's own vector outlines (#474, ADR-051), drawn from the dimensioned
/// figures 1.8.1–1.8.14 of Payments Canada Standard 006, Appendix I (taken from ISO 1004-1995). No font is
/// shipped or embedded: the MICR line is drawn as paths, so nothing depends on a font licence or on fonts
/// the runtime image does not have.
/// <para>
/// Every Std 006 dimension is a multiple of 0.165 mm, so the outlines are integer polygons on a grid of
/// <see cref="UnitInches"/> (0.0065 in = 0.1651 mm): a character is 18 units tall, its horizontal centre
/// line at 9, and 8 to 14 units wide. Coordinates here run from the character's left edge (x) and bottom
/// edge (y). Corners are rounded as the figures require (notes 1–2: every radius 0.165 mm — one unit —
/// blended with both edges; the zero's are 0.660 mm outside and 0.330 mm inside). The one figure that is
/// not on the grid is the seven's slanted joint, read from the figure to a quarter unit.
/// </para>
/// <para>
/// <c>E13BGlyphTests</c> compares these against an independent drawing of the same figures.
/// </para>
/// </summary>
public static class E13BGlyphs
{
    /// <summary>One grid unit: 0.0065 in (0.1651 mm).</summary>
    public const double UnitInches = 0.0065;

    /// <summary>The height of a full character: 18 units, 0.117 in (2.972 mm).</summary>
    public const int HeightUnits = 18;

    private sealed record Ring((double X, double Y)[] Points, double Radius = 1);

    private static Ring Rect(double x0, double y0, double x1, double y1) =>
        new([(x0, y0), (x1, y0), (x1, y1), (x0, y1)]);

    // Outer rings counter-clockwise (y up); holes clockwise. Figure numbers are Std 006 App. I.
    private static readonly IReadOnlyDictionary<char, Ring[]> Outlines = new Dictionary<char, Ring[]>
    {
        // Fig. 1.8.1: a ring, outer radius 0.660 mm (4 units), inner 0.330 mm (2 units).
        ['0'] = [new([(0, 0), (14, 0), (14, 18), (0, 18)], 4), new([(2, 2), (2, 16), (12, 16), (12, 2)], 2)],
        // Fig. 1.8.2
        ['1'] = [new([(0, 0), (8, 0), (8, 8), (4, 8), (4, 18), (0, 18), (0, 15), (2, 15), (2, 8), (0, 8)])],
        // Fig. 1.8.3
        ['2'] = [new([(0, 0), (8, 0), (8, 2), (2, 2), (2, 8), (8, 8), (8, 18), (0, 18), (0, 16), (6, 16), (6, 10), (0, 10)])],
        // Fig. 1.8.4
        ['3'] = [new([(0, 0), (10, 0), (10, 9), (8, 9), (8, 18), (0, 18), (0, 16), (6, 16), (6, 10), (0, 10), (0, 8), (6, 8), (6, 2), (0, 2)])],
        // Fig. 1.8.5
        ['4'] = [new([(0, 4), (8, 4), (8, 0), (12, 0), (12, 8), (8, 8), (8, 6), (4, 6), (4, 18), (0, 18)])],
        // Fig. 1.8.6
        ['5'] = [new([(0, 0), (10, 0), (10, 10), (2, 10), (2, 16), (10, 16), (10, 18), (0, 18), (0, 8), (8, 8), (8, 2), (0, 2)])],
        // Fig. 1.8.7
        ['6'] = [new([(0, 0), (12, 0), (12, 8), (2, 8), (2, 16), (6, 16), (6, 13), (8, 13), (8, 18), (0, 18)]),
                 new([(2, 2), (2, 6), (10, 6), (10, 2)])],
        // Fig. 1.8.8: the slanted joint between the upper right stroke and the lower stem.
        ['7'] = [new([(4, 0), (6, 0), (6, 8.25), (10, 10.5), (10, 18), (0, 18), (0, 12), (2, 12), (2, 16), (8, 16), (8, 11.5), (4, 9.25)])],
        // Fig. 1.8.9
        ['8'] = [new([(0, 0), (14, 0), (14, 9), (12, 9), (12, 18), (2, 18), (2, 9), (0, 9)]),
                 new([(4, 2), (4, 8), (10, 8), (10, 2)]), new([(4, 10), (4, 16), (10, 16), (10, 10)])],
        // Fig. 1.8.10
        ['9'] = [new([(8, 0), (12, 0), (12, 18), (0, 18), (0, 8), (8, 8)]), new([(2, 10), (2, 16), (10, 16), (10, 10)])],
        // Fig. 1.8.11: transit ⑆
        ['T'] = [Rect(0, 3, 4, 15), Rect(8, 0, 14, 6), Rect(8, 12, 14, 18)],
        // Fig. 1.8.12: amount ⑇ (never printed by the drawer; kept so the set is complete)
        ['A'] = [Rect(0, 0, 4, 8), Rect(6, 4.5, 8, 13.5), Rect(10, 10, 14, 18)],
        // Fig. 1.8.13: On-Us ⑈
        ['U'] = [Rect(0, 3, 2, 15), Rect(4, 3, 6, 15), Rect(8, 9, 14, 17)],
        // Fig. 1.8.14: dash ⑉
        ['-'] = [Rect(0, 5, 4, 13), Rect(6, 5, 10, 13), Rect(12, 5, 14, 13)],
    };

    /// <summary>The width of <paramref name="character"/> in grid units.</summary>
    public static double WidthUnits(char character) =>
        Get(character).SelectMany(r => r.Points).Max(p => p.X);

    /// <summary>
    /// SVG path data for <paramref name="character"/> in grid units, y pointing down, the character's
    /// top-left at the origin and its full 18-unit frame below it. Fill it even-odd: holes are rings too.
    /// </summary>
    public static string OutlinePath(char character, double offsetXUnits = 0, double offsetYUnits = 0)
    {
        var path = new StringBuilder();
        foreach (var ring in Get(character))
        {
            AppendRing(path, ring, offsetXUnits, offsetYUnits);
        }

        return path.ToString().TrimEnd();
    }

    private static Ring[] Get(char character) =>
        Outlines.TryGetValue(character, out var rings)
            ? rings
            : throw new ArgumentOutOfRangeException(nameof(character), character, "Not an E-13B character.");

    /// <summary>
    /// One closed ring with every corner replaced by an arc tangent to both edges. For a corner of interior
    /// angle φ the tangent points sit r / tan(φ/2) from it (r at a right angle), clamped so two arcs never
    /// overlap on a short edge.
    /// </summary>
    private static void AppendRing(StringBuilder path, Ring ring, double dx, double dy)
    {
        // To SVG space: y down, frame top at 0.
        var points = ring.Points.Select(p => (X: p.X + dx, Y: HeightUnits - p.Y + dy)).ToArray();
        var n = points.Length;
        var corners = new (double AX, double AY, double BX, double BY, double R, int Sweep)[n];
        for (var i = 0; i < n; i++)
        {
            var prev = points[(i + n - 1) % n];
            var cur = points[i];
            var next = points[(i + 1) % n];
            var (inX, inY, inLength) = Direction(prev, cur);
            var (outX, outY, outLength) = Direction(cur, next);

            // Interior angle between the edges back to prev and on to next.
            var cosPhi = Math.Clamp(-inX * outX - inY * outY, -1, 1);
            var halfTan = Math.Tan(Math.Acos(cosPhi) / 2);
            var tangent = Math.Min(ring.Radius / halfTan, Math.Min(inLength, outLength) / 2);
            var radius = tangent * halfTan;
            var sweep = inX * outY - inY * outX > 0 ? 1 : 0; // clockwise on screen
            corners[i] = (cur.X - inX * tangent, cur.Y - inY * tangent, cur.X + outX * tangent, cur.Y + outY * tangent, radius, sweep);
        }

        path.Append(CultureInfo.InvariantCulture, $"M{F(corners[0].AX)},{F(corners[0].AY)} ");
        for (var i = 0; i < n; i++)
        {
            var c = corners[i];
            if (i > 0)
            {
                path.Append(CultureInfo.InvariantCulture, $"L{F(c.AX)},{F(c.AY)} ");
            }

            path.Append(CultureInfo.InvariantCulture, $"A{F(c.R)},{F(c.R)} 0 0 {c.Sweep} {F(c.BX)},{F(c.BY)} ");
        }

        path.Append("Z ");
    }

    private static (double X, double Y, double Length) Direction((double X, double Y) from, (double X, double Y) to)
    {
        double x = to.X - from.X, y = to.Y - from.Y;
        var length = Math.Sqrt(x * x + y * y);
        return (x / length, y / length, length);
    }

    private static string F(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
