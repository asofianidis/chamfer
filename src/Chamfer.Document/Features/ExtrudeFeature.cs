using Chamfer.Document.Profiles;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Operations;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Document.Features;

public enum EndCondition
{
    Blind,
    ThroughAll,
    MidPlane,
    UpToSurface,
    OffsetFromSurface,

    /// <summary>To the plane through a point (a reference point), parallel to the sketch.</summary>
    UpToVertex,
}

/// <summary>Extruded boss or cut from a sketch's closed contours (or a thin wall from any chain).</summary>
public sealed class ExtrudeFeature : Feature
{
    public override string DefaultName => IsCut ? "Cut-Extrude" : "Boss-Extrude";

    public string SketchId { get; set; } = string.Empty;

    public bool IsCut { get; set; }

    public EndCondition EndCondition { get; set; } = EndCondition.Blind;

    public double Depth { get; set; } = 10;

    /// <summary>Flip direction 1. Bosses go along the sketch normal, cuts against it.</summary>
    public bool Reverse { get; set; }

    /// <summary>Also extrude in the opposite direction.</summary>
    public bool HasDirection2 { get; set; }

    public EndCondition EndCondition2 { get; set; } = EndCondition.Blind;

    public double Depth2 { get; set; } = 10;

    /// <summary>Start the extrusion this far from the sketch plane, along direction 1.</summary>
    public double StartOffset { get; set; }

    /// <summary>Face to stop at, for <see cref="EndCondition.UpToSurface"/> and <see cref="EndCondition.OffsetFromSurface"/>.</summary>
    public TopoRef? UpToFace { get; set; }

    /// <summary>Reference point to stop at, for <see cref="EndCondition.UpToVertex"/>.</summary>
    public string? UpToPoint { get; set; }

    /// <summary>Distance short of <see cref="UpToFace"/> for <see cref="EndCondition.OffsetFromSurface"/>.</summary>
    public double SurfaceOffset { get; set; }

    /// <summary>Taper angle of the side walls in degrees (0 = straight).</summary>
    public double DraftAngle { get; set; }

    /// <summary>Taper outward instead of inward.</summary>
    public bool DraftOutward { get; set; }

    /// <summary>Make a wall of <see cref="ThinThickness"/> from the sketch curves instead of filling the profile.</summary>
    public bool IsThin { get; set; }

    public double ThinThickness { get; set; } = 1;

    /// <summary>Put a thin wall on the other side of the sketch curves.</summary>
    public bool ThinReverse { get; set; }

    /// <summary>Fuse a boss into the bodies it touches instead of making a new body.</summary>
    public bool Merge { get; set; } = true;

    public override IEnumerable<string> Dependencies => UpToPoint is { } point ? [SketchId, point] : [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sketch = ctx.Sketch(SketchId);
        var regions = IsThin ? ThinRegions(sketch) : ProfileShapes.Regions(sketch);
        var plane = sketch.Plane;
        var dir = plane.Normal * ((IsCut ? -1 : 1) * (Reverse ? -1 : 1));
        var through = ctx.ThroughAllLength(plane.Origin);
        var upTo = EndCondition is EndCondition.UpToSurface or EndCondition.OffsetFromSurface;

        if (EndCondition is EndCondition.Blind or EndCondition.MidPlane && Depth <= 0)
        {
            throw new FeatureException("Depth must be greater than zero.");
        }

        if (HasDirection2 && EndCondition2 == EndCondition.Blind && Depth2 <= 0)
        {
            throw new FeatureException("Depth 2 must be greater than zero.");
        }

        (Body Body, int Face)? target = null;
        if (upTo)
        {
            if (UpToFace is null)
            {
                throw new FeatureException("Select the face to extrude up to.");
            }

            target = ctx.ResolveFace(UpToFace);
        }

        double start, length;
        if (EndCondition == EndCondition.MidPlane)
        {
            start = -Depth / 2;
            length = Depth;
        }
        else
        {
            var d1 = EndCondition switch
            {
                EndCondition.ThroughAll or EndCondition.UpToSurface or EndCondition.OffsetFromSurface => through,
                EndCondition.UpToVertex => VertexReach(ctx, plane, dir),
                _ => Depth,
            };
            var d2 = HasDirection2 ? (EndCondition2 == EndCondition.ThroughAll ? through : Depth2) : 0;
            start = -d2;
            length = d1 + d2;
        }

        start += StartOffset;
        var startPlane = new Plane3(plane.Origin + (dir * start), plane.Normal, plane.XDirection);

        // A face parallel to the sketch plane is reached with a plain blind extrusion.
        if (target is { } t && t.Body.Shape.FacePlane(t.Face) is { } targetPlane && Math.Abs(Math.Abs(Vec3.Dot(targetPlane.Normal, dir)) - 1) < 1e-9)
        {
            var reach = Vec3.Dot(targetPlane.Origin - plane.Origin, dir) - (EndCondition == EndCondition.OffsetFromSurface ? SurfaceOffset : 0) - StartOffset;
            if (reach <= 1e-9)
            {
                throw new FeatureException("The selected face is behind the sketch. Try reversing the direction.");
            }

            length = reach + (HasDirection2 ? -start + StartOffset : 0);
            target = null;
        }

        var pieces = new List<(Shape, string[])>();
        foreach (var region in regions)
        {
            using var face = ProfileShapes.Face(startPlane, region, out var entityOfEdge);
            using var op = Modeling.Extrude(face, dir * length);
            var solid = op.Result;
            var names = ProfileShapes.NameSweep(op, solid, face, entityOfEdge, Id, "side", region.Key);

            if (Math.Abs(DraftAngle) > 1e-9)
            {
                (solid, names) = Taper(op, solid, names, face, entityOfEdge, startPlane, dir);
            }

            if (target is { } tgt)
            {
                (solid, names) = AdvancedShapes.ClipAtFace(solid, names, tgt.Body, tgt.Face,
                    EndCondition == EndCondition.OffsetFromSurface ? SurfaceOffset : 0, dir, startPlane.Origin, Id);
            }

            pieces.Add((solid, names));
        }

        AdvancedShapes.Apply(ctx, Id, pieces, IsCut, Merge);
    }

