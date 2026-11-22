using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>Constant-radius fillet on edges, and on every edge of selected faces.</summary>
public sealed class FilletFeature : Feature
{
    public override string DefaultName => "Fillet";

    public List<TopoRef> Edges { get; set; } = [];

    /// <summary>Faces whose boundary edges are all filleted.</summary>
    public List<TopoRef> Faces { get; set; } = [];

    public double Radius { get; set; } = 1;

    /// <summary>Variable radius: each edge runs from <see cref="Radius"/> at its start to this at its end.</summary>
    public double? EndRadius { get; set; }

    /// <summary>Face fillet: round only the edges the selected faces share, not all their edges.</summary>
    public bool BetweenFaces { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Radius <= 0 || EndRadius is <= 0)
        {
            throw new FeatureException("The radius must be greater than zero.");
        }

        var groups = BetweenFaces ? DressUp.SharedEdges(ctx, Faces) : DressUp.GroupEdges(ctx, Edges, Faces);
        foreach (var (body, edges) in groups)
        {
            using var op = Run(() => EndRadius is { } end
                    ? Modeling.FilletVariable(body.Shape, edges, [.. edges.Select(_ => Radius)], [.. edges.Select(_ => end)])
                    : Modeling.Fillet(body.Shape, edges, [.. edges.Select(_ => Radius)]),
                "The fillet can't be built. Try a smaller radius.");
            ctx.Replace(body, op, "fillet");
        }
    }

    internal static Operation Run(Func<Operation> build, string message)
    {
        try
        {
            var op = build();
            if (!op.Result.IsValid)
            {
                op.Dispose();
                throw new FeatureException(message);
            }

            return op;
        }
        catch (KernelException ex)
        {
            throw new FeatureException(message, ex);
        }
    }
}

public enum ChamferType
{
    /// <summary>Equal distance on both faces.</summary>
    Distance,

    /// <summary>Different distances on the two faces.</summary>
    DistanceDistance,

    /// <summary>A distance and an angle from the first face.</summary>
    DistanceAngle,
}

/// <summary>Bevels edges.</summary>
public sealed class ChamferFeature : Feature
{
    public override string DefaultName => "Chamfer";

    public List<TopoRef> Edges { get; set; } = [];

    public List<TopoRef> Faces { get; set; } = [];

    public ChamferType Type { get; set; } = ChamferType.Distance;

    public double Distance { get; set; } = 1;

    public double Distance2 { get; set; } = 1;

    /// <summary>Degrees, for <see cref="ChamferType.DistanceAngle"/>.</summary>
    public double Angle { get; set; } = 45;

    /// <summary>Measure the first distance on the other face.</summary>
    public bool Flip { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var d2 = Type switch
        {
            ChamferType.Distance => Distance,
            ChamferType.DistanceDistance => Distance2,
            _ => Distance * Math.Tan(Angle * Math.PI / 180),
        };
        if (Distance <= 0 || d2 <= 0)
        {
            throw new FeatureException("Chamfer distances must be greater than zero.");
        }

        foreach (var (body, edges) in DressUp.GroupEdges(ctx, Edges, Faces))
        {
            var faces = edges.Select(e =>
            {
                var adjacent = body.Shape.FacesOfEdge(e);
                return adjacent.Length == 0 ? 0 : Flip && adjacent.Length > 1 ? adjacent[1] : adjacent[0];
            }).ToArray();
            using var op = FilletFeature.Run(
                () => Modeling.Chamfer(body.Shape, edges, faces, [.. edges.Select(_ => Distance)], [.. edges.Select(_ => d2)]),
                "The chamfer can't be built. Try a smaller distance.");
            ctx.Replace(body, op, "chamfer");
        }
    }
}

/// <summary>Hollows a body, leaving walls of constant thickness and removing selected faces.</summary>
public sealed class ShellFeature : Feature
{
    public override string DefaultName => "Shell";

    public List<TopoRef> FacesToRemove { get; set; } = [];

    public double Thickness { get; set; } = 1;

    /// <summary>Grow the walls outward instead of inward.</summary>
    public bool Outward { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Thickness <= 0)
        {
            throw new FeatureException("The thickness must be greater than zero.");
        }

