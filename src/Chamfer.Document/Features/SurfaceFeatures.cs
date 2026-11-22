using Chamfer.Document.Profiles;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>Shared helpers for surface features.</summary>
internal static class Surfaces
{
    /// <summary>The sketch's curve chains as wires in 3D.</summary>
    public static List<Shape> Wires(PlacedSketch sketch)
    {
        var chains = ProfileBuilder.Chains(sketch.Sketch) ?? throw new FeatureException("The sketch's curves branch; surfaces need simple chains.");
        if (chains.Count == 0)
        {
            throw new FeatureException("The sketch has no curves.");
        }

        return [.. chains.Select(c => Modeling.Wire(sketch.Plane, [.. c.Segments.SelectMany(ProfileShapes.ToKernelAll)]))];
    }

    /// <summary>Adds a new surface body with positional face names.</summary>
    public static void AddBody(RebuildContext ctx, Shape shape, string featureId)
    {
        var names = Naming.Finish(shape, new string?[shape.FaceCount], featureId);
        ctx.Bodies.Add(new Body(shape, names, ctx.NewBodyName()));
    }

    public static Shape Combine(List<Shape> shapes) => shapes.Count == 1 ? shapes[0] : Modeling.Compound(shapes);
}

/// <summary>A surface swept straight out of the sketch's curves (open or closed).</summary>
public sealed class SurfaceExtrudeFeature : Feature
{
    public override string DefaultName => "Surface-Extrude";

    public string SketchId { get; set; } = string.Empty;

    public double Depth { get; set; } = 10;

    public bool MidPlane { get; set; }

    public bool Reverse { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Depth <= 0)
        {
            throw new FeatureException("Depth must be greater than zero.");
        }

        var sketch = ctx.Sketch(SketchId);
        var dir = sketch.Plane.Normal * (Reverse ? -1 : 1);
        var faces = new List<Shape>();
        foreach (var wire in Surfaces.Wires(sketch))
        {
            using (wire)
            {
                if (MidPlane)
                {
                    using var shift = Modeling.Transform(wire, Transform3.Translation(dir * (-Depth / 2)));
                    using var op = Modeling.Extrude(shift.Result, dir * Depth);
                    faces.Add(op.Result);
                }
                else
                {
                    using var op = Modeling.Extrude(wire, dir * Depth);
                    faces.Add(op.Result);
                }
            }
        }

        Surfaces.AddBody(ctx, Surfaces.Combine(faces), Id);
    }
}

/// <summary>A surface of revolution from the sketch's curves about its first centerline.</summary>
public sealed class SurfaceRevolveFeature : Feature
{
    public override string DefaultName => "Surface-Revolve";

    public string SketchId { get; set; } = string.Empty;

    public double Angle { get; set; } = 360;

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sketch = ctx.Sketch(SketchId);
        var axis = sketch.Sketch.Entities.OfType<Sketching.Entities.SketchLine>().FirstOrDefault(l => l.IsConstruction)
            ?? throw new FeatureException("Add a centerline to revolve about.");
        var a = sketch.Plane.ToWorld(axis.Start.Position.X, axis.Start.Position.Y);
        var b = sketch.Plane.ToWorld(axis.End.Position.X, axis.End.Position.Y);
        var faces = new List<Shape>();

        // The axis itself is construction geometry, so it isn't part of any chain.
        foreach (var wire in Surfaces.Wires(sketch))
        {
            using (wire)
            {
                using var op = Modeling.Revolve(wire, a, (b - a).Normalized(), Math.Clamp(Angle, 1e-3, 360) * Math.PI / 180);
                faces.Add(op.Result);
            }
        }

        Surfaces.AddBody(ctx, Surfaces.Combine(faces), Id);
    }
}

/// <summary>Flat surfaces filling the sketch's closed regions.</summary>
public sealed class PlanarSurfaceFeature : Feature
{
    public override string DefaultName => "Surface-Plane";

    public string SketchId { get; set; } = string.Empty;

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sketch = ctx.Sketch(SketchId);
        var faces = ProfileShapes.Regions(sketch).Select(r => ProfileShapes.Face(sketch.Plane, r, out _)).ToList();
        Surfaces.AddBody(ctx, Surfaces.Combine(faces), Id);
    }
}

