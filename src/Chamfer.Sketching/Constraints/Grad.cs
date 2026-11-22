using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Constraints;

/// <summary>Residual functions shared by several constraints, together with their exact gradients.</summary>
internal static class Grad
{
    private const double Tiny = 1e-12;

    public static Vec2 SafeUnit(Vec2 v)
    {
        var n = v.Normalized();
        return n == Vec2.Zero ? Vec2.UnitX : n;
    }

    /// <summary>sin of the angle from u to v: cross(u, v) / (|u||v|).</summary>
    public static double NormalizedCross(Vec2 u, Vec2 v, out Vec2 gu, out Vec2 gv)
    {
        var lu2 = Math.Max(u.LengthSquared, Tiny);
        var lv2 = Math.Max(v.LengthSquared, Tiny);
        var n = Math.Sqrt(lu2 * lv2);
        var f = Vec2.Cross(u, v) / n;
        gu = new Vec2(v.Y, -v.X) / n - u * (f / lu2);
        gv = new Vec2(-u.Y, u.X) / n - v * (f / lv2);
        return f;
    }

    /// <summary>cos of the angle from u to v: dot(u, v) / (|u||v|).</summary>
    public static double NormalizedDot(Vec2 u, Vec2 v, out Vec2 gu, out Vec2 gv)
    {
        var lu2 = Math.Max(u.LengthSquared, Tiny);
        var lv2 = Math.Max(v.LengthSquared, Tiny);
        var n = Math.Sqrt(lu2 * lv2);
        var f = Vec2.Dot(u, v) / n;
        gu = v / n - u * (f / lu2);
        gv = u / n - v * (f / lv2);
        return f;
    }

    /// <summary>
    /// Signed distance from <paramref name="p"/> to the infinite line through <paramref name="line"/>
    /// (positive on the left of Start→End). Writes the gradient into the current equation, scaled.
    /// </summary>
    public static double SignedDistance(SketchPoint p, SketchLine line, IEquationSink? sink, double scale = 1)
    {
        var d = line.Direction;
        var w = p.Position - line.Start.Position;
        var len2 = Math.Max(d.LengthSquared, Tiny);
        var len = Math.Sqrt(len2);
        var f = Vec2.Cross(d, w) / len;
        if (sink is not null)
        {
            var gd = new Vec2(w.Y, -w.X) / len - d * (f / len2);
            var gw = new Vec2(-d.Y, d.X) / len;
            sink.Partial(line.End, gd * scale);
            sink.Partial(p, gw * scale);
            sink.Partial(line.Start, (-gd - gw) * scale);
        }

        return f;
    }

    /// <summary>|q - p| with its gradient written to the current equation, scaled.</summary>
    public static double Distance(SketchPoint p, SketchPoint q, IEquationSink? sink, double scale = 1)
    {
        var u = q.Position - p.Position;
        if (sink is not null)
        {
            var g = SafeUnit(u) * scale;
            sink.Partial(q, g);
            sink.Partial(p, -g);
        }

        return u.Length;
    }

    /// <summary>Signed angle from line a to line b, in (-π, π].</summary>
    public static double Angle(SketchLine a, SketchLine b, IEquationSink? sink, double scale = 1)
    {
        var d1 = a.Direction;
        var d2 = b.Direction;
        var c = Vec2.Cross(d1, d2);
        var k = Vec2.Dot(d1, d2);
        if (sink is not null)
        {
            // ∂atan2(c, k) = (k ∂c - c ∂k) / (c² + k²)
            var den = Math.Max(c * c + k * k, Tiny);
            var g1 = (new Vec2(d2.Y, -d2.X) * k - d2 * c) / den;
            var g2 = (new Vec2(-d1.Y, d1.X) * k - d1 * c) / den;
            sink.PartialDirection(a, g1 * scale);
            sink.PartialDirection(b, g2 * scale);
        }

        return Math.Atan2(c, k);
    }

    /// <summary>Wraps an angle to (-π, π].</summary>
    public static double WrapAngle(double a)
    {
        a %= 2 * Math.PI;
        if (a <= -Math.PI)
        {
            a += 2 * Math.PI;
        }
        else if (a > Math.PI)
        {
            a -= 2 * Math.PI;
        }

        return a;
    }

    public static double SignOf(double v) => v < 0 ? -1 : 1;
}
