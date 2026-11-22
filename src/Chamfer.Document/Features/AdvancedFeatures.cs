using Chamfer.Document.Profiles;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>Sweeps a sketch profile along a path sketch.</summary>
public sealed class SweepFeature : Feature
{
    public override string DefaultName => IsCut ? "Cut-Sweep" : "Sweep";

    public string ProfileSketchId { get; set; } = string.Empty;

    public string PathSketchId { get; set; } = string.Empty;

    public bool IsCut { get; set; }

    public bool Merge { get; set; } = true;

    public override IEnumerable<string> Dependencies => [ProfileSketchId, PathSketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var profile = ctx.Sketch(ProfileSketchId);
        var path = ctx.Sketch(PathSketchId);
        using var wire = AdvancedShapes.Wire(path, "The path must be one connected chain of lines and arcs, or a circle.");

        var pieces = new List<(Shape, string[])>();
        foreach (var region in ProfileShapes.Regions(profile))
        {
            using var face = ProfileShapes.Face(profile.Plane, region, out var entityOfEdge);
            using var op = Run(() => Modeling.Sweep(face, wire), "The profile can't be swept along this path. Check that the path doesn't make the profile self-intersect.");
            var solid = op.Result;
            pieces.Add((solid, ProfileShapes.NameSweep(op, solid, face, entityOfEdge, Id, "sweep", region.Key)));
        }

        AdvancedShapes.Apply(ctx, Id, pieces, IsCut, Merge);
    }

