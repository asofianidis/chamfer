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
    private static double SideOfSurface(Shape surface, Vec3 p)
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