/// <summary>A smooth patch filling a closed sketch boundary.</summary>
public sealed class FilledSurfaceFeature : Feature
{
    public override string DefaultName => "Surface-Fill";

    public string SketchId { get; set; } = string.Empty;

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var patches = new List<Shape>();
        foreach (var w in Surfaces.Wires(ctx.Sketch(SketchId)))
        {
            using (w)
            {
                try
                {
                    patches.Add(Modeling.Fill(w));
                }
                catch (KernelException ex)
                {
                    throw new FeatureException("The boundary can't be filled. It must be closed.", ex);
                }
            }
        }

        Surfaces.AddBody(ctx, Surfaces.Combine(patches), Id);
    }
}

/// <summary>Copies of faces offset along their normals, as a new surface body.</summary>
public sealed class OffsetSurfaceFeature : Feature
{
    public override string DefaultName => "Surface-Offset";

    public List<TopoRef> Faces { get; set; } = [];

    public double Distance { get; set; } = 2;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Faces.Count == 0)
        {
            throw new FeatureException("Select faces to offset.");
        }

        var pieces = new List<Shape>();
        foreach (var f in Faces)
        {
            var (body, index) = ctx.ResolveFace(f);
            var face = body.Shape.Sub(TopoKind.Face, index);
            if (Math.Abs(Distance) < 1e-12)
            {
                pieces.Add(face);
                continue;
            }

            using (face)
            {
                try
                {
                    using var op = Modeling.OffsetShape(face, Distance);
                    pieces.Add(op.Result);
                }
                catch (KernelException ex)
                {
                    throw new FeatureException("A face can't be offset that far.", ex);
                }
            }
        }

        using var sewn = Modeling.Sew(pieces);
        Surfaces.AddBody(ctx, sewn.Result, Id);
    }
}

/// <summary>Sews surface bodies together along shared edges; a closed result becomes a solid.</summary>
public sealed class KnitFeature : Feature
{
    public override string DefaultName => "Surface-Knit";

    public List<string> Bodies { get; set; } = [];

    public double Tolerance { get; set; } = 0.01;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var bodies = Bodies.Select(ctx.FindBody).Distinct().ToList();
        if (bodies.Count == 0)
        {
            throw new FeatureException("Select the surfaces to knit.");
        }

        using var op = Modeling.Sew([.. bodies.Select(b => b.Shape)], Tolerance);
        var result = op.Result;
        var names = Naming.Finish(result, new string?[result.FaceCount], Id);
        var index = ctx.Bodies.IndexOf(bodies[0]);
        foreach (var b in bodies)
        {
            ctx.Bodies.Remove(b);
        }

        ctx.Bodies.Insert(Math.Min(index, ctx.Bodies.Count), new Body(result, names, bodies[0].Name, bodies[0].Color));
        if (result.SolidCount == 0)
        {
            ctx.Warn("The knitted surfaces don't enclose a volume, so the result is a surface.");
        }
    }
}

/// <summary>Turns a surface body into a solid of the given thickness.</summary>
public sealed class ThickenFeature : Feature
{
    public override string DefaultName => IsCut ? "Thicken-Cut" : "Thicken";

    public string Body { get; set; } = string.Empty;

    /// <summary>Remove the thickened volume from the other bodies instead of adding it (Thickened Cut).</summary>
    public bool IsCut { get; set; }

    public double Thickness { get; set; } = 1;

    /// <summary>Thicken against the surface normal.</summary>
    public bool Reverse { get; set; }

    /// <summary>Fuse the result into the other bodies it touches.</summary>
    public bool Merge { get; set; } = true;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var surface = ctx.FindBody(Body);
        if (Thickness <= 0)
        {
            throw new FeatureException("The thickness must be greater than zero.");
        }

        Operation op;
        try
        {
            op = Modeling.Thicken(surface.Shape, Reverse ? -Thickness : Thickness);
        }
        catch (KernelException ex)
        {
            throw new FeatureException("The surface can't be thickened that much.", ex);
        }

        using (op)
        {
            var solid = op.Result;
            ctx.Bodies.Remove(surface);
            var names = Naming.Finish(solid, new string?[solid.FaceCount], Id);
            if (IsCut)
            {
                ctx.RemoveMaterial(solid, names);
            }
            else
            {
                ctx.AddMaterial(solid, names, Merge);
            }
        }
    }
}

