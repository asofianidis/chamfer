using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

public enum EndCondition
{
    Blind,
    ThroughAll,
    MidPlane,
}

/// <summary>Extruded boss or cut from a sketch's closed contours.</summary>
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

    /// <summary>Fuse a boss into the bodies it touches instead of making a new body.</summary>
    public bool Merge { get; set; } = true;

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sketch = ctx.Sketch(SketchId);
        var regions = ProfileShapes.Regions(sketch);
        var plane = sketch.Plane;
        var dir = plane.Normal * ((IsCut ? -1 : 1) * (Reverse ? -1 : 1));
        var through = ctx.ThroughAllLength(plane.Origin);

        double Length(EndCondition c, double depth) => c switch
        {
            EndCondition.ThroughAll => through,
            _ => depth,
        };

        if ((EndCondition != EndCondition.ThroughAll && Depth <= 0) || (HasDirection2 && EndCondition2 == EndCondition.Blind && Depth2 <= 0))
        {
            throw new FeatureException("Depth must be greater than zero.");
        }

        double start, length;
        if (EndCondition == EndCondition.MidPlane)
        {
            start = -Depth / 2;
            length = Depth;
        }
        else
        {
            var d1 = Length(EndCondition, Depth);
            var d2 = HasDirection2 ? Length(EndCondition2, Depth2) : 0;
            start = -d2;
            length = d1 + d2;
        }

        start += StartOffset;
        var origin = plane.Origin + (dir * start);
        var startPlane = new Geometry.Plane3(origin, plane.Normal, plane.XDirection);

        var pieces = new List<(Shape, string[])>();
        foreach (var region in regions)
        {
            using var face = ProfileShapes.Face(startPlane, region, out var entityOfEdge);
            using var op = Modeling.Extrude(face, dir * length);
            var solid = op.Result;
            pieces.Add((solid, ProfileShapes.NameSweep(op, solid, face, entityOfEdge, Id, "side", region.Key)));
        }

        var (tool, names) = ProfileShapes.Combine(pieces);
        ctx.Tools[Id] = new FeatureTool(tool, names, IsCut);
        if (IsCut)
        {
            ctx.RemoveMaterial(tool, names);
        }
        else
        {
            ctx.AddMaterial(tool, names, Merge);
        }
    }
}