        Body body;
        var faces = new List<int>();
        if (FacesToRemove.Count == 0)
        {
            body = ctx.Bodies.FirstOrDefault() ?? throw new FeatureException("There is no body to shell.");
        }
        else
        {
            (body, _) = ctx.ResolveFace(FacesToRemove[0]);
            foreach (var f in FacesToRemove)
            {
                var (b, i) = ctx.ResolveFace(f);
                if (b == body)
                {
                    faces.Add(i);
                }
            }
        }

        using var op = FilletFeature.Run(() => Modeling.Shell(body.Shape, faces, Outward ? -Thickness : Thickness),
            "The shell can't be built. The thickness may be larger than a fillet radius or wall.");
        ctx.Replace(body, op, "shell");
    }
}

/// <summary>Tapers faces about a neutral plane.</summary>
public sealed class DraftFeature : Feature
{
    public override string DefaultName => "Draft";

    public List<TopoRef> Faces { get; set; } = [];

    public PlaneRef NeutralPlane { get; set; } = PlaneRef.Top;

    /// <summary>Degrees.</summary>
    public double Angle { get; set; } = 1;

    public bool Reverse { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Faces.Count == 0)
        {
            throw new FeatureException("Select faces to draft.");
        }

        var neutral = ctx.ResolvePlane(NeutralPlane);
        var pull = Reverse ? -neutral.Normal : neutral.Normal;
        foreach (var group in Faces.Select(ctx.ResolveFace).GroupBy(x => x.Body))
        {
            var body = group.Key;
            using var op = FilletFeature.Run(
                () => Modeling.Draft(body.Shape, [.. group.Select(x => x.Index)], neutral.Origin, pull, Angle * Math.PI / 180),
                "The draft can't be built on these faces.");
            ctx.Replace(body, op, "draft");
        }
    }
}

internal static class DressUp
{
    /// <summary>Resolves edge and face references to edge indices grouped by body.</summary>
    public static List<(Body Body, int[] Edges)> GroupEdges(RebuildContext ctx, List<TopoRef> edges, List<TopoRef> faces)
    {
        var byBody = new Dictionary<Body, SortedSet<int>>();
        foreach (var e in edges)
        {
            var (b, i) = ctx.ResolveEdge(e);
            (byBody.TryGetValue(b, out var set) ? set : byBody[b] = []).Add(i);
        }

        foreach (var f in faces)
        {
            var (b, i) = ctx.ResolveFace(f);
            var set = byBody.TryGetValue(b, out var s) ? s : byBody[b] = [];
            foreach (var e in b.Shape.EdgesOfFace(i))
            {
                if (b.Shape.FacesOfEdge(e).Length > 1)
                {
                    set.Add(e);
                }
            }
        }

        if (byBody.Count == 0)
        {
            throw new FeatureException("Select edges or faces.");
        }

        return [.. byBody.Select(kv => (kv.Key, kv.Value.ToArray()))];
    }

    /// <summary>The edges where any two of the faces meet (for face fillets).</summary>
    public static List<(Body Body, int[] Edges)> SharedEdges(RebuildContext ctx, List<TopoRef> faces)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(faces);
        var byBody = faces.Select(ctx.ResolveFace).GroupBy(x => x.Body);
        var result = new List<(Body, int[])>();
        foreach (var group in byBody)
        {
            var body = group.Key;
            var picked = group.Select(x => x.Index).ToHashSet();
            var shared = picked.SelectMany(f => body.Shape.EdgesOfFace(f))
                .Distinct()
                .Where(e => body.Shape.FacesOfEdge(e).Count(picked.Contains) >= 2)
                .ToArray();
            if (shared.Length > 0)
            {
                result.Add((body, shared));
            }
        }

        return result.Count > 0 ? result : throw new FeatureException("The selected faces don't meet; pick faces that share an edge.");
    }
}

/// <summary>A spherical dome raised (or sunk) on a planar face, following the face's outline.</summary>
public sealed class DomeFeature : Feature
{
    public override string DefaultName => "Dome";

    public TopoRef? Face { get; set; }

    /// <summary>Height of the dome above the face (mm).</summary>
    public double Height { get; set; } = 5;

    /// <summary>Sink the dome into the part instead.</summary>
    public bool Reverse { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Face is null)
        {
            throw new FeatureException("Select a flat face for the dome.");
        }

