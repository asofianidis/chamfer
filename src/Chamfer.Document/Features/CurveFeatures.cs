using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching.Entities;

namespace Chamfer.Document.Features;

/// <summary>
/// A helix on the circle of a sketch: its centre and radius come from the
/// sketch's first circle, it rises along the sketch normal. Use it as a sweep
/// path for springs and threads.
/// </summary>
public sealed class HelixFeature : Feature
{
    public override string DefaultName => "Helix";

    public string SketchId { get; set; } = string.Empty;

    public double Pitch { get; set; } = 5;

    public double Height { get; set; } = 30;

    public bool LeftHanded { get; set; }

    /// <summary>Start angle in degrees, measured from the sketch's X axis.</summary>
    public double StartAngle { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Pitch <= 0 || Height <= 0)
        {
            throw new FeatureException("Pitch and height must be greater than zero.");
        }

        var sketch = ctx.Sketch(SketchId);
        var circle = sketch.Sketch.Entities.OfType<SketchCircle>().FirstOrDefault()
            ?? throw new FeatureException("Sketch a circle for the helix's diameter and position.");
        var center = sketch.Plane.ToWorld(circle.Center.X, circle.Center.Y);
        var a = StartAngle * Math.PI / 180;
        var x = (sketch.Plane.XDirection * Math.Cos(a)) + (sketch.Plane.YDirection * Math.Sin(a));
        ctx.Curves[Id] = Modeling.Helix(new Plane3(center, sketch.Plane.Normal, x), Pitch, Height, circle.Radius, LeftHanded);
    }
}

/// <summary>
/// A modelled ISO metric thread on a cylindrical face: a 60° thread form
/// swept along a helix and cut out of the shaft (external) or the hole wall
/// (internal, detected from the face's orientation).
/// </summary>
public sealed class ThreadFeature : Feature
{
    public override string DefaultName => "Thread";

    public TopoRef? Face { get; set; }

    public double Pitch { get; set; } = 1;

    /// <summary>Thread length along the axis; 0 threads the whole face.</summary>
    public double Length { get; set; }

    /// <summary>Start from the other end of the face.</summary>
    public bool FromOtherEnd { get; set; }

    public bool LeftHanded { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Face is null)
        {
            throw new FeatureException("Select a cylindrical face to thread.");
        }

        if (Pitch <= 0)
        {
            throw new FeatureException("The pitch must be greater than zero.");
        }

        var (body, index) = ctx.ResolveFace(Face);
        var info = body.Face(index);
        if (info.Surface != SurfaceKind.Cylinder)
        {
            throw new FeatureException("Threads go on cylindrical faces.");
        }

        var axis = info.AxisDirection.Normalized();
        var r = info.Radius;

        // Internal when the face's outward normal points at the axis (a hole).
        var radialAtCentroid = info.Centroid - info.AxisOrigin - (axis * Vec3.Dot(info.Centroid - info.AxisOrigin, axis));
        var internalThread = Vec3.Dot(info.Normal, radialAtCentroid) < 0;

        // The face's extent along the axis, from its circular edges.
        var along = body.Shape.EdgesOfFace(index).Select(e => body.Edge(e))
            .SelectMany(e => new[] { e.Start, e.End, e.Midpoint })
            .Select(p => Vec3.Dot(p - info.AxisOrigin, axis)).ToList();
        double lo = along.Min(), hi = along.Max();
        var length = Length > 0 ? Math.Min(Length, hi - lo) : hi - lo;
        var dir = FromOtherEnd ? -axis : axis;
        var startAt = FromOtherEnd ? hi : lo;
        var origin = info.AxisOrigin + (axis * startAt);

        // Start half a pitch outside the face so the thread runs out cleanly.
        var lead = Pitch;
        var x = axis.AnyPerpendicular();
        var frame = new Plane3(origin - (dir * lead), dir, x);

        // Truncated 60° profile (ISO 68-1 proportions) in the plane of the axis and x, at the helix start.
        var depth = 0.6134 * Pitch;
        var wide = 0.875 * Pitch / 2;
        var narrow = 0.125 * Pitch / 2;
        var sign = internalThread ? 1.0 : -1.0;
        var surface = r - (sign * 0.05 * Pitch);
        var tip = r + (sign * depth);
        var profilePlane = new Plane3(frame.Origin, Vec3.Cross(x, dir), x);
        var segments = new List<ProfileSegment>
        {
            ProfileSegment.Line(surface, -wide, tip, -narrow),
            ProfileSegment.Line(tip, -narrow, tip, narrow),
            ProfileSegment.Line(tip, narrow, surface, wide),
            ProfileSegment.Line(surface, wide, surface, -wide),
        };