/// <summary>How a ruled surface leaves its edges.</summary>
public enum RuledDirection
{
    /// <summary>Square to a flat face next to each edge.</summary>
    NormalToFace,

    /// <summary>Along one direction for every edge.</summary>
    AlongDirection,
}

/// <summary>
/// A ruled surface: a strip of the given width from each selected model edge, square to a face
/// beside it or along a direction (for parting and shut-off surfaces, flanges to build on).
/// </summary>
public sealed class RuledSurfaceFeature : Feature
{
    public override string DefaultName => "Ruled-Surface";

    public List<TopoRef> Edges { get; set; } = [];

    public double Distance { get; set; } = 5;

    public RuledDirection Type { get; set; }

    /// <summary>For <see cref="RuledDirection.AlongDirection"/>.</summary>
    public AxisRef Direction { get; set; } = AxisRef.Z;

    /// <summary>Go the other way.</summary>
    public bool Reverse { get; set; }

    /// <summary>For <see cref="RuledDirection.NormalToFace"/>: use the other face beside each edge.</summary>
    public bool OtherFace { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Edges.Count == 0)
        {
            throw new FeatureException("Select the edges to rule from.");
        }

        if (Distance <= 0)
        {
            throw new FeatureException("The distance must be greater than zero.");
        }

        var along = Type == RuledDirection.AlongDirection ? ctx.ResolveAxis(Direction).Direction.Normalized() : Vec3.Zero;
        var strips = new List<Shape>();
        try
        {
            foreach (var edgeRef in Edges)
            {
                var (body, index) = ctx.ResolveEdge(edgeRef);
                var dir = along;
                if (Type == RuledDirection.NormalToFace)
                {
                    var flat = body.Shape.FacesOfEdge(index).Select(f => body.Shape.FacePlane(f)).OfType<Plane3>().ToList();
                    if (flat.Count == 0)
                    {
                        throw new FeatureException("A selected edge has no flat face beside it; rule it along a direction instead.");
                    }

                    dir = flat[OtherFace && flat.Count > 1 ? 1 : 0].Normal;
                }

                using var edge = body.Shape.Sub(TopoKind.Edge, index);
                using var op = Modeling.Extrude(edge, dir * (Reverse ? -Distance : Distance));
                strips.Add(op.Result);
            }

            using var sewn = strips.Count == 1 ? null : Modeling.Sew(strips);
            var shape = sewn is null ? strips[0] : sewn.Result;
            if (sewn is null)
            {
                strips.Clear(); // the body owns it now
            }

            Surfaces.AddBody(ctx, shape, Id);
        }
        finally
        {
            foreach (var s in strips)
            {
                s.Dispose();
            }
        }
    }
}

/// <summary>
/// Split Line: divides the model's faces along a sketch's curves (lines, arcs, splines, closed or
/// open), without changing the solid, so the pieces can be coloured, drafted or used as references.
/// The curves are projected along the sketch normal onto faces within <see cref="Reach"/> of the sketch.
/// </summary>
public sealed class SplitLineFeature : Feature
{
    public override string DefaultName => "Split Line";

    public string SketchId { get; set; } = string.Empty;

    /// <summary>How far either side of the sketch plane faces are split (default: just the face the sketch is on).</summary>
    public double Reach { get; set; } = 0.05;

    /// <summary>Project right through the model, splitting every face in line with the curves.</summary>
    public bool ThroughAll { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sketch = ctx.Sketch(SketchId);
        var reach = ThroughAll ? ctx.ThroughAllLength(sketch.Plane.Origin) : Reach;
        if (reach <= 0)
        {
            throw new FeatureException("The reach must be greater than zero.");
        }

        var n = sketch.Plane.Normal;
        var sheets = new List<Shape>();
        try
        {
            foreach (var w in Surfaces.Wires(sketch))
            {
                using (w)
                {
                    using var moved = Modeling.Transform(w, Transform3.Translation(n * -reach));
                    using var band = Modeling.Extrude(moved.Result, n * (2 * reach));
                    sheets.Add(band.Result);
                }
            }

            if (sheets.Count == 0)
            {
                throw new FeatureException("The sketch has no curves to split with.");
            }

            using var tool = sheets.Count == 1 ? null : Modeling.Compound(sheets);
            var cutter = tool ?? sheets[0];
            var touched = false;
            foreach (var body in ctx.Bodies.Where(b => !b.IsSurface && b.Shape.Bounds.Intersects(cutter.Bounds)).ToList())
            {
                Operation op;
                try
                {
                    op = Modeling.SplitLine(body.Shape, cutter);
                }
                catch (KernelException ex)
                {
                    throw new FeatureException($"The faces can't be split here: {ex.Message}");
                }

                using (op)
                {
                    if (op.Result.FaceCount > body.FaceCount)
                    {
                        ctx.Replace(body, op, "split");
                        touched = true;
                    }
                }
            }

            if (!touched)
            {
                ctx.Warn("The sketch doesn't cross any face; try Through all.");
            }
        }
        finally
        {
            foreach (var s in sheets)
            {
                s.Dispose();
            }
        }
    }
}

