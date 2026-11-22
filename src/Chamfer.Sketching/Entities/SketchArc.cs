using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Entities;

/// <summary>
/// A circular arc running counter-clockwise from <see cref="Start"/> to <see cref="End"/> around <see cref="Center"/>.
/// The radius is the distance from the center to the start point; a built-in equation keeps the end point
/// at the same distance.
/// </summary>
public sealed class SketchArc : SketchEntity, IRadial, IEquationSource
{
    public SketchArc(Vec2 center, Vec2 start, Vec2 end)
    {
        Center = new SketchPoint(center) { Owner = this };
        Start = new SketchPoint(start) { Owner = this };
        End = new SketchPoint(end) { Owner = this };
    }

    /// <summary>
    /// Creates the arc through three points: it starts at <paramref name="a"/>, passes through
    /// <paramref name="through"/> and ends at <paramref name="b"/>. Returns null if the points are collinear.
    /// </summary>
    public static SketchArc? ThroughThreePoints(Vec2 a, Vec2 through, Vec2 b)
    {
        if (CircleThroughThreePoints(a, through, b) is not { } center)
        {
            return null;
        }

        // Counter-clockwise from a to b must pass through 'through'; otherwise swap ends.
        var ccw = Vec2.Cross(through - a, b - a) > 0;
        return ccw ? new SketchArc(center, a, b) : new SketchArc(center, b, a);
    }

    public static Vec2? CircleThroughThreePoints(Vec2 a, Vec2 b, Vec2 c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        var scale = Math.Max(1e-12, (b - a).LengthSquared + (c - a).LengthSquared);
        if (Math.Abs(d) < 1e-9 * scale)
        {
            return null;
        }

        var a2 = a.LengthSquared;
        var b2 = b.LengthSquared;
        var c2 = c.LengthSquared;
        return new Vec2(
            (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
    }

    public override EntityKind Kind => EntityKind.Arc;

    public SketchPoint Center { get; }
    public SketchPoint Start { get; }
    public SketchPoint End { get; }

    public double Radius => Vec2.Distance(Start.Position, Center.Position);

    public double StartAngle => (Start.Position - Center.Position).Angle;
    public double EndAngle => (End.Position - Center.Position).Angle;

    /// <summary>Counter-clockwise sweep from start to end, in (0, 2π].</summary>
    public double Sweep
    {
        get
        {
            var sweep = EndAngle - StartAngle;
            while (sweep <= 1e-12)
            {
                sweep += 2 * Math.PI;
            }

            while (sweep > 2 * Math.PI)
            {
                sweep -= 2 * Math.PI;
            }

            return sweep;
        }
    }

    public Vec2 Midpoint => Center.Position + Vec2.FromAngle(StartAngle + Sweep / 2, Radius);

    public override IReadOnlyList<SketchPoint> Points => [Center, Start, End];

    public override IEnumerable<Param> Params =>
        [Center.XParam, Center.YParam, Start.XParam, Start.YParam, End.XParam, End.YParam];

    /// <summary>True if the direction at <paramref name="angle"/> lies within the arc's sweep.</summary>
    public bool ContainsAngle(double angle)
    {
        var rel = angle - StartAngle;
        while (rel < 0)
        {
            rel += 2 * Math.PI;
        }

        while (rel >= 2 * Math.PI)
        {
            rel -= 2 * Math.PI;
        }

        return rel <= Sweep + 1e-12;
    }

    public override Vec2 ClosestPoint(Vec2 p)
    {
        var angle = (p - Center.Position).Angle;
        if (ContainsAngle(angle))
        {
            return Center.Position + Vec2.FromAngle(angle, Radius);
        }

        return Vec2.Distance(p, Start.Position) <= Vec2.Distance(p, End.Position) ? Start.Position : End.Position;
    }

    public override double DistanceTo(Vec2 p) => Vec2.Distance(ClosestPoint(p), p);

    public void PartialRadius(IEquationSink sink, double scale)
    {
        var u = Start.Position - Center.Position;
        var g = SafeUnit(u) * scale;
        sink.Partial(Start, g);
        sink.Partial(Center, -g);
    }

    // Built-in equation: |End - Center| - |Start - Center| = 0.
    int IEquationSource.EquationCount => 1;

    void IEquationSource.Evaluate(IEquationSink sink)
    {
        var us = Start.Position - Center.Position;
        var ue = End.Position - Center.Position;
        sink.Equation(ue.Length - us.Length);
        var ge = SafeUnit(ue);
        var gs = SafeUnit(us);
        sink.Partial(End, ge);
        sink.Partial(Start, -gs);
        sink.Partial(Center, gs - ge);
    }

    private static Vec2 SafeUnit(Vec2 v)
    {
        var n = v.Normalized();
        return n == Vec2.Zero ? Vec2.UnitX : n;
    }
}