        if (Height <= 0)
        {
            throw new FeatureException("The dome height must be positive.");
        }

        var (body, index) = ctx.ResolveFace(Face);
        _ = body.Shape.FacePlane(index) ?? throw new FeatureException("Domes go on flat faces.");
        using var face = body.Shape.Sub(TopoKind.Face, index);

        // The circle around the face's outline (centred on its centroid) sets the sphere.
        var info = body.Face(index);
        var center = info.Centroid;
        var c = 0.0;
        foreach (var e in body.Shape.EdgesOfFace(index))
        {
            foreach (var p in body.Shape.SampleEdge(e))
            {
                c = Math.Max(c, Vec3.Distance(p, center));
            }
        }

        var h = Height;
        var radius = ((c * c) + (h * h)) / (2 * h);
        var normal = info.Normal.Normalized();
        var dir = Reverse ? -normal : normal;
        var sphereCenter = center + (dir * (h - radius));

        using var sphere = Modeling.Sphere(radius);
        using var placed = Modeling.Transform(sphere, Transform3.Translation(sphereCenter));
        using var prism = Modeling.Extrude(face, dir * h);
        using var cap = Modeling.Boolean(placed.Result, prism.Result, BooleanKind.Common);
        var tool = cap.Result;
        if (tool.SolidCount == 0)
        {
            throw new FeatureException("The dome couldn't be made on this face.");
        }

        var names = Naming.Finish(tool, new string?[tool.FaceCount], Id);
        if (Reverse)
        {
            ctx.RemoveMaterial(tool, names);
        }
        else
        {
            ctx.AddMaterial(tool, names, merge: true);
        }

        ctx.Tools[Id] = new FeatureTool(tool, names, Reverse);
    }
}

/// <summary>
/// Full-round fillet: replaces a centre face (the top of a rib, the end of a tab) with a half-round
/// tangent to the two parallel side faces either side of it, its diameter the distance between them.
/// </summary>
public sealed class FullRoundFilletFeature : Feature
{
    public override string DefaultName => "Full Round";

    public TopoRef? SideFace1 { get; set; }

    public TopoRef? CenterFace { get; set; }

    public TopoRef? SideFace2 { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (SideFace1 is null || CenterFace is null || SideFace2 is null)
        {
            throw new FeatureException("Select a side face, the centre face and the other side face.");
        }

        var (body, center) = ctx.ResolveFace(CenterFace);
        var (_, side1) = ctx.ResolveFace(SideFace1);
        var (_, side2) = ctx.ResolveFace(SideFace2);
        if (body.Shape.FacePlane(center) is not { } pc || body.Shape.FacePlane(side1) is not { } p1 || body.Shape.FacePlane(side2) is not { } p2)
        {
            throw new FeatureException("Full round works on flat faces.");
        }

        if (Vec3.Dot(p1.Normal, p2.Normal) > -0.9999 || Math.Abs(Vec3.Dot(p1.Normal, pc.Normal)) > 1e-4)
        {
            throw new FeatureException("The side faces must be parallel and face apart, square to the centre face.");
        }

        var across = p1.Normal;
        var thickness = Math.Abs(Vec3.Dot(p2.Origin - p1.Origin, across));
        var r = thickness / 2;
        var up = pc.Normal;
        var along = Vec3.Cross(across, up).Normalized();

        // The round's axis: r below the centre face, midway between the sides.
        var c = body.Face(center).Centroid;
        var mid = (p1.Origin + p2.Origin) * 0.5;
        var axis = c - (up * r) + (across * Vec3.Dot(mid - c, across));
        var span = body.Shape.EdgesOfFace(center).SelectMany(e => body.Shape.SampleEdge(e)).Select(p => Vec3.Dot(p - axis, along)).ToList();
        var (from, to) = (span.Min() - 1, span.Max() + 1);

        // The material beyond the axis, less a cylinder around it, is cut away.
        var start = new Plane3(axis + (along * from), along, across);
        using var block = Modeling.Face(start, [[
            ProfileSegment.Line(-r - 1, 0, r + 1, 0), ProfileSegment.Line(r + 1, 0, r + 1, r + 1),
            ProfileSegment.Line(r + 1, r + 1, -r - 1, r + 1), ProfileSegment.Line(-r - 1, r + 1, -r - 1, 0)]]);
        using var disc = Modeling.Face(start, [[ProfileSegment.Circle(0, 0, r)]]);
        using var slab = Modeling.Extrude(block, along * (to - from));
        using var rod = Modeling.Extrude(disc, along * (to - from));
        using var tool = Modeling.Boolean(slab.Result, rod.Result, BooleanKind.Cut);
        var cutter = tool.Result;
        ctx.RemoveMaterial(cutter, Naming.Finish(cutter, new string?[cutter.FaceCount], Id));
    }
}

