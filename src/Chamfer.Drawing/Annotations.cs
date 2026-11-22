using Chamfer.Geometry;

namespace Chamfer.Drawing;

/// <summary>Geometric characteristics (ISO 1101 / ASME Y14.5).</summary>
public enum GeometricCharacteristic
{
    Straightness,
    Flatness,
    Circularity,
    Cylindricity,
    ProfileOfLine,
    ProfileOfSurface,
    Perpendicularity,
    Angularity,
    Parallelism,
    Position,
    Concentricity,
    Symmetry,
    CircularRunout,
    TotalRunout,
}

public enum MaterialCondition
{
    None,

    /// <summary>Maximum material condition (Ⓜ).</summary>
    Maximum,

    /// <summary>Least material condition (Ⓛ).</summary>
    Least,
}

/// <summary>A feature control frame with an optional leader.</summary>
public sealed class GeometricTolerance
{
    public string Id { get; set; } = string.Empty;

    public GeometricCharacteristic Characteristic { get; set; } = GeometricCharacteristic.Position;

    /// <summary>Tolerance value as written, e.g. "0.05".</summary>
    public string Tolerance { get; set; } = "0.1";

    /// <summary>Prefix the tolerance with Ø (cylindrical zone).</summary>
    public bool Diameter { get; set; }

    public MaterialCondition Condition { get; set; }

    /// <summary>Datum references in order of precedence: primary, secondary, tertiary.</summary>
    public List<string> Datums { get; set; } = [];

    /// <summary>Lower-left corner of the frame on the sheet (mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Where the leader arrow points (sheet mm); none when null.</summary>
    public double? LeaderX { get; set; }

    public double? LeaderY { get; set; }
}

/// <summary>A datum feature symbol: a boxed letter on a leader ending in a filled triangle.</summary>
public sealed class DatumFeature
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = "A";

    /// <summary>Centre of the letter box (sheet mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Where the triangle sits on the feature (sheet mm).</summary>
    public double AnchorX { get; set; }

    public double AnchorY { get; set; }
}

/// <summary>A surface texture symbol (ISO 1302) with roughness.</summary>
public sealed class SurfaceFinish
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Roughness requirement, e.g. "Ra 1.6".</summary>
    public string Roughness { get; set; } = "Ra 3.2";

    /// <summary>Material removal required (closed symbol bar).</summary>
    public bool MachiningRequired { get; set; } = true;

    /// <summary>Tip of the symbol, on the surface (sheet mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>Draws annotation symbols from lines and circles so they look the same in every export format.</summary>
public static class AnnotationGraphics
{
    /// <summary>Height of a feature control frame row (sheet mm).</summary>
    public const double FrameHeight = 7;

    private const double TextHeight = 3.5;

    private static double TextWidth(string s) => s.Length * TextHeight * 0.62;

    /// <summary>The frame's cells (x positions of the dividing lines) and overall width.</summary>
    public static (double[] Cells, double Width) Layout(GeometricTolerance t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var cells = new List<double> { 0, FrameHeight };
        var tolWidth = TextWidth(t.Tolerance) + 3 + (t.Diameter ? FrameHeight * 0.6 : 0) + (t.Condition != MaterialCondition.None ? FrameHeight * 0.7 : 0);
        cells.Add(cells[^1] + Math.Max(FrameHeight * 1.5, tolWidth));
        foreach (var d in t.Datums.Where(d => d.Length > 0))
        {
            cells.Add(cells[^1] + Math.Max(FrameHeight, TextWidth(d) + 3));
        }

        return ([.. cells], cells[^1]);
    }

    public static void Draw(SheetGraphics g, GeometricTolerance t)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(t);
        var h = FrameHeight;
        var (cells, width) = Layout(t);
        var o = new Point2(t.X, t.Y);
        g.Polyline(Pen.Thin, [o, o + new Point2(width, 0), o + new Point2(width, h), o + new Point2(0, h)], closed: true, owner: t.Id);
        foreach (var x in cells.Skip(1).SkipLast(1))
        {
            g.Line(Pen.Thin, o + new Point2(x, 0), o + new Point2(x, h), t.Id);
        }

        Symbol(g, t.Characteristic, o + new Point2(h / 2, h / 2), h * 0.6, t.Id);

