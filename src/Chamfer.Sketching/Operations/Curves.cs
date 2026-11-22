using Chamfer.Sketching.Entities;

namespace Chamfer.Sketching.Operations;

/// <summary>
/// Parametric views of lines, arcs and circles, and their intersections.
/// Lines are parameterised 0..1 from start to end; arcs by angle 0..sweep
/// counter-clockwise from the start; circles by angle 0..2π from +X.
/// </summary>
public static class Curves
{
    private const double Eps = 1e-9;

    public static bool IsCurve(SketchEntity e) => e is SketchLine or SketchArc or SketchCircle;

    /// <summary>Parameter of the point on <paramref name="e"/> closest to <paramref name="p"/>.</summary>
    public static double ParamOf(SketchEntity e, Vec2 p) => e switch
    {
        SketchLine l => l.Length < Eps ? 0 : Vec2.Dot(p - l.Start.Position, l.Direction) / l.Direction.LengthSquared,
        SketchArc a => Wrap((p - a.Center.Position).Angle - a.StartAngle),
        SketchCircle c => Wrap((p - c.Center.Position).Angle),
        _ => 0,
    };

    public static Vec2 PointAt(SketchEntity e, double t) => e switch
    {
        SketchLine l => Vec2.Lerp(l.Start.Position, l.End.Position, t),
        SketchArc a => a.Center.Position + Vec2.FromAngle(a.StartAngle + t, a.Radius),
        SketchCircle c => c.Center.Position + Vec2.FromAngle(t, c.Radius),
        _ => throw new ArgumentException("Not a curve.", nameof(e)),
    };

    /// <summary>Parameter range of a bounded curve.</summary>
    public static (double Lo, double Hi) Range(SketchEntity e) => e switch
    {
        SketchLine => (0, 1),
        SketchArc a => (0, a.Sweep),
        _ => (0, 2 * Math.PI),
    };

    public static double Wrap(double angle)
    {
        var a = angle % (2 * Math.PI);
        return a < 0 ? a + (2 * Math.PI) : a;
    }

    /// <summary>Is the point (known to be on the carrier) within the curve's extent?</summary>
    public static bool Within(SketchEntity e, Vec2 p, double tol = 1e-7)
    {
        switch (e)
        {
            case SketchLine l:
                var t = ParamOf(l, p);
                var slack = tol / Math.Max(l.Length, Eps);
                return t >= -slack && t <= 1 + slack;
            case SketchArc a:
                var phi = ParamOf(a, p);
                var angTol = tol / Math.Max(a.Radius, Eps);
                return phi <= a.Sweep + angTol || phi >= (2 * Math.PI) - angTol;
            default:
                return true;
        }
    }

    /// <summary>Intersections of the carriers (infinite line, full circle) of two curves.</summary>
    public static IEnumerable<Vec2> CarrierIntersections(SketchEntity a, SketchEntity b)
    {
        switch (a, b)
        {
            case (SketchLine l1, SketchLine l2):
                var d1 = l1.Direction;
                var d2 = l2.Direction;
                var den = Vec2.Cross(d1, d2);
                if (Math.Abs(den) < Eps * d1.Length * d2.Length)
                {
                    yield break;
                }

                var t = Vec2.Cross(l2.Start.Position - l1.Start.Position, d2) / den;
                yield return l1.Start.Position + (d1 * t);
                break;
            case (SketchLine l, _) when b is IRadial r:
                foreach (var p in LineCircle(l.Start.Position, l.Direction, r.Center.Position, r.Radius))
                {
                    yield return p;
                }

                break;
            case (_, SketchLine l) when a is IRadial r:
                foreach (var p in LineCircle(l.Start.Position, l.Direction, r.Center.Position, r.Radius))
                {
                    yield return p;
                }

                break;
            case (IRadial r1, IRadial r2):
                foreach (var p in CircleCircle(r1.Center.Position, r1.Radius, r2.Center.Position, r2.Radius))
                {
                    yield return p;
                }

                break;
        }
    }

    /// <summary>Points where two bounded curves cross.</summary>
    public static IEnumerable<Vec2> Intersections(SketchEntity a, SketchEntity b, double tol = 1e-7) =>
        CarrierIntersections(a, b).Where(p => Within(a, p, tol) && Within(b, p, tol));

    private static IEnumerable<Vec2> LineCircle(Vec2 o, Vec2 d, Vec2 c, double r)
    {
        var f = o - c;
        var aa = Vec2.Dot(d, d);
        var bb = 2 * Vec2.Dot(f, d);
        var cc = Vec2.Dot(f, f) - (r * r);
        var disc = (bb * bb) - (4 * aa * cc);
        if (aa < Eps || disc < -Eps * aa * r * r)
        {
            yield break;
        }

        disc = Math.Sqrt(Math.Max(0, disc));
        var t1 = (-bb - disc) / (2 * aa);
        var t2 = (-bb + disc) / (2 * aa);
        yield return o + (d * t1);
        if (disc > 1e-12)
        {
            yield return o + (d * t2);
        }
    }

    private static IEnumerable<Vec2> CircleCircle(Vec2 c1, double r1, Vec2 c2, double r2)
    {
        var d = Vec2.Distance(c1, c2);
        if (d < Eps || d > r1 + r2 + Eps || d < Math.Abs(r1 - r2) - Eps)
        {
            yield break;
        }

        var a = ((r1 * r1) - (r2 * r2) + (d * d)) / (2 * d);
        var h = Math.Sqrt(Math.Max(0, (r1 * r1) - (a * a)));
        var u = (c2 - c1) / d;
        var mid = c1 + (u * a);
        yield return mid + (u.Perp() * h);
        if (h > 1e-12)
        {
            yield return mid - (u.Perp() * h);
        }
    }
}
