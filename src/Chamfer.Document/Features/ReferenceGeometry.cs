using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>How a reference axis is defined.</summary>
public enum AxisKind
{
    /// <summary>Along a straight edge, a cylinder's or circular edge's axis, a sketch line, or a plane normal.</summary>
    Along,

    /// <summary>Where two planes meet.</summary>
    TwoPlanes,

    /// <summary>Through two reference points.</summary>
    TwoPoints,
}

/// <summary>A reference axis, usable wherever an axis is asked for (revolves, patterns, mates).</summary>
public sealed class ReferenceAxisFeature : Feature
{
    public override string DefaultName => "Axis";

    public AxisKind Kind { get; set; }

    public AxisRef? Along { get; set; }

    public PlaneRef? PlaneA { get; set; }

    public PlaneRef? PlaneB { get; set; }

    public string? PointA { get; set; }

    public string? PointB { get; set; }

    public override IEnumerable<string> Dependencies =>
        new[] { PlaneA?.PlaneId, PlaneB?.PlaneId, PointA, PointB, Along?.SketchId, Along?.AxisId, Along?.PlaneNormal?.PlaneId }
            .OfType<string>().Where(id => id is not ("front" or "top" or "right"));

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Axes[Id] = Kind switch
        {
            AxisKind.Along => ctx.ResolveAxis(Along ?? throw new FeatureException("Select an edge, cylindrical face or line for the axis.")),
            AxisKind.TwoPlanes => Intersection(
                ctx.ResolvePlane(PlaneA ?? throw new FeatureException("Select two planes.")),
                ctx.ResolvePlane(PlaneB ?? throw new FeatureException("Select two planes."))),
            AxisKind.TwoPoints => Through(
                ctx.ResolvePoint(PointA ?? throw new FeatureException("Select two points.")),
                ctx.ResolvePoint(PointB ?? throw new FeatureException("Select two points."))),
            _ => throw new FeatureException("Unknown axis type."),
        };
    }

    /// <summary>The line where two planes meet.</summary>
    public static (Vec3 Origin, Vec3 Direction) Intersection(Plane3 a, Plane3 b)
    {
        var dir = Vec3.Cross(a.Normal, b.Normal);
        if (dir.LengthSquared < 1e-12)
        {
            throw new FeatureException("The planes are parallel; they don't meet in an axis.");
        }

        // The point on both planes closest to the origin.
        var da = Vec3.Dot(a.Normal, a.Origin);
        var db = Vec3.Dot(b.Normal, b.Origin);
        var nn = Vec3.Dot(a.Normal, b.Normal);
        var det = 1 - (nn * nn);
        var origin = (a.Normal * ((da - (db * nn)) / det)) + (b.Normal * ((db - (da * nn)) / det));
        return (origin, dir.Normalized());
    }

    private static (Vec3, Vec3) Through(Vec3 a, Vec3 b) =>
        Vec3.Distance(a, b) < 1e-9 ? throw new FeatureException("The two points are the same.") : (a, (b - a).Normalized());
}

/// <summary>How a reference point is defined.</summary>
public enum PointKind
{
    Coordinates,
    FaceCenter,
    ArcCenter,
    EdgeMidpoint,
}

/// <summary>A reference point: at coordinates, a face's centre, an arc's centre or an edge's midpoint.</summary>
public sealed class ReferencePointFeature : Feature
{
    public override string DefaultName => "Point";

    public PointKind Kind { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public TopoRef? Face { get; set; }

    public TopoRef? Edge { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Points[Id] = Kind switch
        {
            PointKind.Coordinates => new Vec3(X, Y, Z),
            PointKind.FaceCenter => FaceCenter(ctx),
            PointKind.ArcCenter => ArcCenter(ctx),
            PointKind.EdgeMidpoint => EdgeInfo(ctx).Midpoint,
            _ => throw new FeatureException("Unknown point type."),
        };
    }

    private Vec3 FaceCenter(RebuildContext ctx)
    {
        var (body, index) = ctx.ResolveFace(Face ?? throw new FeatureException("Select a face."));
        return body.Face(index).Centroid;
    }

    private Vec3 ArcCenter(RebuildContext ctx)
    {
        var e = EdgeInfo(ctx);
        return e.Curve == CurveKind.Circle ? e.CenterOrDirection : throw new FeatureException("Select a circular edge.");
    }

    private EdgeInfo EdgeInfo(RebuildContext ctx)
    {
        var (body, index) = ctx.ResolveEdge(Edge ?? throw new FeatureException("Select an edge."));
        return body.Edge(index);
    }
}

/// <summary>
/// A coordinate system: an origin (a reference point, or coordinates) with a Z direction and an
/// X direction from axes. Mass properties can be reported in it.
/// </summary>
public sealed class CoordinateSystemFeature : Feature
{
    public override string DefaultName => "Coordinate System";

    /// <summary>Reference point for the origin; when unset, <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/>.</summary>
    public string? OriginPoint { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    /// <summary>The Z direction (model Z when unset).</summary>
    public AxisRef? ZAxis { get; set; }

    /// <summary>The X direction, made square to Z (any perpendicular when unset).</summary>
    public AxisRef? XAxis { get; set; }

    public override IEnumerable<string> Dependencies =>
        new[] { OriginPoint, ZAxis?.AxisId, XAxis?.AxisId, ZAxis?.SketchId, XAxis?.SketchId }.OfType<string>();

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var origin = OriginPoint is { } p ? ctx.ResolvePoint(p) : new Vec3(X, Y, Z);
        var z = ZAxis is { } za ? ctx.ResolveAxis(za).Direction.Normalized() : Vec3.UnitZ;
        var xHint = XAxis is { } xa ? ctx.ResolveAxis(xa).Direction : z.AnyPerpendicular();
        var x = xHint - (z * Vec3.Dot(xHint, z));
        if (x.LengthSquared < 1e-12)
        {
            throw new FeatureException("The X axis can't be parallel to the Z axis.");
        }

        x = x.Normalized();
        var y = Vec3.Cross(z, x);

        // Local to model: columns are the axes.
        ctx.Frames[Id] = new Transform3(x.X, y.X, z.X, origin.X, x.Y, y.Y, z.Y, origin.Y, x.Z, y.Z, z.Z, origin.Z);
    }
}