        // Tolerance cell: [Ø] value [Ⓜ/Ⓛ]
        var x0 = o.X + cells[1] + 1.5;
        var mid = o.Y + (h / 2);
        if (t.Diameter)
        {
            var c = new Point2(x0 + (h * 0.25), mid);
            g.Circle(Pen.Thin, c, h * 0.2, t.Id, 24);
            g.Line(Pen.Thin, c + new Point2(-h * 0.28, -h * 0.28), c + new Point2(h * 0.28, h * 0.28), t.Id);
            x0 += h * 0.6;
        }

        g.Text(new Point2(x0, mid - (TextHeight / 2)), t.Tolerance, TextHeight, owner: t.Id);
        x0 += TextWidth(t.Tolerance) + 0.5;
        if (t.Condition != MaterialCondition.None)
        {
            var c = new Point2(x0 + (h * 0.3), mid);
            g.Circle(Pen.Thin, c, h * 0.27, t.Id, 24);
            g.Text(c - new Point2(0, TextHeight * 0.35), t.Condition == MaterialCondition.Maximum ? "M" : "L", TextHeight * 0.7, TextAnchor.Center, owner: t.Id);
        }

        var datums = t.Datums.Where(d => d.Length > 0).ToList();
        for (var i = 0; i < datums.Count; i++)
        {
            var cx = o.X + ((cells[i + 2] + cells[i + 3]) / 2);
            g.Text(new Point2(cx, mid - (TextHeight / 2)), datums[i], TextHeight, TextAnchor.Center, owner: t.Id);
        }

