using System.Globalization;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Editing;

/// <summary>Where a dimension's lines, arrows and label go, in sketch coordinates.</summary>
public sealed record DimensionLayout(
    Vec2 Label,
    IReadOnlyList<(Vec2 A, Vec2 B)> ExtensionLines,
    IReadOnlyList<(Vec2 A, Vec2 B)> DimensionLines,
    IReadOnlyList<(Vec2 Tip, Vec2 Direction)> Arrows,
    PreviewArc? Arc,
    string Text);

public static class DimensionGeometry
{
    /// <summary>The point a dimension's <see cref="DimensionConstraint.LabelOffset"/> is measured from.</summary>
    public static Vec2 Anchor(DimensionConstraint dim) => dim switch
    {
        DistanceConstraint d => Vec2.Lerp(d.A.Position, d.B.Position, 0.5),
        AxisDistanceConstraint d => Vec2.Lerp(d.A.Position, d.B.Position, 0.5),
        PointLineDistanceConstraint d => Vec2.Lerp(d.Point.Position, Foot(d.Point.Position, d.Line), 0.5),
        AngleConstraint d => Vertex(d.A, d.B),
        RadialDimensionConstraint d => ((IRadial)d.Circle).Center.Position,
        _ => Vec2.Zero,
    };

    public static string FormatValue(DimensionConstraint dim)
    {
        var v = dim.Value;
        var text = dim switch
        {
            AngleConstraint => (v * 180 / Math.PI).ToString("0.##", CultureInfo.InvariantCulture) + "°",
            RadialDimensionConstraint { IsDiameter: true } => "⌀" + v.ToString("0.###", CultureInfo.InvariantCulture),
            RadialDimensionConstraint => "R" + v.ToString("0.###", CultureInfo.InvariantCulture),
            _ => v.ToString("0.###", CultureInfo.InvariantCulture),
        };
        return dim.IsDriving ? text : $"({text})";
    }

    public static DimensionLayout Layout(DimensionConstraint dim)
    {
        var label = Anchor(dim) + dim.LabelOffset;
        var text = FormatValue(dim);
        switch (dim)
        {
            case DistanceConstraint d:
                return Aligned(d.A.Position, d.B.Position, label, text);
            case PointLineDistanceConstraint d:
                return Aligned(d.Point.Position, Foot(d.Point.Position, d.Line), label, text);
            case AxisDistanceConstraint d:
            {
                var a = d.A.Position;
                var b = d.B.Position;
                Vec2 a2, b2;
                if (d.IsVertical)
                {
                    a2 = new Vec2(label.X, a.Y);
                    b2 = new Vec2(label.X, b.Y);
                }
                else
                {
                    a2 = new Vec2(a.X, label.Y);
                    b2 = new Vec2(b.X, label.Y);
                }

                return Linear(a, b, a2, b2, label, text);
            }

            case AngleConstraint d:
            {
                var vertex = Vertex(d.A, d.B);
                var radius = Math.Max(dim.LabelOffset.Length, 1e-6);
                var start = d.A.Direction.Angle;
                var sweep = d.Sign * d.Measure();
                if (sweep < 0)
                {
                    start += sweep;
                    sweep = -sweep;
                }

                var arc = new PreviewArc(vertex, radius, start, sweep);
                return new DimensionLayout(label, [], [], [], arc, text);
            }

            case RadialDimensionConstraint d:
            {
                var r = (IRadial)d.Circle;
                var c = r.Center.Position;
                var dir = (label - c).Normalized();
                if (dir == Vec2.Zero)
                {
                    dir = Vec2.UnitX;
                }

                var rim = c + dir * r.Radius;
                var from = d.IsDiameter ? c - dir * r.Radius : c;
                List<(Vec2, Vec2)> lines = [(from, rim)];
                if (Vec2.Distance(label, c) > r.Radius)
                {
                    lines.Add((rim, label));
                }

                List<(Vec2, Vec2)> arrows = [(rim, dir)];
                if (d.IsDiameter)
                {
                    arrows.Add((from, -dir));
                }

                return new DimensionLayout(label, [], lines, arrows, null, text);
            }

            default:
                return new DimensionLayout(label, [], [], [], null, text);
        }
    }

    private static DimensionLayout Aligned(Vec2 a, Vec2 b, Vec2 label, string text)
    {
        var u = (b - a).Normalized();
        if (u == Vec2.Zero)
        {
            u = Vec2.UnitX;
        }

        var n = u.Perp();
        var offset = Vec2.Dot(label - Vec2.Lerp(a, b, 0.5), n);
        return Linear(a, b, a + n * offset, b + n * offset, label, text);
    }

    private static DimensionLayout Linear(Vec2 a, Vec2 b, Vec2 a2, Vec2 b2, Vec2 label, string text)
    {
        var dir = (b2 - a2).Normalized();
        return new DimensionLayout(
            label,
            [(a, a2), (b, b2)],
            [(a2, b2)],
            [(a2, -dir), (b2, dir)],
            null,
            text);
    }

    public static Vec2 Foot(Vec2 p, SketchLine line) =>
        Vec2.Lerp(line.Start.Position, line.End.Position, line.Project(p));

    /// <summary>Where two lines (extended) cross, or the midpoint between them if they are parallel.</summary>
    public static Vec2 Vertex(SketchLine a, SketchLine b)
    {
        var d1 = a.Direction;
        var d2 = b.Direction;
        var den = Vec2.Cross(d1, d2);
        if (Math.Abs(den) < 1e-12 * Math.Max(1, d1.LengthSquared * d2.LengthSquared))
        {
            return Vec2.Lerp(a.Midpoint, b.Midpoint, 0.5);
        }

        var t = Vec2.Cross(b.Start.Position - a.Start.Position, d2) / den;
        return a.Start.Position + d1 * t;
    }
}