    internal static Operation Run(Func<Operation> build, string message)
    {
        try
        {
            var op = build();
            if (op.Result.SolidCount == 0 || !op.Result.IsValid)
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

/// <summary>Lofts through the closed profiles of two or more sketches, in order.</summary>
public sealed class LoftFeature : Feature
{
    public override string DefaultName => IsCut ? "Cut-Loft" : "Loft";

    public List<string> SectionSketchIds { get; set; } = [];

    public bool IsCut { get; set; }

    public bool Merge { get; set; } = true;

    /// <summary>Straight (ruled) faces between sections instead of smooth ones.</summary>
    public bool Ruled { get; set; }

    public override IEnumerable<string> Dependencies => SectionSketchIds;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (SectionSketchIds.Count < 2)
        {
            throw new FeatureException("Select at least two profile sketches.");
        }

        var faces = new List<Shape>();
        Dictionary<int, int>? firstMap = null;
        var key = 0;
        try
        {
            foreach (var id in SectionSketchIds)
            {
                var sketch = ctx.Sketch(id);
                var regions = ProfileShapes.Regions(sketch);
                var region = regions.OrderByDescending(r => Math.Abs(r.Area)).First();
                faces.Add(ProfileShapes.Face(sketch.Plane, region with { Holes = [] }, out var map));
                if (firstMap is null)
                {
                    firstMap = map;
                    key = region.Key;
                }
            }

            using var op = SweepFeature.Run(() => Modeling.Loft(faces, solid: true, ruled: Ruled),
                "The loft can't be built. Profiles should be similar shapes, in order, and not intersect.");
            var solid = op.Result;
            var names = ProfileShapes.NameSweep(op, solid, faces[0], firstMap!, Id, "loft", key);
            AdvancedShapes.Apply(ctx, Id, [(solid, names)], IsCut, Merge);
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

/// <summary>A rib: an open sketch profile thickened and grown into the part until it meets it.</summary>
public sealed class RibFeature : Feature
{
    public override string DefaultName => "Rib";

    public string SketchId { get; set; } = string.Empty;

    public double Thickness { get; set; } = 2;

    /// <summary>Thickness on both sides of the sketch plane (otherwise one side).</summary>
    public bool BothSides { get; set; } = true;

    /// <summary>For one-sided ribs, grow against the sketch normal.</summary>
    public bool FlipSide { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Thickness <= 0)
        {
            throw new FeatureException("The rib thickness must be greater than zero.");
        }

        var sketch = ctx.Sketch(SketchId);
        var chain = ProfileBuilder.Chain(sketch.Sketch) ?? throw new FeatureException("A rib profile must be one connected chain of lines and arcs.");
        var body = ctx.Bodies.FirstOrDefault() ?? throw new FeatureException("A rib needs a body to grow into.");
        var (t1, t2) = BothSides ? (Thickness / 2, Thickness / 2) : FlipSide ? (0.0, Thickness) : (Thickness, 0.0);
        var baseVolume = body.Shape.MassProperties.Volume;

        // The material side depends on the profile's direction; build both and keep the smaller rib,
        // which is the one bounded by the part's walls.
        Operation? best = null;
        var bestAdded = double.PositiveInfinity;
        foreach (var segments in new[] { chain.Segments, ProfileBuilder.Reverse(chain.Segments) })
        {
            using var wire = Modeling.Wire(sketch.Plane, [.. segments.Select(ProfileShapes.ToKernel)]);
            try
            {
                var candidate = Modeling.Rib(body.Shape, wire, sketch.Plane.Origin, sketch.Plane.Normal, t1, t2);
                var added = candidate.Result.IsValid ? candidate.Result.MassProperties.Volume - baseVolume : double.PositiveInfinity;
                if (added > 1e-9 && added < bestAdded)
                {
                    best?.Dispose();
                    best = candidate;
                    bestAdded = added;
                }
                else
                {
                    candidate.Dispose();
                }
            }
            catch (KernelException)
            {
            }
        }

        using var op = best ?? throw new FeatureException("The rib can't be built. The profile must lie between walls of the part it can grow into.");
        ctx.Replace(body, op, "rib");
    }
}

internal static class AdvancedShapes
{
    /// <summary>A sketch's single chain of curves as a 3D wire.</summary>
    public static Shape Wire(PlacedSketch sketch, string message)
    {
        var chain = ProfileBuilder.Chain(sketch.Sketch) ?? throw new FeatureException(message);
        return Modeling.Wire(sketch.Plane, [.. chain.Segments.Select(ProfileShapes.ToKernel)]);
    }

    /// <summary>Adds or removes the combined tool, and records it for patterns.</summary>
    public static void Apply(RebuildContext ctx, string id, List<(Shape, string[])> pieces, bool isCut, bool merge)
    {
        var (tool, names) = ProfileShapes.Combine(pieces);
        ctx.Tools[id] = new FeatureTool(tool, names, isCut);
        if (isCut)
        {
            ctx.RemoveMaterial(tool, names);
        }
        else
        {
            ctx.AddMaterial(tool, names, merge);
        }
    }

    /// <summary>
    /// Clips a solid swept along <paramref name="direction"/> at a face: the
    /// part between the profile and the face is kept.
    /// </summary>
    public static (Shape Shape, string[] Names) ClipAtFace(Shape solid, string[] names, Body body, int face, double offset, Vec3 direction, Vec3 start, string featureId)
    {
        Shape tool;
        if (body.Shape.FacePlane(face) is { } plane)
        {
            var shifted = new Plane3(plane.Origin - (direction * offset), plane.Normal, plane.XDirection);
            tool = Modeling.PlaneFace(shifted, (solid.Bounds.Diagonal * 2) + 10);
        }
        else
        {
            if (offset != 0)
            {
                throw new FeatureException("Offset From Surface needs a planar face.");
            }

            tool = body.Shape.Sub(TopoKind.Face, face);
        }

        using (tool)
        {
            using var split = Modeling.Split(solid, tool);
            var whole = split.Result;
            var allNames = Naming.Transfer(split, whole, [new Naming.Input(solid, names)], featureId);
            if (whole.SolidCount < 2)
            {
                throw new FeatureException("The extrusion doesn't reach the selected face.");
            }

            // Keep the piece nearest the profile.
            Shape? best = null;
            var bestDistance = double.PositiveInfinity;
            for (var i = 1; i <= whole.SolidCount; i++)
            {
                var piece = whole.Sub(TopoKind.Solid, i);
                var along = Vec3.Dot(piece.MassProperties.CenterOfMass - start, direction);
                if (along < bestDistance)
                {
                    best?.Dispose();
                    best = piece;
                    bestDistance = along;
                }
                else
                {
                    piece.Dispose();
                }
            }

            var keep = best!;
            var keptNames = new string[keep.FaceCount];
            for (var i = 1; i <= keep.FaceCount; i++)
            {
                using var f = keep.Sub(TopoKind.Face, i);
                var index = whole.IndexOf(TopoKind.Face, f);
                keptNames[i - 1] = index > 0 ? allNames[index - 1] : $"{featureId}:face";
            }

            return (keep, keptNames);
        }
    }
}
