using System.Globalization;
using System.Text.RegularExpressions;
using LeaseBook.Web.Payments;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #474 part 2: LeaseBook's own E-13B glyphs (drawn from the dimensioned figures of Payments Canada
/// Standard 006, Appendix I §1.8) against an independent drawing of the same figures — the OFL-licensed
/// outlines in <c>Fixtures/MicrE13BReference</c> (see its README). Two people reading the same scanned
/// figures and landing on the same shape is the check; agreeing with our own reading would prove nothing.
/// Both are compared in E-13B grid units of 0.0065 in (0.1651 mm), the module every Std 006 dimension is a
/// multiple of. Where the two disagree, the figure decides, and the disagreement is pinned here with why.
/// </summary>
public sealed partial class E13BGlyphTests
{
    private const double PointsPerUnit = 0.0065 * 72; // the reference outlines are in points
    private const double ToleranceUnits = 0.038 / 0.1651; // Std 006 App. I §1.8 note 3: ±0.038 mm

    public static TheoryData<char, string> Glyphs => new()
    {
        { '0', "u0030" }, { '1', "u0031" }, { '2', "u0032" }, { '3', "u0033" }, { '4', "u0034" },
        { '5', "u0035" }, { '6', "u0036" }, { '7', "u0037" }, { '8', "u0038" }, { '9', "u0039" },
        { 'T', "u2446" }, { 'A', "u2447" }, { 'U', "u2448" }, { '-', "u2449" },
    };

    [Theory]
    [MemberData(nameof(Glyphs))]
    public void Each_glyph_has_the_reference_outline_s_size(char glyph, string file)
    {
        var ours = Bounds(Rings(E13BGlyphs.OutlinePath(glyph)));
        var reference = ReferenceViewBox(file);

        if (glyph == '0')
        {
            // The reference zero is 13.61 units wide. Std 006 Fig. 1.8.1 dimensions it as 2.146 mm from the
            // outer left edge to the centre line plus the 0.165 mm half-stroke beyond it: 14 units, the same
            // 2.311 mm as the 8 and every symbol. Its left stroke is thinner than the figure's 0.330 mm.
            (ours.Width - 14).ShouldBe(0, ToleranceUnits);
            (reference.Width - 13.61).ShouldBe(0, 0.01);
        }
        else
        {
            ours.Width.ShouldBe(reference.Width, ToleranceUnits, $"width of '{glyph}'");
        }

        ours.Height.ShouldBe(reference.Height, ToleranceUnits, $"height of '{glyph}'");
    }

    [Theory]
    [MemberData(nameof(Glyphs))]
    public void Each_glyph_covers_the_same_area_as_the_reference(char glyph, string file)
    {
        var ours = Rings(E13BGlyphs.OutlinePath(glyph));
        var reference = ReferenceRings(file);
        var oursBox = Bounds(ours);
        // The reference is aligned by its viewBox, not by its points: its corner arcs have slightly unequal
        // radii, which pushes the true circle a tenth of a unit past the edge in places, so its point bounds
        // sit off the outline. The viewBox is the outline's own frame. Ours is aligned by its corner points,
        // which never pass an edge (each arc is tangent to both of its edges).
        var refBox = ReferenceViewBox(file);

        // Sample both on a 0.1-unit grid, each relative to its own frame's top-left corner.
        int both = 0, either = 0;
        for (var x = 0.05; x < Math.Max(oursBox.Width, refBox.Width); x += 0.1)
        {
            for (var y = 0.05; y < Math.Max(oursBox.Height, refBox.Height); y += 0.1)
            {
                var inOurs = Inside(ours, oursBox.X + x, oursBox.Y + y);
                var inRef = Inside(reference, refBox.X + x, refBox.Y + y);
                both += inOurs && inRef ? 1 : 0;
                either += inOurs || inRef ? 1 : 0;
            }
        }

        // The zero differs by the reference's thinner left stroke (0.4 units down its full height; see the
        // size test, which holds ours to the figure's 14 units) and measures 0.88. Everything else agrees to
        // 0.95 or better: what remains is the blend of the 0.165 mm corner radii.
        var overlap = (double)both / either;
        overlap.ShouldBeGreaterThan(glyph == '0' ? 0.85 : 0.95, $"overlap of '{glyph}'");
    }

    [Fact]
    public void The_amount_symbol_is_drawable_but_every_other_character_is_what_a_line_prints()
    {
        foreach (var c in "0123456789TU-A")
        {
            E13BGlyphs.OutlinePath(c).ShouldNotBeNullOrWhiteSpace();
        }

        Should.Throw<ArgumentOutOfRangeException>(() => E13BGlyphs.OutlinePath('X'));
    }

    // ---- geometry helpers (test-only) -----------------------------------------------------------------

    private sealed record Box(double X, double Y, double Width, double Height);

