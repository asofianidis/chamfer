using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>
/// Cuts solid bodies with a surface body (or a plane) and removes the material on one
/// side: by default the side the surface's normal points to.
/// </summary>
public sealed class CutWithSurfaceFeature : Feature
{
    public override string DefaultName => "SurfaceCut";

    /// <summary>A face of the cutting surface body; when null, <see cref="Plane"/> cuts.</summary>
    public TopoRef? Surface { get; set; }

    public PlaneRef Plane { get; set; } = PlaneRef.Right;

    /// <summary>Remove the other side instead.</summary>
    public bool Flip { get; set; }

    /// <summary>Solid bodies to cut; empty cuts all of them.</summary>
    public List<string> Bodies { get; set; } = [];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Body? surfaceBody = null;
        Plane3? plane = null;
        if (Surface is { } s)
        {
            surfaceBody = ctx.ResolveFace(s).Body;
        }
        else
        {
            plane = ctx.ResolvePlane(Plane);
        }

        var targets = (Bodies.Count == 0 ? [.. ctx.Bodies] : Bodies.Select(ctx.FindBody).ToList())
            .Where(b => !b.IsSurface && b != surfaceBody).ToList();
        var cut = false;
        foreach (var body in targets)
        {
            using var planeTool = plane is { } p ? Modeling.PlaneFace(p, (body.Shape.Bounds.Diagonal * 2) + 10) : null;
            var tool = planeTool ?? surfaceBody!.Shape;
            using var op = Modeling.Split(body.Shape, tool);
            var whole = op.Result;
            if (whole.SolidCount < 2)
            {
                continue;
            }

            // Which side of the surface is each piece on?
            var keep = new List<Shape>();
            for (var i = 1; i <= whole.SolidCount; i++)
            {
                var piece = whole.Sub(TopoKind.Solid, i);
                var c = piece.MassProperties.CenterOfMass;
                var side = plane is { } pl ? pl.SignedDistance(c) : SideOfSurface(surfaceBody!.Shape, c);
                if ((side > 0) == Flip)
                {
                    keep.Add(piece);
                }
                else
                {
                    piece.Dispose();
                }
            }

            if (keep.Count == whole.SolidCount)
            {
                foreach (var k in keep)
                {
                    k.Dispose();
                }

                continue;
            }

            cut = true;
            var names = Naming.Transfer(op, whole, [new Naming.Input(body.Shape, body.FaceNames, body.EdgeNames)], Id, "cut");
            var index = ctx.Bodies.IndexOf(body);
            ctx.Bodies.RemoveAt(index);
            if (keep.Count == 0)
            {
                ctx.Warn($"{body.Name} was entirely on the removed side.");
                continue;
            }

            var result = keep.Count == 1 ? keep[0] : Modeling.Compound(keep);
            var resultNames = new string[result.FaceCount];
            for (var f = 1; f <= result.FaceCount; f++)
            {
                using var face = result.Sub(TopoKind.Face, f);
                var k = whole.IndexOf(TopoKind.Face, face);
                resultNames[f - 1] = k > 0 ? names[k - 1] : $"{Id}:face{f}";
            }

            ctx.Bodies.Insert(index, new Body(result, resultNames, body.Name, body.Color));
        }

        if (!cut)
        {
            ctx.Warn("The surface doesn't cut any of the selected bodies.");
        }
    }

    /// <summary>Signed distance from the surface along its normal at the nearest point.</summary>
    internal static double SideOfSurface(Shape surface, Vec3 p)
    {
        (Vec3 Point, Vec3 Normal, double Distance)? best = null;
        for (var f = 1; f <= surface.FaceCount; f++)
        {
            if (surface.ProjectOnFace(f, p) is { } hit && (best is null || hit.Distance < best.Value.Distance))
            {
                best = hit;
            }
        }

        return best is { } b ? Vec3.Dot(p - b.Point, b.Normal) : 0;
    }
}

