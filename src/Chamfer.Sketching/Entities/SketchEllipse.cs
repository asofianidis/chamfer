using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Entities;

/// <summary>A full ellipse: a centre, the end of its major semi-axis (size and direction), and a minor semi-axis.</summary>
public sealed class SketchEllipse : SketchEntity
{
    public SketchEllipse(Vec2 center, Vec2 majorEnd, double minorRadius)
    {
        Center = new SketchPoint(center) { Owner = this };
        MajorEnd = new SketchPoint(majorEnd) { Owner = this };
        MinorParam = new Param(minorRadius);
    }

    public override EntityKind Kind => EntityKind.Ellipse;

    public SketchPoint Center { get; }

    /// <summary>End of the major semi-axis.</summary>
    public SketchPoint MajorEnd { get; }

    public Param MinorParam { get; }

    public double MajorRadius => Vec2.Distance(Center.Position, MajorEnd.Position);

    public double MinorRadius => Math.Abs(MinorParam.Value);

    /// <summary>Direction of the major axis, radians.</summary>
    public double Angle => (MajorEnd.Position - Center.Position).Angle;

    public override IReadOnlyList<SketchPoint> Points => [Center, MajorEnd];

    public override IEnumerable<Param> Params => [Center.XParam, Center.YParam, MajorEnd.XParam, MajorEnd.YParam, MinorParam];

    /// <summary>The point at parameter <paramref name="t"/> (radians around the ellipse).</summary>
    public Vec2 PointAt(double t)
    {
        var (c, s) = (Math.Cos(Angle), Math.Sin(Angle));
        var x = MajorRadius * Math.Cos(t);
        var y = MinorRadius * Math.Sin(t);
        return Center.Position + new Vec2((x * c) - (y * s), (x * s) + (y * c));
    }

    /// <summary>Points around the ellipse (closed: the first point isn't repeated).</summary>
    public IEnumerable<Vec2> Sample(int count = 72) => Enumerable.Range(0, count).Select(i => PointAt(2 * Math.PI * i / count));

    public override Vec2 ClosestPoint(Vec2 p)
    {
        // Coarse search, then refine around the best parameter.
        var best = 0.0;
        var bestD = double.MaxValue;
        for (var i = 0; i < 144; i++)
        {
            var t = 2 * Math.PI * i / 144;
            var d = Vec2.Distance(PointAt(t), p);
            if (d < bestD)
            {
                (best, bestD) = (t, d);
            }
        }

        var step = 2 * Math.PI / 144;
        for (var k = 0; k < 30; k++)
        {
            foreach (var t in new[] { best - step, best + step })
            {
                var d = Vec2.Distance(PointAt(t), p);
                if (d < bestD)
                {
                    (best, bestD) = (t, d);
                }
            }

            step /= 2;
        }

        return PointAt(best);
    }

    public override double DistanceTo(Vec2 p) => Vec2.Distance(ClosestPoint(p), p);
}
