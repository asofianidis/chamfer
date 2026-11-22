using Chamfer.Geometry;

namespace Chamfer.Kernel.Occt;

/// <summary>Topological entity kinds; values match OCCT's TopAbs_ShapeEnum.</summary>
public enum TopoKind
{
    Compound = 0,
    CompSolid = 1,
    Solid = 2,
    Shell = 3,
    Face = 4,
    Wire = 5,
    Edge = 6,
    Vertex = 7,
}

public enum SurfaceKind
{
    Plane,
    Cylinder,
    Cone,
    Sphere,
    Torus,
    Bezier,
    BSpline,
    Revolution,
    Extrusion,
    Offset,
    Other,
}

public enum CurveKind
{
    Line,
    Circle,
    Ellipse,
    Hyperbola,
    Parabola,
    Bezier,
    BSpline,
    Offset,
    Other,
}

public enum BooleanKind
{
    Fuse = 0,
    Cut = 1,
    Common = 2,
}

/// <summary>Geometry summary of a face.</summary>
/// <param name="Index">1-based face index in the owning shape.</param>
/// <param name="Normal">Outward normal at the point nearest the centroid.</param>
/// <param name="AxisOrigin">Plane origin, or axis origin for cylinders, cones and tori; sphere centre.</param>
/// <param name="AxisDirection">Plane normal (outward) or axis direction.</param>
public sealed record FaceInfo(
    int Index,
    SurfaceKind Surface,
    double Area,
    Vec3 Centroid,
    Vec3 Normal,
    Vec3 AxisOrigin,
    Vec3 AxisDirection,
    double Radius);

/// <summary>Geometry summary of an edge.</summary>
/// <param name="CenterOrDirection">Circle centre for circular edges, direction for lines.</param>
public sealed record EdgeInfo(
    int Index,
    CurveKind Curve,
    double Length,
    Vec3 Midpoint,
    Vec3 Start,
    Vec3 End,
    Vec3 CenterOrDirection,
    double Radius,
    bool IsClosed);

/// <summary>Mass properties at unit density. Inertia is about the centre of mass, in mm^5.</summary>
public sealed record MassProperties(
    double Volume,
    double SurfaceArea,
    Vec3 CenterOfMass,
    double Ixx,
    double Iyy,
    double Izz,
    double Ixy,
    double Ixz,
    double Iyz);

public enum SegmentKind
{
    Line = 0,
    Arc = 1,
    Circle = 2,
    Ellipse = 3,
}

/// <summary>
/// One segment of a planar profile in plane coordinates. The meaning of the
/// eight values depends on <see cref="Kind"/>; use the factory methods.
/// </summary>
public readonly record struct ProfileSegment(SegmentKind Kind, double A, double B, double C, double D, double E, double F, double G)
{
    public static ProfileSegment Line(double x0, double y0, double x1, double y1) =>
        new(SegmentKind.Line, x0, y0, x1, y1, 0, 0, 0);

    /// <summary>Arc around (cx, cy) from start to end, counter-clockwise when <paramref name="ccw"/>.</summary>
    public static ProfileSegment Arc(double cx, double cy, double x0, double y0, double x1, double y1, bool ccw) =>
        new(SegmentKind.Arc, cx, cy, x0, y0, x1, y1, ccw ? 1 : 0);

    public static ProfileSegment Circle(double cx, double cy, double r) =>
        new(SegmentKind.Circle, cx, cy, r, 0, 0, 0, 0);

    public static ProfileSegment Ellipse(double cx, double cy, double major, double minor, double angle) =>
        new(SegmentKind.Ellipse, cx, cy, major, minor, angle, 0, 0);
}

/// <summary>Thrown when the geometry kernel rejects an operation.</summary>
public sealed class KernelException : Exception
{
    public KernelException()
    {
    }

    public KernelException(string message)
        : base(message)
    {
    }

    public KernelException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