    private static Box Bounds(IReadOnlyList<List<(double X, double Y)>> rings)
    {
        var points = rings.SelectMany(r => r).ToList();
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        return new Box(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Even-odd point-in-polygon over every ring, so holes count as outside.</summary>
    private static bool Inside(IReadOnlyList<List<(double X, double Y)>> rings, double x, double y)
    {
        var inside = false;
        foreach (var ring in rings)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var (xi, yi) = ring[i];
                var (xj, yj) = ring[j];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    private static string ReferenceSvg(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MicrE13BReference", file + ".svg"));

    private static Box ReferenceViewBox(string file)
    {
        var parts = ViewBox().Match(ReferenceSvg(file)).Groups[1].Value
            .Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return new Box(0, 0, parts[2] / PointsPerUnit, parts[3] / PointsPerUnit);
    }

    /// <summary>The reference outline as rings in grid units: its paths, and its rounded rectangles.</summary>
    private static List<List<(double X, double Y)>> ReferenceRings(string file)
    {
        var svg = ReferenceSvg(file);
        var rings = new List<List<(double X, double Y)>>();
        foreach (Match d in PathData().Matches(svg))
        {
            rings.AddRange(Rings(d.Groups[1].Value).Select(r => r.Select(p => (p.X / PointsPerUnit, p.Y / PointsPerUnit)).ToList()));
        }

        foreach (Match rect in RectElement().Matches(svg))
        {
            var a = Attribute().Matches(rect.Value).ToDictionary(m => m.Groups[1].Value, m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            double x = a.GetValueOrDefault("x") / PointsPerUnit, y = a.GetValueOrDefault("y") / PointsPerUnit;
            double w = a["width"] / PointsPerUnit, h = a["height"] / PointsPerUnit;
            rings.Add([(x, y), (x + w, y), (x + w, y + h), (x, y + h)]);
        }

        return rings;
    }

    /// <summary>
    /// SVG path data (M, L, H, V, A, Z, absolute and relative) as closed rings. An arc is sampled along its
    /// true circle, so a rounded corner is compared as drawn.
    /// </summary>
    private static List<List<(double X, double Y)>> Rings(string d)
    {
        var tokens = PathToken().Matches(d).Select(m => m.Value).ToList();
        var rings = new List<List<(double X, double Y)>>();
        List<(double X, double Y)>? ring = null;
        double x = 0, y = 0, startX = 0, startY = 0;
        var i = 0;
        var command = 'M';
        double Next() => double.Parse(tokens[i++], CultureInfo.InvariantCulture);

        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0]))
            {
                command = tokens[i++][0];
            }

            var relative = char.IsLower(command);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    var (mx, my) = (Next(), Next());
                    (x, y) = relative ? (x + mx, y + my) : (mx, my);
                    ring = [(x, y)];
                    rings.Add(ring);
                    (startX, startY) = (x, y);
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    var (lx, ly) = (Next(), Next());
                    (x, y) = relative ? (x + lx, y + ly) : (lx, ly);
                    ring!.Add((x, y));
                    break;
                case 'H':
                    var hx = Next();
                    x = relative ? x + hx : hx;
                    ring!.Add((x, y));
                    break;
                case 'V':
                    var vy = Next();
                    y = relative ? y + vy : vy;
                    ring!.Add((x, y));
                    break;
                case 'A':
                    double rx = Next(), ry = Next();
                    Next(); // x-axis rotation: always 0 in these outlines
                    var largeArc = Next() != 0;
                    var sweep = Next() != 0;
                    var (ax, ay) = (Next(), Next());
                    var (ex, ey) = relative ? (x + ax, y + ay) : (ax, ay);
                    ring!.AddRange(Arc(x, y, rx, ry, largeArc, sweep, ex, ey));
                    (x, y) = (ex, ey);
                    break;
                case 'Z':
                    (x, y) = (startX, startY);
                    break;
                default:
                    throw new NotSupportedException($"Path command {command}");
            }
        }

        return rings;
    }

    /// <summary>SVG endpoint arc → sampled points (rotation 0), per the SVG spec's centre parameterisation.</summary>
    private static IEnumerable<(double X, double Y)> Arc(
        double x1, double y1, double rx, double ry, bool largeArc, bool sweep, double x2, double y2)
    {
        if (rx == 0 || ry == 0)
        {
            yield return (x2, y2);
            yield break;
        }

        double dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
        var lambda = dx * dx / (rx * rx) + dy * dy / (ry * ry);
        if (lambda > 1)
        {
            rx *= Math.Sqrt(lambda);
            ry *= Math.Sqrt(lambda);
        }

        var num = rx * rx * ry * ry - rx * rx * dy * dy - ry * ry * dx * dx;
        var den = rx * rx * dy * dy + ry * ry * dx * dx;
        var coefficient = Math.Sqrt(Math.Max(0, num / den)) * (largeArc == sweep ? -1 : 1);
        double cxp = coefficient * rx * dy / ry, cyp = -coefficient * ry * dx / rx;
        double cx = cxp + (x1 + x2) / 2, cy = cyp + (y1 + y2) / 2;
        var start = Math.Atan2((dy - cyp) / ry, (dx - cxp) / rx);
        var sweepAngle = Math.Atan2((-dy - cyp) / ry, (-dx - cxp) / rx) - start;
        if (sweep && sweepAngle < 0)
        {
            sweepAngle += 2 * Math.PI;
        }
        else if (!sweep && sweepAngle > 0)
        {
            sweepAngle -= 2 * Math.PI;
        }

        const int steps = 12;
        for (var s = 1; s <= steps; s++)
        {
            var angle = start + sweepAngle * s / steps;
            yield return (cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle));
        }
    }

    [GeneratedRegex("viewBox=\"([^\"]+)\"")]
    private static partial Regex ViewBox();

    [GeneratedRegex(" d=\"([^\"]+)\"")]
    private static partial Regex PathData();

    [GeneratedRegex("<rect[^>]*/>")]
    private static partial Regex RectElement();

    [GeneratedRegex("(\\w+)=\"([-0-9.]+)\"")]
    private static partial Regex Attribute();

    [GeneratedRegex("[A-Za-z]|-?(?:\\d+\\.?\\d*|\\.\\d+)(?:e-?\\d+)?")]
    private static partial Regex PathToken();
}
