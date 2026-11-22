using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.Document;

/// <summary>Projects model edges onto a sketch plane as fixed sketch geometry (Convert Entities).</summary>
public static class EdgeProjection
{
    private const double Tol = 1e-6;

    public static IReadOnlyList<SketchEntity> Project(Sketch sketch, Body body, int edge, Plane3 plane)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(body);
        var info = body.Edge(edge);
        var made = new List<SketchEntity>();
        Vec2 Local(Vec3 p)
        {
            var (u, v) = plane.ToLocal(p);
            return new Vec2(u, v);
        }

        switch (info.Curve)
        {
            case CurveKind.Line:
                var a = Local(info.Start);
                var b = Local(info.End);
                if (Vec2.Distance(a, b) > Tol)
                {
                    made.Add(sketch.AddLine(a, b));
                }

                break;

            case CurveKind.Circle when CircleNormal(body, edge, info) is { } n && Math.Abs(Math.Abs(Vec3.Dot(n, plane.Normal)) - 1) < 1e-6:
                var c = Local(info.CenterOrDirection);
                if (info.IsClosed)
                {
                    made.Add(sketch.AddCircle(c, info.Radius));
                    break;
                }

                var s = Local(info.Start);
                var m = Local(info.Midpoint);
                var e = Local(info.End);
                var ccw = Vec2.Cross(s - c, m - c) > 0;
                made.Add(ccw ? sketch.AddArc(c, s, e) : sketch.AddArc(c, e, s));
                break;

            default:
                var pts = body.Shape.SampleEdge(edge, 0.02).Select(Local).ToList();
                for (var i = 0; i + 1 < pts.Count; i++)
                {
                    if (Vec2.Distance(pts[i], pts[i + 1]) > Tol)
                    {
                        made.Add(sketch.AddLine(pts[i], pts[i + 1]));
                    }
                }

                break;
        }

        foreach (var entity in made)
        {
            sketch.AddConstraint(new FixConstraint(entity));
        }

        return made;
    }

    private static Vec3? CircleNormal(Body body, int edge, EdgeInfo info)
    {
        var n = Vec3.Cross(info.Start - info.CenterOrDirection, info.Midpoint - info.CenterOrDirection);
        if (n.LengthSquared > 1e-18)
        {
            return n.Normalized();
        }

        // Closed circle: start and end coincide, so use samples.
        var pts = body.Shape.SampleEdge(edge, 0.05);
        if (pts.Count < 3)
        {
            return null;
        }

        var n2 = Vec3.Cross(pts[pts.Count / 3] - info.CenterOrDirection, pts[2 * pts.Count / 3] - info.CenterOrDirection);
        return n2.LengthSquared > 1e-18 ? n2.Normalized() : null;
    }
}
