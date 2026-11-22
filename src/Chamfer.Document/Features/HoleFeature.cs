using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching.Entities;

namespace Chamfer.Document.Features;

public enum HoleType
{
    Simple,
    Counterbore,
    Countersink,
    Tapped,
}

/// <summary>A standard fastener size with its hole dimensions in millimetres.</summary>
public sealed record HoleSize(
    string Name,
    double TapDrill,
    double Clearance,
    double CounterboreDiameter,
    double CounterboreDepth,
    double CountersinkDiameter);

/// <summary>ISO metric hole sizes for socket head and countersunk screws (ISO 273 normal fit).</summary>
public static class HoleStandards
{
    public static IReadOnlyList<HoleSize> IsoMetric { get; } =
    [
        new("M2", 1.6, 2.4, 4.4, 2.0, 4.4),
        new("M2.5", 2.05, 2.9, 5.5, 2.5, 5.5),
        new("M3", 2.5, 3.4, 6.5, 3.3, 6.5),
        new("M4", 3.3, 4.5, 8.0, 4.4, 8.6),
        new("M5", 4.2, 5.5, 10.0, 5.4, 10.4),
        new("M6", 5.0, 6.6, 11.0, 6.5, 12.4),
        new("M8", 6.8, 9.0, 15.0, 8.6, 16.4),
        new("M10", 8.5, 11.0, 18.0, 10.8, 20.4),
        new("M12", 10.2, 13.5, 20.0, 13.0, 24.4),
        new("M16", 14.0, 17.5, 26.0, 17.5, 32.4),
        new("M20", 17.5, 22.0, 33.0, 21.5, 40.4),
        new("M24", 21.0, 26.0, 40.0, 25.5, 48.4),
    ];

    public static HoleSize? Find(string name) => IsoMetric.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Hole Wizard-style holes at the points of a sketch placed on a face. Holes
/// go into the material, against the sketch plane's normal.
/// </summary>
public sealed class HoleFeature : Feature
{
    public override string DefaultName => "Hole";

    /// <summary>Sketch whose standalone points locate the holes.</summary>
    public string SketchId { get; set; } = string.Empty;

    public HoleType Type { get; set; } = HoleType.Simple;

    /// <summary>Standard size this hole was configured from (informational).</summary>
    public string? Size { get; set; }

    public double Diameter { get; set; } = 5;

    public EndCondition EndCondition { get; set; } = EndCondition.Blind;

    public double Depth { get; set; } = 10;

    /// <summary>Included drill point angle in degrees for blind holes (0 = flat bottom).</summary>
    public double DrillPointAngle { get; set; } = 118;

    public double CounterboreDiameter { get; set; } = 10;

    public double CounterboreDepth { get; set; } = 5;

    public double CountersinkDiameter { get; set; } = 10;

    /// <summary>Included countersink angle in degrees.</summary>
    public double CountersinkAngle { get; set; } = 90;

    /// <summary>Nominal thread diameter for tapped holes (shown as a cosmetic thread).</summary>
    public double ThreadDiameter { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sketch = ctx.Sketch(SketchId);
        var points = sketch.Sketch.Entities.OfType<SketchPoint>().Where(p => !p.IsConstruction).ToList();
        if (points.Count == 0)
        {
            throw new FeatureException("Add sketch points to locate the holes.");
        }

        if (Diameter <= 0)
        {
            throw new FeatureException("The hole diameter must be greater than zero.");
        }

        var axis = -sketch.Plane.Normal;
        var pieces = new List<(Shape, string[])>();
        for (var k = 0; k < points.Count; k++)
        {
            var p = points[k];
            var origin = sketch.Plane.ToWorld(p.Position.X, p.Position.Y);
            var depth = EndCondition == EndCondition.ThroughAll ? ctx.ThroughAllLength(origin) : Depth;
            var profile = Profile(depth);
            var u = sketch.Plane.XDirection;
            var frame = new Plane3(origin, Vec3.Cross(u, axis), u);
            var segments = new List<ProfileSegment>();
            for (var i = 0; i < profile.Count; i++)
            {
                var (r0, z0) = profile[i];
                var (r1, z1) = profile[(i + 1) % profile.Count];
                segments.Add(ProfileSegment.Line(r0, z0, r1, z1));
            }

            using var face = Modeling.Face(frame, [segments], out var map);
            var entityOfEdge = new Dictionary<int, int>();
            for (var i = 0; i < map.Length; i++)
            {
                entityOfEdge.TryAdd(map[i], i + 1);
            }

            using var op = Modeling.Revolve(face, origin, axis, 2 * Math.PI);
            var solid = op.Result;
            pieces.Add((solid, ProfileShapes.NameSweep(op, solid, face, entityOfEdge, Id, $"hole{p.Id}", p.Id)));
        }

        var (tool, names) = ProfileShapes.Combine(pieces);
        ctx.Tools[Id] = new FeatureTool(tool, names, IsCut: true);
        ctx.RemoveMaterial(tool, names);
    }

    /// <summary>Half cross-section as (radius, depth) points, depth measured into the material.</summary>
    private List<(double R, double Z)> Profile(double depth)
    {
        var r = Diameter / 2;
        var lead = -Math.Max(0.01, Diameter * 0.01);
        var pts = new List<(double, double)> { (0, lead) };
        switch (Type)
        {
            case HoleType.Counterbore:
                if (CounterboreDiameter <= Diameter || CounterboreDepth <= 0 || CounterboreDepth >= depth)
                {
                    throw new FeatureException("The counterbore must be wider than the hole and shallower than its depth.");
                }

                pts.Add((CounterboreDiameter / 2, lead));
                pts.Add((CounterboreDiameter / 2, CounterboreDepth));
                pts.Add((r, CounterboreDepth));
                break;
            case HoleType.Countersink:
                var rs = CountersinkDiameter / 2;
                var sinkDepth = (rs - r) / Math.Tan(CountersinkAngle * Math.PI / 360);
                if (rs <= r || sinkDepth >= depth)
                {
                    throw new FeatureException("The countersink must be wider than the hole and shallower than its depth.");
                }

                pts.Add((rs, lead));
                pts.Add((rs, 0));
                pts.Add((r, sinkDepth));
                break;
            default:
                pts.Add((r, lead));
                break;
        }

        pts.Add((r, depth));
        if (EndCondition == EndCondition.Blind && DrillPointAngle > 0 && DrillPointAngle < 180)
        {
            pts.Add((0, depth + (r / Math.Tan(DrillPointAngle * Math.PI / 360))));
        }
        else
        {
            pts.Add((0, depth));
        }

        return pts;
    }
}
