using System.Globalization;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>
/// A pipe, tube or cable route: a 3D polyline with bends of a set radius at its corners, and a
/// round section (hollow when it has a wall) swept along it.
/// </summary>
public sealed class RouteFeature : Feature
{
    public override string DefaultName => "Route";

    public List<Point3D> Points { get; set; } = [];

    public double OuterDiameter { get; set; } = 20;

    /// <summary>Wall thickness; 0 makes a solid rod or cable.</summary>
    public double Wall { get; set; } = 2;

    /// <summary>Centreline bend radius at each corner; 0 makes mitred (sharp) corners.</summary>
    public double BendRadius { get; set; } = 30;

    /// <summary>The centreline as straight runs and bends (start, mid, end per arc).</summary>
    public IReadOnlyList<(bool Arc, Vec3 Start, Vec3 Mid, Vec3 End)> Centerline()
    {
        var pts = Points.Select(p => p.ToVec3()).ToList();
        for (var i = pts.Count - 1; i > 0; i--)
        {
            if (Vec3.Distance(pts[i], pts[i - 1]) < 1e-9)
            {
                pts.RemoveAt(i);
            }
        }

        if (pts.Count < 2)
        {
            throw new FeatureException("A route needs at least two different points.");
        }

        var segments = new List<(bool, Vec3, Vec3, Vec3)>();
        var current = pts[0];
        for (var i = 1; i < pts.Count - 1; i++)
        {
            var d1 = (pts[i] - pts[i - 1]).Normalized();
            var d2 = (pts[i + 1] - pts[i]).Normalized();
            var cos = Math.Clamp(Vec3.Dot(d1, d2), -1, 1);
            var theta = Math.Acos(cos);
            if (BendRadius <= 0 || theta < 1e-6)
            {
                continue; // straight through, or a sharp corner
            }

            if (theta > Math.PI - 1e-3)
            {
                throw new FeatureException("A route can't double back on itself.");
            }

            var t = BendRadius * Math.Tan(theta / 2);
            if (t > Vec3.Distance(pts[i], pts[i - 1]) / (i == 1 ? 1 : 2) + 1e-9 || t > Vec3.Distance(pts[i + 1], pts[i]) / (i == pts.Count - 2 ? 1 : 2) + 1e-9)
            {
                throw new FeatureException("The bend radius is too big for these runs; make it smaller or the runs longer.");
            }

            var t1 = pts[i] - (d1 * t);
            var t2 = pts[i] + (d2 * t);
            var bisector = (d2 - d1).Normalized();
            var center = pts[i] + (bisector * (BendRadius / Math.Cos(theta / 2)));
            var mid = center - (bisector * BendRadius);
            if (Vec3.Distance(current, t1) > 1e-9)
            {
                segments.Add((false, current, current, t1));
            }

            segments.Add((true, t1, mid, t2));
            current = t2;
        }

        segments.Add((false, current, current, pts[^1]));
        return segments;
    }

    /// <summary>Length along the centreline (runs plus bends).</summary>
    public double CenterlineLength() => Centerline().Sum(s => s.Arc ? ArcLength(s.Start, s.Mid, s.End) : Vec3.Distance(s.Start, s.End));

    private static double ArcLength(Vec3 a, Vec3 m, Vec3 b)
    {
        // Circle through three points: the chord a–b and the sagitta give the angle.
        var chord = Vec3.Distance(a, b);
        var mid = (a + b) * 0.5;
        var sagitta = Vec3.Distance(m, mid);
        var r = ((chord * chord / 4) + (sagitta * sagitta)) / (2 * sagitta);
        var angle = 2 * Math.Asin(Math.Clamp(chord / (2 * r), -1, 1));
        return r * (sagitta > r ? (2 * Math.PI) - angle : angle);
    }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (OuterDiameter <= 0 || Wall < 0 || Wall * 2 >= OuterDiameter)
        {
            throw new FeatureException("The diameter must be positive and the wall less than half of it.");
        }

        var segments = Centerline();
        using var path = Modeling.Path3D(segments);
        var start = segments[0].Start;
        var dir = (segments[0].Arc ? segments[0].Mid - segments[0].Start : segments[0].End - segments[0].Start).Normalized();
        var plane = new Plane3(start, dir, dir.AnyPerpendicular());
        var r = OuterDiameter / 2;
        List<IReadOnlyList<ProfileSegment>> loops = [[ProfileSegment.Circle(0, 0, r)]];
        if (Wall > 0)
        {
            loops.Add([ProfileSegment.Circle(0, 0, r - Wall)]);
        }

        using var face = Modeling.Face(plane, loops);
        Operation op;
        try
        {
            op = Modeling.Sweep(face, path);
        }
        catch (KernelException ex)
        {
            throw new FeatureException($"The section can't follow this route: {ex.Message}");
        }

        using (op)
        {
            var pipe = op.Result;
            var names = Naming.Finish(pipe, new string?[pipe.FaceCount], Id);
            var inv = CultureInfo.InvariantCulture;
            var label = Wall > 0 ? $"Pipe Ø{OuterDiameter.ToString("0.#", inv)}x{Wall.ToString("0.#", inv)}" : $"Rod Ø{OuterDiameter.ToString("0.#", inv)}";
            ctx.Bodies.Add(new Body(pipe, names, $"{label} L{CenterlineLength().ToString("0.#", inv)} <{ctx.Bodies.Count + 1}>"));
        }
    }
}