/// <summary>A model corner, named by an edge that ends there and which end.</summary>
public sealed record VertexRef(TopoRef Edge, bool AtEnd);

/// <summary>
/// Vertex chamfer: cuts a corner where three edges meet with a flat face through points the given
/// distances along each edge (equal, or one each).
/// </summary>
public sealed class VertexChamferFeature : Feature
{
    public override string DefaultName => "Vertex Chamfer";

    public List<VertexRef> Vertices { get; set; } = [];

    public double Distance { get; set; } = 2;

    /// <summary>Distances along the second and third edges; null uses <see cref="Distance"/>.</summary>
    public double? Distance2 { get; set; }

    public double? Distance3 { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Vertices.Count == 0)
        {
            throw new FeatureException("Select the corners to chamfer.");
        }

        double[] distances = [Distance, Distance2 ?? Distance, Distance3 ?? Distance];
        if (distances.Any(d => d <= 0))
        {
            throw new FeatureException("The distances must be greater than zero.");
        }

        foreach (var v in Vertices)
        {
            var (body, index) = ctx.ResolveEdge(v.Edge);
            var corner = v.AtEnd ? body.Edge(index).End : body.Edge(index).Start;

            // The edges that meet at the corner, and the way each leaves it.
            var leaving = new List<Vec3>();
            for (var e = 1; e <= body.EdgeCount; e++)
            {
                var info = body.Edge(e);
                var atStart = Vec3.Distance(info.Start, corner) < 1e-6;
                var atEnd = Vec3.Distance(info.End, corner) < 1e-6;
                if (atStart == atEnd)
                {
                    continue; // not at the corner (or a closed edge through it)
                }

                var pts = body.Shape.SampleEdge(e, 0.01);
                var next = atStart ? pts[Math.Min(1, pts.Count - 1)] : pts[Math.Max(0, pts.Count - 2)];
                leaving.Add((next - corner).Normalized());
            }

            if (leaving.Count != 3)
            {
                throw new FeatureException("A vertex chamfer needs a corner where exactly three edges meet.");
            }

            // Distances 1, 2 and 3 go to the corner's edges in the order the body lists them.
            var points = leaving.Select((d, i) => corner + (d * distances[i])).ToList();
            var tool = Tetrahedron(corner, points[0], points[1], points[2]);
            ctx.RemoveMaterial(tool, Naming.Finish(tool, new string?[tool.FaceCount], Id));
        }
    }

    /// <summary>A tetrahedron from four corners, sewn from its four triangles.</summary>
    private static Shape Tetrahedron(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var faces = new List<Shape>();
        try
        {
            foreach (var (p, q, r) in new[] { (b, c, d), (a, b, c), (a, c, d), (a, d, b) })
            {
                var n = Vec3.Cross(q - p, r - p);
                if (n.LengthSquared < 1e-18)
                {
                    throw new FeatureException("The chamfer's corner points are in a line.");
                }

                var plane = new Plane3(p, n.Normalized(), (q - p).Normalized());
                var (qu, qv) = plane.ToLocal(q);
                var (ru, rv) = plane.ToLocal(r);
                faces.Add(Modeling.Face(plane, [[ProfileSegment.Line(0, 0, qu, qv), ProfileSegment.Line(qu, qv, ru, rv), ProfileSegment.Line(ru, rv, 0, 0)]]));
            }

            using var sewn = Modeling.Sew(faces);
            if (sewn.Result.SolidCount == 0)
            {
                throw new FeatureException("The chamfer's cutting shape didn't close up.");
            }

            return sewn.Result.Sub(TopoKind.Solid, 1);
        }
        finally
        {
            foreach (var f in faces)
            {
                f.Dispose();
            }
        }
    }
}