    /// <summary>How far along the extrusion the up-to point's parallel plane is.</summary>
    private double VertexReach(RebuildContext ctx, Plane3 plane, Vec3 dir)
    {
        var point = ctx.ResolvePoint(UpToPoint ?? throw new FeatureException("Select a point to extrude up to."));
        var reach = Vec3.Dot(point - plane.Origin, dir) - StartOffset;
        return reach > 1e-9 ? reach : throw new FeatureException("The point is behind the sketch. Try reversing the direction.");
    }

    private (Shape, string[]) Taper(Operation prism, Shape solid, string[] names, Shape face, Dictionary<int, int> entityOfEdge, Plane3 startPlane, Vec3 dir)
    {
        var sides = entityOfEdge.Keys.SelectMany(e => prism.Generated(face, TopoKind.Edge, e, TopoKind.Face)).Distinct().ToList();
        var angle = DraftAngle * Math.PI / 180 * (DraftOutward ? -1 : 1);
        try
        {
            using var draft = Modeling.Draft(solid, sides, startPlane.Origin, dir, angle);
            var drafted = draft.Result;
            return (drafted, Naming.Transfer(draft, drafted, [new Naming.Input(solid, names)], Id, "draft"));
        }
        catch (KernelException ex)
        {
            throw new FeatureException("The draft angle can't be applied to this profile.", ex);
        }
    }

    /// <summary>Regions for a thin feature: a band of the given thickness along the sketch curves.</summary>
    private IReadOnlyList<ProfileRegion> ThinRegions(PlacedSketch sketch)
    {
        if (ThinThickness <= 0)
        {
            throw new FeatureException("The wall thickness must be greater than zero.");
        }

        var copy = SketchSerializer.Deserialize(SketchSerializer.Serialize(sketch.Sketch));
        var chain = ProfileBuilder.Chain(copy) ?? throw new FeatureException("A thin feature needs one connected chain of sketch curves.");
        var curves = chain.Segments.Select(s => copy.FindEntity(s.EntityId)!).ToList();
        try
        {
            var offsets = SketchOperations.Offset(copy, curves, ThinThickness, reverse: chain.Closed ? !ThinReverse : ThinReverse);
            if (!chain.Closed)
            {
                // Close the band with caps across both ends.
                var first = chain.Segments[0];
                var last = chain.Segments[^1];
                var ends = offsets.SelectMany(e => e.Points).Where(p => p.Owner is not null && p.Owner is not Sketching.Entities.SketchCircle)
                    .Where(p => !IsCenter(p)).Select(p => p.Position).ToList();
                copy.AddLine(first.Start, Nearest(ends, first.Start));
                copy.AddLine(last.End, Nearest(ends, last.End));
            }
        }
        catch (ArgumentException ex)
        {
            throw new FeatureException(ex.Message, ex);
        }

        foreach (var c in copy.Constraints.ToList())
        {
            copy.RemoveConstraint(c);
        }

        var regions = ProfileBuilder.Build(copy).Regions;
        return regions.Count == 0 ? throw new FeatureException("The thin wall couldn't be formed.") : regions;
    }

    private static bool IsCenter(Sketching.Entities.SketchPoint p) =>
        p.Owner is Sketching.Entities.SketchArc a && ReferenceEquals(a.Center, p);

    private static Vec2 Nearest(List<Vec2> points, Vec2 to) => points.OrderBy(p => Vec2.Distance(p, to)).First();
}