/// <summary>What a surface edit does to the chosen surface body's faces.</summary>
public enum SurfaceEdit
{
    /// <summary>Cut it with a plane (or a flat face) and keep one side.</summary>
    Trim,

    /// <summary>Fill its faces' holes back in (and grow curved faces to their full surface).</summary>
    Untrim,

    /// <summary>Grow its flat faces outward by a distance all round.</summary>
    Extend,
}

/// <summary>Trim, untrim or extend a surface body.</summary>
public sealed class SurfaceEditFeature : Feature
{
    public override string DefaultName => Operation switch
    {
        SurfaceEdit.Trim => "Surface-Trim",
        SurfaceEdit.Untrim => "Surface-Untrim",
        _ => "Surface-Extend",
    };

    public string Body { get; set; } = string.Empty;

    public SurfaceEdit Operation { get; set; }

    /// <summary>For trimming: the plane or flat face to cut with.</summary>
    public PlaneRef Tool { get; set; } = PlaneRef.Front;

    /// <summary>For trimming: keep the side the tool's normal points away from.</summary>
    public bool KeepOtherSide { get; set; }

    /// <summary>For extending.</summary>
    public double Distance { get; set; } = 5;

    public override IEnumerable<string> Dependencies => Tool.PlaneId is { } id && id is not ("front" or "top" or "right") ? [id] : [];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var body = ctx.FindBody(Body);
        if (!body.IsSurface)
        {
            throw new FeatureException("Pick a surface body.");
        }

        Shape result;
        switch (Operation)
        {
            case SurfaceEdit.Trim:
            {
                var plane = ctx.ResolvePlane(Tool);
                using var tool = Modeling.PlaneFace(plane, (body.Shape.Bounds.Diagonal * 2) + 10);
                using var split = Modeling.Split(body.Shape, tool);
                var keep = new List<Shape>();
                for (var i = 1; i <= split.Result.FaceCount; i++)
                {
                    var face = split.Result.Sub(TopoKind.Face, i);
                    var side = Vec3.Dot(split.Result.Face(i).Centroid - plane.Origin, plane.Normal);
                    if (KeepOtherSide ? side < 0 : side > 0)
                    {
                        keep.Add(face);
                    }
                    else
                    {
                        face.Dispose();
                    }
                }

                if (keep.Count == 0)
                {
                    throw new FeatureException("Nothing of the surface is on that side of the plane.");
                }

                using var sewn = Modeling.Sew(keep);
                result = sewn.Result;
                foreach (var k in keep)
                {
                    k.Dispose();
                }

                break;
            }

            case SurfaceEdit.Untrim:
            case SurfaceEdit.Extend:
            {
                var faces = new List<Shape>();
                try
                {
                    for (var i = 1; i <= body.Shape.FaceCount; i++)
                    {
                        using var face = body.Shape.Sub(TopoKind.Face, i);
                        faces.Add(Operation == SurfaceEdit.Untrim ? Modeling.UntrimFace(face, natural: true) : Modeling.ExtendFace(face, Distance));
                    }
                }
                catch (KernelException ex)
                {
                    throw new FeatureException(ex.Message);
                }

                result = faces.Count == 1 ? faces[0] : Modeling.Compound(faces);
                break;
            }

            default:
                throw new FeatureException("Unknown surface edit.");
        }

        var names = Naming.Finish(result, new string?[result.FaceCount], Id);
        ctx.Bodies[ctx.Bodies.IndexOf(body)] = new Body(result, names, body.Name, body.Color);
    }
}