/// <summary>
/// Wrap: lays a sketch's closed regions onto a cylindrical face (sketch distances become lengths
/// around it) and raises them (emboss) or sinks them (deboss) by a depth.
/// </summary>
public sealed class WrapFeature : Feature
{
    public override string DefaultName => "Wrap";

    public string SketchId { get; set; } = string.Empty;

    public TopoRef? Face { get; set; }

    /// <summary>Sink the regions into the face instead of raising them.</summary>
    public bool Deboss { get; set; }

    public double Depth { get; set; } = 1;

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Depth <= 0)
        {
            throw new FeatureException("The depth must be greater than zero.");
        }

        var sketch = ctx.Sketch(SketchId);
        var (body, index) = ctx.ResolveFace(Face ?? throw new FeatureException("Select the cylindrical face to wrap onto."));
        var info = body.Face(index);
        if (info.Surface != SurfaceKind.Cylinder)
        {
            throw new FeatureException("Wrap goes onto a cylindrical face.");
        }

        // Angle zero faces the sketch; distances across the sketch become arc length round the cylinder.
        var axis = info.AxisDirection.Normalized();
        var o = info.AxisOrigin;
        var toSketch = sketch.Plane.Origin - o;
        var radial = toSketch - (axis * Vec3.Dot(toSketch, axis));
        if (radial.LengthSquared < 1e-12)
        {
            throw new FeatureException("Put the sketch on a plane beside the cylinder, not through its axis.");
        }

        radial = radial.Normalized();
        var around = Vec3.Cross(axis, radial);
        var r = info.Radius;
        (double, double) Map(Chamfer.Sketching.Vec2 p)
        {
            var w = sketch.Plane.ToWorld(p.X, p.Y);
            return (Vec3.Dot(w - sketch.Plane.Origin, around) / r, Vec3.Dot(w - o, axis));
        }

        var regions = ProfileShapes.Regions(sketch);
        if (regions.Count == 0)
        {
            throw new FeatureException("The sketch needs closed regions to wrap.");
        }

        foreach (var region in regions)
        {
            List<IReadOnlyList<(double, double)>> loops = [[.. region.Outer.Segments.SelectMany(s => s.Sample()).Select(Map)]];
            loops.AddRange(region.Holes.Select(h => (IReadOnlyList<(double, double)>)[.. h.Segments.SelectMany(s => s.Sample()).Select(Map)]));
            using var face = Modeling.WrapFace(o, axis, radial, r, loops);
            Operation thick;
            try
            {
                thick = Modeling.Thicken(face, Deboss ? -Depth : Depth);
            }
            catch (KernelException ex)
            {
                throw new FeatureException("The wrapped region can't be given that depth.", ex);
            }

            using (thick)
            {
                var solid = thick.Result;
                var names = Naming.Finish(solid, new string?[solid.FaceCount], Id);
                if (Deboss)
                {
                    ctx.RemoveMaterial(solid, names);
                }
                else
                {
                    ctx.AddMaterial(solid, names, merge: true);
                }
            }
        }
    }
}

/// <summary>How Flex deforms the bodies.</summary>
public enum FlexMode
{
    Twisting,
    Tapering,
    Stretching,
}

/// <summary>
/// Flex: twists, tapers or stretches bodies along an axis, from where the axis starts over a length
/// (beyond it the change holds, before it nothing moves).
/// </summary>
public sealed class FlexFeature : Feature
{
    public override string DefaultName => "Flex";

    /// <summary>Bodies to flex; empty flexes every solid body.</summary>
    public List<string> Bodies { get; set; } = [];

    public FlexMode Mode { get; set; }

    /// <summary>The flex axis; its origin is where the flexing starts.</summary>
    public AxisRef Axis { get; set; } = AxisRef.Z;

    public double Length { get; set; } = 100;

    /// <summary>Twist angle in degrees.</summary>
    public double Angle { get; set; } = 90;

    /// <summary>Taper: the size at the far end, as a fraction of the start (0.5 is half size).</summary>
    public double EndScale { get; set; } = 0.5;