/// <summary>Scales bodies uniformly about their centroid or the origin (mould shrinkage and the like).</summary>
public sealed class ScaleFeature : Feature
{
    public override string DefaultName => "Scale";

    public double Factor { get; set; } = 1.01;

    /// <summary>Scale about the part's centre of mass instead of the origin.</summary>
    public bool AboutCentroid { get; set; } = true;

    /// <summary>Bodies to scale; empty scales all of them.</summary>
    public List<string> Bodies { get; set; } = [];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Factor <= 0)
        {
            throw new FeatureException("The scale factor must be positive.");
        }

        var targets = Bodies.Count == 0 ? [.. ctx.Bodies] : Bodies.Select(ctx.FindBody).ToList();
        if (targets.Count == 0)
        {
            throw new FeatureException("There is nothing to scale.");
        }

        var center = Vec3.Zero;
        if (AboutCentroid)
        {
            double mass = 0;
            foreach (var b in targets.Where(b => !b.IsSurface))
            {
                var m = b.Shape.MassProperties;
                center += m.CenterOfMass * m.Volume;
                mass += m.Volume;
            }

            center = mass > 0 ? center / mass : targets.Aggregate(Box3.Empty, (box, b) => box.Union(b.Shape.Bounds)).Center;
        }

        var t = Transform3.Scale(center, Factor);
        foreach (var body in targets)
        {
            using var op = Modeling.Transform(body.Shape, t);
            ctx.Replace(body, op, "scale");
        }
    }
}

/// <summary>
/// Mould tooling: a block around the part, less the part (scaled for shrinkage), split at a flat
/// parting plane into a core (below) and a cavity (above). The part body stays.
/// </summary>
public sealed class ToolingSplitFeature : Feature
{
    public override string DefaultName => "Tooling Split";

    /// <summary>Normal of the parting plane (the pull direction); the cavity is on this side.</summary>
    public AxisRef Pull { get; set; } = AxisRef.Z;

    /// <summary>Parting plane height along the pull direction, measured from the part's lowest point.</summary>
    public double PartingHeight { get; set; }

    /// <summary>Block wall around the part on every side (mm).</summary>
    public double Margin { get; set; } = 15;

    /// <summary>Scale applied to the part's impression for material shrinkage (1 = none).</summary>
    public double Shrinkage { get; set; } = 1;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var parts = ctx.Bodies.Where(b => !b.IsSurface).ToList();
        if (parts.Count == 0)
        {
            throw new FeatureException("There's no solid to make tooling for.");
        }

        if (Margin <= 0 || Shrinkage <= 0)
        {
            throw new FeatureException("The margin and shrinkage must be positive.");
        }

        var pull = ctx.ResolveAxis(Pull).Direction.Normalized();
        var x = pull.AnyPerpendicular().Normalized();
        var y = Vec3.Cross(pull, x);

        // The part's extent in the pull frame.
        double Lo(Vec3 axis) => parts.Min(b => Extent(b.Shape, axis).Min);
        double Hi(Vec3 axis) => parts.Max(b => Extent(b.Shape, axis).Max);
        var (x0, x1, y0, y1, z0, z1) = (Lo(x), Hi(x), Lo(y), Hi(y), Lo(pull), Hi(pull));
        var parting = z0 + PartingHeight;
        if (parting < z0 - Margin || parting > z1 + Margin)
        {
            throw new FeatureException("The parting plane must pass through the block.");
        }

        // The block, as a rectangle on a plane normal to the pull, extruded through the part plus margins.
        var basePlane = new Plane3((x * (x0 - Margin)) + (y * (y0 - Margin)) + (pull * (z0 - Margin)), pull, x);
        var w = x1 - x0 + (2 * Margin);
        var h = y1 - y0 + (2 * Margin);
        using var rect = Modeling.Face(basePlane, [[
            ProfileSegment.Line(0, 0, w, 0), ProfileSegment.Line(w, 0, w, h), ProfileSegment.Line(w, h, 0, h), ProfileSegment.Line(0, h, 0, 0),
        ]]);
        using var blockOp = Modeling.Extrude(rect, pull * (z1 - z0 + (2 * Margin)));
        var block = blockOp.Result;

