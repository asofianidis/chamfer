using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document;

/// <summary>A 3D annotation: lines to draw and a text anchored in the model.</summary>
public sealed record PmiAnnotation(IReadOnlyList<(Vec3 A, Vec3 B)> Lines, Vec3 TextAt, string Text);

/// <summary>
/// Model-based definition: overall size dimensions and hole callouts generated from the part and
/// placed in 3D, so the model carries its own manufacturing information.
/// </summary>
public static class Pmi
{
    public static IReadOnlyList<PmiAnnotation> Build(PartDocument doc, LengthUnit unit = LengthUnit.Millimeter)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var solids = doc.Bodies.Where(b => !b.IsSurface).ToList();
        if (solids.Count == 0)
        {
            return [];
        }

        var box = solids.Aggregate(Box3.Empty, (b, x) => b.Union(x.Shape.Bounds));
        var gap = Math.Max(2, box.Diagonal * 0.08);
        var (lo, hi) = (box.Min, box.Max);
        var list = new List<PmiAnnotation>
        {
            // Width along X in front of the part, depth along Y beside it, height along Z at a corner.
            Linear(new Vec3(lo.X, lo.Y, lo.Z), new Vec3(hi.X, lo.Y, lo.Z), new Vec3(0, -gap, 0), unit),
            Linear(new Vec3(hi.X, lo.Y, lo.Z), new Vec3(hi.X, hi.Y, lo.Z), new Vec3(gap, 0, 0), unit),
            Linear(new Vec3(hi.X, lo.Y, lo.Z), new Vec3(hi.X, lo.Y, hi.Z), new Vec3(gap, -gap, 0), unit),
        };

        // Hole callouts: round holes grouped by diameter, with a leader to the first of each size.
        var holes = new List<(double D, Vec3 Top, Vec3 Axis, bool Through)>();
        foreach (var body in solids)
        {
            var seen = new HashSet<(double, double, double, double)>();
            for (var f = 1; f <= body.FaceCount; f++)
            {
                var face = body.Face(f);
                if (face.Surface != SurfaceKind.Cylinder || !IsHole(body, f))
                {
                    continue;
                }

                var axis = face.AxisDirection.Normalized();
                var radius = face.Radius;
                var origin = face.AxisOrigin;
                var key = (Math.Round(origin.X - (axis.X * Vec3.Dot(origin, axis)), 4), Math.Round(origin.Y - (axis.Y * Vec3.Dot(origin, axis)), 4), Math.Round(origin.Z - (axis.Z * Vec3.Dot(origin, axis)), 4), Math.Round(radius, 4));
                if (!seen.Add(key))
                {
                    continue;
                }

                // Extent of the hole along its axis, from the face's bounds.
                using var sub = body.Shape.Sub(TopoKind.Face, f);
                var fb = sub.Bounds;
                var (a0, a1) = (double.MaxValue, double.MinValue);
                for (var k = 0; k < 8; k++)
                {
                    var p = new Vec3((k & 1) == 0 ? fb.Min.X : fb.Max.X, (k & 2) == 0 ? fb.Min.Y : fb.Max.Y, (k & 4) == 0 ? fb.Min.Z : fb.Max.Z);
                    var d = Vec3.Dot(p - origin, axis);
                    (a0, a1) = (Math.Min(a0, d), Math.Max(a1, d));
                }

                var through = Math.Abs(a1 - a0 - Extent(box, axis)) < 1e-3;
                var up = Vec3.Dot(axis, Vec3.UnitZ) < 0 ? -axis : axis;
                var top = origin + (axis * (Vec3.Dot(up, axis) > 0 ? a1 : a0));
                holes.Add((2 * radius, top, up, through));
            }
        }

        foreach (var group in holes.GroupBy(h => Math.Round(h.D, 3)).OrderBy(g => g.Key))
        {
            var first = group.First();
            var count = group.Count();
            var text = (count > 1 ? $"{count}× " : string.Empty) + "Ø" + Units.Format(group.Key, unit) + (group.All(h => h.Through) ? " THRU" : string.Empty);
            var knee = first.Top + (first.Axis * gap) + (new Vec3(1, 1, 0).Normalized() * gap * 0.5);
            list.Add(new PmiAnnotation([(first.Top, knee)], knee, text));
        }

        return list;
    }

    private static PmiAnnotation Linear(Vec3 a, Vec3 b, Vec3 offset, LengthUnit unit)
    {
        var da = a + offset;
        var db = b + offset;
        var length = Vec3.Distance(a, b);
        return new PmiAnnotation([(a, da + (offset.Normalized() * 1)), (b, db + (offset.Normalized() * 1)), (da, db)], (da + db) * 0.5,
            Units.Format(length, unit));
    }

    private static double Extent(Box3 box, Vec3 axis)
    {
        var s = box.Size;
        return Math.Abs(axis.X * s.X) + Math.Abs(axis.Y * s.Y) + Math.Abs(axis.Z * s.Z);
    }

    /// <summary>The face's outward normal points toward its axis (material around it).</summary>
    private static bool IsHole(Body body, int face)
    {
        var info = body.Face(face);
        var axis = info.AxisDirection.Normalized();
        var probe = info.AxisOrigin + (axis.AnyPerpendicular().Normalized() * (info.Radius * 2));
        if (body.Shape.ProjectOnFace(face, probe) is not { } hit)
        {
            return false;
        }

        var radial = hit.Point - info.AxisOrigin;
        radial -= axis * Vec3.Dot(radial, axis);
        return Vec3.Dot(hit.Normal, radial) < 0;
    }
}