    /// <summary>Stretch: how much longer the flexed length gets, in mm.</summary>
    public double Stretch { get; set; } = 10;

    public override IEnumerable<string> Dependencies => new[] { Axis.AxisId, Axis.SketchId }.OfType<string>();

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Length <= 0)
        {
            throw new FeatureException("The flex length must be greater than zero.");
        }

        if (Mode == FlexMode.Tapering && EndScale <= 0)
        {
            throw new FeatureException("The end size must be greater than zero.");
        }

        var (origin, direction) = ctx.ResolveAxis(Axis);
        direction = direction.Normalized();
        var (mode, amount) = Mode switch
        {
            FlexMode.Twisting => (0, Angle * Math.PI / 180),
            FlexMode.Tapering => (1, EndScale - 1),
            _ => (2, Stretch / Length),
        };
        var targets = Bodies.Count == 0 ? ctx.Bodies.Where(b => !b.IsSurface).ToList() : Bodies.Select(ctx.FindBody).ToList();
        foreach (var body in targets)
        {
            Shape flexed;
            try
            {
                flexed = Modeling.Flex(body.Shape, origin, direction, direction.AnyPerpendicular(), mode, amount, Length);
            }
            catch (KernelException ex)
            {
                throw new FeatureException($"The body can't be flexed that way: {ex.Message}");
            }

            if (flexed.SolidCount == 0 || !flexed.IsValid)
            {
                flexed.Dispose();
                throw new FeatureException("Flexing left an invalid body; try a gentler amount.");
            }

            // The topology is unchanged, so the faces keep their names.
            var names = flexed.FaceCount == body.FaceCount ? [.. body.FaceNames] : Naming.Finish(flexed, new string?[flexed.FaceCount], Id);
            ctx.Bodies[ctx.Bodies.IndexOf(body)] = new Body(flexed, names, body.Name, body.Color);
        }
    }
}

/// <summary>
/// Deform (point): pushes the model's surface near a point along a direction by a distance, fading
/// smoothly to nothing at a radius, like pressing a dent or pulling a bulge.
/// </summary>
public sealed class DeformFeature : Feature
{
    public override string DefaultName => "Deform";

    /// <summary>The point to push (a reference point).</summary>
    public string PointId { get; set; } = string.Empty;

    /// <summary>Which way to push.</summary>
    public AxisRef Direction { get; set; } = AxisRef.Z;

    public double Distance { get; set; } = 3;

    /// <summary>How far from the point the deformation reaches.</summary>
    public double Radius { get; set; } = 20;

    /// <summary>Bodies to deform; empty deforms every solid body.</summary>
    public List<string> Bodies { get; set; } = [];

    public override IEnumerable<string> Dependencies => new[] { PointId, Direction.AxisId }.OfType<string>().Where(id => id.Length > 0);

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Radius <= 0)
        {
            throw new FeatureException("The radius must be greater than zero.");
        }

        var point = ctx.ResolvePoint(PointId.Length > 0 ? PointId : throw new FeatureException("Select the point to push."));
        var dir = ctx.ResolveAxis(Direction).Direction.Normalized();
        var targets = Bodies.Count == 0 ? ctx.Bodies.Where(b => !b.IsSurface).ToList() : Bodies.Select(ctx.FindBody).ToList();
        foreach (var body in targets.Where(b => b.Shape.Bounds.Intersects(new Box3(point - new Vec3(Radius, Radius, Radius), point + new Vec3(Radius, Radius, Radius)))))
        {
            Shape moved;
            try
            {
                moved = Modeling.Flex(body.Shape, point, dir, dir.AnyPerpendicular(), 3, Distance, Radius);
            }
            catch (KernelException ex)
            {
                throw new FeatureException($"The body can't be deformed that way: {ex.Message}");
            }

            if (moved.SolidCount == 0 || !moved.IsValid)
            {
                moved.Dispose();
                throw new FeatureException("Deforming left an invalid body; try a smaller distance or a larger radius.");
            }

            var names = moved.FaceCount == body.FaceCount ? [.. body.FaceNames] : Naming.Finish(moved, new string?[moved.FaceCount], Id);
            ctx.Bodies[ctx.Bodies.IndexOf(body)] = new Body(moved, names, body.Name, body.Color);
        }
    }
}