        // The impression: the part, grown by the shrinkage about its centre.
        var center = (x * ((x0 + x1) / 2)) + (y * ((y0 + y1) / 2)) + (pull * ((z0 + z1) / 2));
        foreach (var part in parts)
        {
            using var grown = Modeling.Transform(part.Shape, Transform3.Scale(center, Shrinkage));
            using var cut = Modeling.Boolean(block, grown.Result, BooleanKind.Cut);
            block = cut.Result;
        }

        // Split at the parting plane.
        using var tool = Modeling.PlaneFace(new Plane3(pull * parting, pull, x), (block.Bounds.Diagonal * 2) + 10);
        using var split = Modeling.Split(block, tool);
        var whole = split.Result;
        var core = new List<Shape>();
        var cavity = new List<Shape>();
        for (var i = 1; i <= whole.SolidCount; i++)
        {
            var piece = whole.Sub(TopoKind.Solid, i);
            (Vec3.Dot(piece.MassProperties.CenterOfMass, pull) > parting ? cavity : core).Add(piece);
        }

        if (core.Count == 0 || cavity.Count == 0)
        {
            throw new FeatureException("The parting plane must split the block in two.");
        }

        foreach (var (pieces, name) in new[] { (core, "Core"), (cavity, "Cavity") })
        {
            var shape = pieces.Count == 1 ? pieces[0] : Modeling.Compound(pieces);
            var names = Naming.Finish(shape, new string?[shape.FaceCount], $"{Id}:{name}");
            ctx.Bodies.Add(new Body(shape, names, name, name == "Core" ? 0x7C8DA6u : 0xB08D57u));
        }
    }

    private static (double Min, double Max) Extent(Shape s, Vec3 axis)
    {
        var b = s.Bounds;
        double lo = double.MaxValue, hi = double.MinValue;
        for (var k = 0; k < 8; k++)
        {
            var p = new Vec3((k & 1) == 0 ? b.Min.X : b.Max.X, (k & 2) == 0 ? b.Min.Y : b.Max.Y, (k & 4) == 0 ? b.Min.Z : b.Max.Z);
            var d = Vec3.Dot(p, axis);
            lo = Math.Min(lo, d);
            hi = Math.Max(hi, d);
        }

        return (lo, hi);
    }
}

/// <summary>Parting line analysis: edges where faces change from facing the pull direction to facing away.</summary>
public static class PartingLines
{
    /// <summary>1-based edge indices of a body on its parting line for a pull direction.</summary>
    public static IReadOnlyList<int> Find(Body body, Vec3 pull)
    {
        ArgumentNullException.ThrowIfNull(body);
        var dir = pull.Normalized();
        var edges = new List<int>();
        for (var e = 1; e <= body.EdgeCount; e++)
        {
            var faces = body.Shape.FacesOfEdge(e);
            if (faces.Length != 2)
            {
                continue;
            }

            var mid = body.Edge(e).Midpoint;
            var sides = faces.Select(f => body.Shape.ProjectOnFace(f, mid) is { } hit ? Math.Sign(Math.Round(Vec3.Dot(hit.Normal, dir), 6)) : 0).ToArray();
            if ((sides[0] > 0 && sides[1] <= 0) || (sides[1] > 0 && sides[0] <= 0))
            {
                edges.Add(e);
            }
        }

        return edges;
    }
}

/// <summary>
/// Replace Face: moves a flat face of a solid onto a surface (a face of a surface body) or a plane,
/// growing the solid out to it or trimming it back, whichever the surface needs.
/// </summary>
public sealed class ReplaceFaceFeature : Feature
{
    public override string DefaultName => "Replace Face";

    public TopoRef? Face { get; set; }

    /// <summary>A face of the replacement surface body; when null, <see cref="Plane"/> replaces.</summary>
    public TopoRef? Surface { get; set; }

