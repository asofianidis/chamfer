using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching.Entities;

namespace Chamfer.Document.Features;

/// <summary>Revolved boss or cut: a sketch's closed contours swept around an axis line.</summary>
public sealed class RevolveFeature : Feature
{
    public override string DefaultName => IsCut ? "Cut-Revolve" : "Revolve";

    public string SketchId { get; set; } = string.Empty;

    /// <summary>The sketch line to revolve about; 0 picks the sketch's first centerline.</summary>
    public int AxisEntityId { get; set; }

    /// <summary>An axis outside the sketch (reference axis, model edge, cylinder); overrides the sketch line.</summary>
    public AxisRef? Axis { get; set; }

    /// <summary>Sweep angle in degrees.</summary>
    public double Angle { get; set; } = 360;

    /// <summary>Split the angle evenly on both sides of the sketch plane.</summary>
    public bool MidPlane { get; set; }

    public bool Reverse { get; set; }

    public bool IsCut { get; set; }

    public bool Merge { get; set; } = true;

    public override IEnumerable<string> Dependencies =>
        new[] { SketchId, Axis?.AxisId, Axis?.SketchId }.OfType<string>().Distinct();

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sketch = ctx.Sketch(SketchId);
        if (Angle <= 0 || Angle > 360)
        {
            throw new FeatureException("The angle must be between 0 and 360 degrees.");
        }

        var plane = sketch.Plane;
        Vec3 a, axis;
        if (Axis is { } axisRef)
        {
            (a, axis) = ctx.ResolveAxis(axisRef);
            axis = axis.Normalized();
            if (Math.Abs(Vec3.Dot(axis, plane.Normal)) > 1e-6)
            {
                throw new FeatureException("The axis must run along the sketch plane (square to its normal).");
            }
        }
        else
        {
            var axisLine = AxisEntityId != 0
                ? sketch.Sketch.FindEntity(AxisEntityId) as SketchLine
                : sketch.Sketch.Entities.OfType<SketchLine>().FirstOrDefault(l => l.IsConstruction);
            if (axisLine is null)
            {
                throw new FeatureException("Select a line or centerline to revolve about.");
            }

            a = plane.ToWorld(axisLine.Start.Position.X, axisLine.Start.Position.Y);
            var b = plane.ToWorld(axisLine.End.Position.X, axisLine.End.Position.Y);
            axis = (b - a).Normalized();
        }

        if (Reverse)
        {
            axis = -axis;
        }

        var angle = Angle * Math.PI / 180;
        var startPlane = plane;
        if (MidPlane && Angle < 360)
        {
            var r = Transform3.Rotation(a, axis, -angle / 2);
            startPlane = new Plane3(r.Apply(plane.Origin), r.ApplyVector(plane.Normal), r.ApplyVector(plane.XDirection));
        }

        var regions = ProfileShapes.Regions(sketch);
        var pieces = new List<(Shape, string[])>();
        foreach (var region in regions)
        {
            using var face = ProfileShapes.Face(startPlane, region, out var entityOfEdge);
            using var op = Modeling.Revolve(face, a, axis, Math.Min(angle, 2 * Math.PI));
            var solid = op.Result;
            pieces.Add((solid, ProfileShapes.NameSweep(op, solid, face, entityOfEdge, Id, "rev", region.Key)));
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
