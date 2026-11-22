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