        using var profile = Modeling.Face(profilePlane, [segments]);
        using var helix = Modeling.Helix(frame, Pitch, length + lead, r, LeftHanded);
        Operation sweep;
        try
        {
            sweep = Modeling.Sweep(profile, helix, correctedFrenet: false);
        }
        catch (KernelException ex)
        {
            throw new FeatureException("The thread form can't be swept on this face.", ex);
        }

        using (sweep)
        {
            var tool = sweep.Result;
            var names = Naming.Finish(tool, new string?[tool.FaceCount], Id);
            ctx.Tools[Id] = new FeatureTool(tool, names, IsCut: true);
            ctx.RemoveMaterial(tool, names);
        }
    }
}

/// <summary>A point of a 3D sketch.</summary>
public sealed record Point3D(double X, double Y, double Z)
{
    public Vec3 ToVec3() => new(X, Y, Z);
}

/// <summary>
/// A 3D sketch: a polyline or spline through points in space, for sweep paths, routing
/// and wireframes. The result is a curve, like a helix.
/// </summary>
public sealed class Sketch3DFeature : Feature
{
    public override string DefaultName => "3DSketch";

    public List<Point3D> Points { get; set; } = [];

    /// <summary>A smooth spline through the points instead of straight segments.</summary>
    public bool Spline { get; set; }

    public bool Closed { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
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
            throw new FeatureException("A 3D sketch needs at least two different points.");
        }

        if (Closed && pts.Count < 3)
        {
            throw new FeatureException("A closed 3D sketch needs at least three points.");
        }

        ctx.Curves[Id] = Modeling.Wire3D(pts, Spline, Closed);
    }

    /// <summary>
    /// Parses points written as "x,y,z" separated by '|', ';' or new lines. A point starting with '@'
    /// is relative to the previous one, e.g. "0,0,0 | @50,0,0 | @0,0,30".
    /// </summary>
    public static List<Point3D> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var list = new List<Point3D>();
        foreach (var raw in text.Split(['|', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var relative = raw.StartsWith('@');
            var parts = raw.TrimStart('@').Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                throw new FormatException($"\"{raw}\" isn't a point; write x,y,z.");
            }

            var v = parts.Select(s => double.Parse(s,System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var p = relative && list.Count > 0 ? new Point3D(list[^1].X + v[0], list[^1].Y + v[1], list[^1].Z + v[2]) : new Point3D(v[0], v[1], v[2]);
            list.Add(p);
        }

        return list;
    }

    public static string Format(IEnumerable<Point3D> points) =>
        string.Join(" | ", points.Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}")));
}

/// <summary>A sketch's curves projected along the sketch normal onto a face: a 3D curve on it (Projected Curve).</summary>
public sealed class ProjectedCurveFeature : Feature
{
    public override string DefaultName => "Curve";

    public string SketchId { get; set; } = string.Empty;

    public TopoRef? Face { get; set; }

    /// <summary>Project against the sketch normal instead of along it.</summary>
    public bool Reverse { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sketch = ctx.Sketch(SketchId);
        var (body, index) = ctx.ResolveFace(Face ?? throw new FeatureException("Select the face to project onto."));
        using var wire = AdvancedShapes.Wire(sketch, "The sketch must be one connected chain of curves.");
        using var face = body.Shape.Sub(TopoKind.Face, index);
        try
        {
            ctx.Curves[Id] = Modeling.ProjectCurve(wire, face, sketch.Plane.Normal * (Reverse ? -1 : 1));
        }
        catch (KernelException ex)
        {
            throw new FeatureException($"The sketch doesn't project onto the face: {ex.Message}");
        }
    }
}

/// <summary>Several curves (sketches, helices, 3D sketches, other curves) joined end to end into one (Composite Curve).</summary>
public sealed class CompositeCurveFeature : Feature
{
    public override string DefaultName => "CompCurve";

    public List<string> CurveIds { get; set; } = [];

    public override IEnumerable<string> Dependencies => CurveIds;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (CurveIds.Count < 2)
        {
            throw new FeatureException("Select at least two curves to join.");
        }

        var parts = new List<Shape>();
        try
        {
            foreach (var id in CurveIds)
            {
                parts.Add(ctx.Curves.TryGetValue(id, out var curve)
                    ? Modeling.Transform(curve, Transform3.Identity).Result
                    : AdvancedShapes.Wire(ctx.Sketch(id), "Each sketch must be one connected chain of curves."));
            }

            ctx.Curves[Id] = Modeling.JoinCurves(parts);
        }
        catch (KernelException ex)
        {
            throw new FeatureException($"The curves can't be joined: {ex.Message}");
        }
        finally
        {
            foreach (var p in parts)
            {
                p.Dispose();
            }
        }
    }
}