    public PlaneRef Plane { get; set; } = PlaneRef.Top;

    public override IEnumerable<string> Dependencies => Plane.PlaneId is { } id && id is not ("front" or "top" or "right") ? [id] : [];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var (body, index) = ctx.ResolveFace(Face ?? throw new FeatureException("Select the face to replace."));
        var facePlane = body.Shape.FacePlane(index) ?? throw new FeatureException("Replace Face works on flat faces.");
        var surfaceBody = Surface is { } s ? ctx.ResolveFace(s).Body : null;
        var plane = surfaceBody is null ? ctx.ResolvePlane(Plane) : (Plane3?)null;
        var size = (body.Shape.Bounds.Diagonal * 4) + 20;
        using var planeTool = plane is { } p ? Modeling.PlaneFace(p, size) : null;
        var tool = planeTool ?? surfaceBody!.Shape;
        double Side(Vec3 point) => plane is { } pl ? pl.SignedDistance(point) : CutWithSurfaceFeature.SideOfSurface(surfaceBody!.Shape, point);

        // The kept side of the new surface is the one behind it as seen from the face's outside:
        // step back (against the face's normal) from where the face's centre meets the surface.
        var c = body.Face(index).Centroid;
        Vec3 onSurface;
        if (plane is { } pp)
        {
            var along = Vec3.Dot(facePlane.Normal, pp.Normal);
            onSurface = Math.Abs(along) > 1e-9 ? c + (facePlane.Normal * (-pp.SignedDistance(c) / along)) : c;
        }
        else
        {
            onSurface = Enumerable.Range(1, surfaceBody!.Shape.FaceCount)
                .Select(f => surfaceBody.Shape.ProjectOnFace(f, c))
                .OfType<(Vec3 Point, Vec3 Normal, double Distance)>()
                .MinBy(x => x.Distance).Point;
        }

        var bodySide = Math.Sign(Side(onSurface - (facePlane.Normal * 1e-3)));
        if (bodySide == 0)
        {
            throw new FeatureException("The replacement surface runs along the face; move it off.");
        }

        // Grow: the face swept outward, clipped at the surface, joined on.
        Shape grown;
        using (var face = body.Shape.Sub(TopoKind.Face, index))
        using (var prism = Modeling.Extrude(face, facePlane.Normal * size))
        using (var split = Modeling.Split(prism.Result, tool))
        {
            var pieces = new List<Shape>();
            for (var i = 1; i <= split.Result.SolidCount; i++)
            {
                var piece = split.Result.Sub(TopoKind.Solid, i);
                if (Math.Sign(Side(piece.MassProperties.CenterOfMass)) == bodySide)
                {
                    pieces.Add(piece);
                }
                else
                {
                    piece.Dispose();
                }
            }

            grown = body.Shape;
            foreach (var piece in pieces)
            {
                using var fuse = Modeling.Boolean(grown, piece, BooleanKind.Fuse);
                grown = fuse.Result;
                piece.Dispose();
            }
        }

        // Trim: anything past the surface on the face's side goes.
        using var cut = Modeling.Split(grown, tool);
        var kept = new List<Shape>();
        for (var i = 1; i <= cut.Result.SolidCount; i++)
        {
            var piece = cut.Result.Sub(TopoKind.Solid, i);
            if (Math.Sign(Side(piece.MassProperties.CenterOfMass)) == bodySide)
            {
                kept.Add(piece);
            }
            else
            {
                piece.Dispose();
            }
        }

        if (kept.Count == 0)
        {
            throw new FeatureException("The surface leaves nothing of the body.");
        }

        var result = kept[0];
        foreach (var more in kept.Skip(1))
        {
            using var fuse = Modeling.Boolean(result, more, BooleanKind.Fuse);
            result = fuse.Result;
        }

        var names = Naming.Finish(result, new string?[result.FaceCount], Id);
        ctx.Bodies[ctx.Bodies.IndexOf(body)] = new Body(result, names, body.Name, body.Color);
    }
}