        if (t.LeaderX is { } lx && t.LeaderY is { } ly)
        {
            var tip = new Point2(lx, ly);
            var start = lx < o.X ? new Point2(o.X, mid) : lx > o.X + width ? new Point2(o.X + width, mid) : new Point2(o.X + (h / 2), ly < o.Y ? o.Y : o.Y + h);
            g.Line(Pen.Thin, start, tip, t.Id);
            g.Arrow(tip, (tip - start).Normalized(), 3, t.Id);
        }
    }

    public static void Draw(SheetGraphics g, DatumFeature d)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(d);
        var h = FrameHeight;
        var c = new Point2(d.X, d.Y);
        var box = new[] { c + new Point2(-h / 2, -h / 2), c + new Point2(h / 2, -h / 2), c + new Point2(h / 2, h / 2), c + new Point2(-h / 2, h / 2) };
        g.Polyline(Pen.Thin, box, closed: true, owner: d.Id);
        g.Text(c - new Point2(0, TextHeight / 2), d.Label, TextHeight, TextAnchor.Center, owner: d.Id);

        // Leader from the box to a filled datum triangle on the feature.
        var anchor = new Point2(d.AnchorX, d.AnchorY);
        var dir = anchor - c;
        if (dir.Length < 1e-9)
        {
            return;
        }

        dir = dir.Normalized();
        var start = c + (dir * (h / 2 / Math.Max(Math.Abs(dir.X), Math.Abs(dir.Y))));
        var baseMid = anchor - (dir * 2.5);
        g.Line(Pen.Thin, start, baseMid, d.Id);
        var side = new Point2(-dir.Y, dir.X) * 1.8;
        for (var k = 0; k <= 6; k++)
        {
            // Hatch the triangle so it reads as filled in vector output.
            var f = k / 6.0;
            g.Line(Pen.Thin, baseMid + (side * (1 - (2 * f))), anchor + (side * 0.02 * (1 - (2 * f))), d.Id);
        }

        g.Polyline(Pen.Thin, [baseMid + side, baseMid - side, anchor], closed: true, owner: d.Id);
    }

    public static void Draw(SheetGraphics g, SurfaceFinish s)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(s);
        var tip = new Point2(s.X, s.Y);
        const double h1 = 5;
        var left = tip + new Point2(-h1 * 0.577, h1);
        var right = tip + new Point2(h1 * 1.155, h1 * 2);
        g.Polyline(Pen.Thin, [left, tip, right], owner: s.Id);
        if (s.MachiningRequired)
        {
            g.Line(Pen.Thin, left, tip + new Point2(h1 * 0.577, h1), s.Id);
        }

        var barEnd = right + new Point2(Math.Max(8, TextWidth(s.Roughness) * 0.8), 0);
        g.Line(Pen.Thin, right, barEnd, s.Id);
        g.Text(new Point2(right.X + 0.5, right.Y - 3.2), s.Roughness, 2.5, owner: s.Id);
    }

    /// <summary>The characteristic symbol in a square of side <paramref name="size"/> centred on <paramref name="c"/>.</summary>
    public static void Symbol(SheetGraphics g, GeometricCharacteristic kind, Point2 c, double size, string? owner = null)
    {
        ArgumentNullException.ThrowIfNull(g);
        var s = size / 2;
        Point2 P(double x, double y) => c + new Point2(x * s, y * s);
        void L(double x1, double y1, double x2, double y2) => g.Line(Pen.Thin, P(x1, y1), P(x2, y2), owner);
        void Arrow(double x, double y)
        {
            L(x - 0.6, y - 0.6, x + 0.6, y + 0.6);
            L(x + 0.6, y + 0.6, x + 0.1, y + 0.6);
            L(x + 0.6, y + 0.6, x + 0.6, y + 0.1);
        }

        switch (kind)
        {
            case GeometricCharacteristic.Straightness:
                L(-1, 0, 1, 0);
                break;
            case GeometricCharacteristic.Flatness:
                g.Polyline(Pen.Thin, [P(-1, -0.5), P(0.5, -0.5), P(1, 0.5), P(-0.5, 0.5)], closed: true, owner: owner);
                break;
            case GeometricCharacteristic.Circularity:
                g.Circle(Pen.Thin, c, s * 0.8, owner, 32);
                break;
            case GeometricCharacteristic.Cylindricity:
                g.Circle(Pen.Thin, c, s * 0.6, owner, 32);
                L(-0.9, -0.9, -0.3, 0.9);
                L(0.3, -0.9, 0.9, 0.9);
                break;
            case GeometricCharacteristic.ProfileOfLine:
            case GeometricCharacteristic.ProfileOfSurface:
                g.Polyline(Pen.Thin, [.. Enumerable.Range(0, 17).Select(i => P(-0.9 * Math.Cos(Math.PI * i / 16), (0.9 * Math.Sin(Math.PI * i / 16)) - 0.45))],
                    closed: kind == GeometricCharacteristic.ProfileOfSurface, owner: owner);
                break;
            case GeometricCharacteristic.Perpendicularity:
                L(-1, -0.8, 1, -0.8);
                L(0, -0.8, 0, 0.9);
                break;
            case GeometricCharacteristic.Angularity:
                L(-1, -0.8, 1, -0.8);
                L(-1, -0.8, 0.8, 0.5);
                break;
            case GeometricCharacteristic.Parallelism:
                L(-0.9, -0.9, -0.1, 0.9);
                L(0.1, -0.9, 0.9, 0.9);
                break;
            case GeometricCharacteristic.Position:
                g.Circle(Pen.Thin, c, s * 0.6, owner, 32);
                L(-1, 0, 1, 0);
                L(0, -1, 0, 1);
                break;
            case GeometricCharacteristic.Concentricity:
                g.Circle(Pen.Thin, c, s * 0.9, owner, 32);
                g.Circle(Pen.Thin, c, s * 0.45, owner, 24);
                break;
            case GeometricCharacteristic.Symmetry:
                L(-1, 0, 1, 0);
                L(-0.6, 0.5, 0.6, 0.5);
                L(-0.6, -0.5, 0.6, -0.5);
                break;
            case GeometricCharacteristic.CircularRunout:
                Arrow(0, 0);
                break;
            case GeometricCharacteristic.TotalRunout:
                Arrow(-0.4, 0);
                Arrow(0.4, 0);
                L(-1, -0.6, 0.2, -0.6);
                break;
        }
    }

    /// <summary>Short text form used in summaries, e.g. "⌖ Ø0.1 Ⓜ | A | B".</summary>
    public static string Describe(GeometricTolerance t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var value = (t.Diameter ? "Ø" : string.Empty) + t.Tolerance + t.Condition switch
        {
            MaterialCondition.Maximum => " (M)",
            MaterialCondition.Least => " (L)",
            _ => string.Empty,
        };
        return string.Join(" | ", new[] { t.Characteristic.ToString(), value }.Concat(t.Datums.Where(d => d.Length > 0)));
    }
}
