using Chamfer.Geometry;

namespace Chamfer.Drawing;

/// <summary>Line styles, following common drafting practice.</summary>
public enum Pen
{
    /// <summary>Visible outlines (thick, continuous).</summary>
    Visible,

    /// <summary>Tangent edges (thin, continuous).</summary>
    Tangent,

    /// <summary>Hidden edges (thin, dashed).</summary>
    Hidden,

    /// <summary>Centre lines (thin, chain).</summary>
    Center,

    /// <summary>Dimensions, leaders, hatching (thin).</summary>
    Thin,

    /// <summary>Sheet border and title block.</summary>
    Frame,

    /// <summary>Cutting planes and detail circles (thick chain).</summary>
    Cut,
}

public enum TextAnchor
{
    Left,
    Center,
    Right,
}

/// <summary>A drawing primitive in sheet millimetres (origin at the sheet's lower-left corner, Y up).</summary>
public abstract record Primitive(Pen Pen);

public sealed record PolylinePrimitive(Pen Pen, IReadOnlyList<Point2> Points, bool Closed = false) : Primitive(Pen);

public sealed record TextPrimitive(Point2 At, string Text, double Height, TextAnchor Anchor = TextAnchor.Left, double Angle = 0) : Primitive(Pen.Thin);

/// <summary>A filled arrowhead triangle.</summary>
public sealed record ArrowPrimitive(Point2 Tip, Point2 Direction, double Length) : Primitive(Pen.Thin);

/// <summary>Everything to draw for one sheet, in sheet coordinates.</summary>
public sealed class SheetGraphics(double width, double height)
{
    public double Width { get; } = width;
    public double Height { get; } = height;
    public List<Primitive> Items { get; } = [];

    /// <summary>Which view (if any) each primitive belongs to, for picking.</summary>
    public Dictionary<Primitive, string> Owners { get; } = new(ReferenceEqualityComparer.Instance);

    public void Line(Pen pen, Point2 a, Point2 b, string? owner = null) => Add(new PolylinePrimitive(pen, [a, b]), owner);

    public void Polyline(Pen pen, IReadOnlyList<Point2> points, bool closed = false, string? owner = null) =>
        Add(new PolylinePrimitive(pen, points, closed), owner);

    public void Text(Point2 at, string text, double height, TextAnchor anchor = TextAnchor.Left, double angle = 0, string? owner = null) =>
        Add(new TextPrimitive(at, text, height, anchor, angle), owner);

    public void Arrow(Point2 tip, Point2 direction, double length = 3, string? owner = null) =>
        Add(new ArrowPrimitive(tip, direction.Normalized(), length), owner);

    public void Circle(Pen pen, Point2 center, double r, string? owner = null, int segments = 72) =>
        Polyline(pen, [.. Enumerable.Range(0, segments).Select(i => center + new Point2(Math.Cos(2 * Math.PI * i / segments), Math.Sin(2 * Math.PI * i / segments)) * r)], closed: true, owner);

    private void Add(Primitive p, string? owner)
    {
        Items.Add(p);
        if (owner is not null)
        {
            Owners[p] = owner;
        }
    }

    /// <summary>Line width in mm for a pen.</summary>
    public static double PenWidth(Pen pen) => pen switch
    {
        Pen.Visible => 0.5,
        Pen.Frame => 0.7,
        Pen.Cut => 0.6,
        _ => 0.25,
    };

    /// <summary>Dash pattern in mm (empty = continuous).</summary>
    public static double[] Dashes(Pen pen) => pen switch
    {
        Pen.Hidden => [3, 1.5],
        Pen.Center or Pen.Cut => [8, 1.5, 1.5, 1.5],
        _ => [],
    };
}
