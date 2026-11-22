namespace Chamfer.Document;

/// <summary>
/// A plane a sketch or feature is placed on: a standard plane ("front",
/// "top", "right"), a reference plane feature id, or a planar face.
/// </summary>
public sealed record PlaneRef(string? PlaneId = null, TopoRef? Face = null)
{
    public static PlaneRef Front { get; } = new("front");
    public static PlaneRef Top { get; } = new("top");
    public static PlaneRef Right { get; } = new("right");
}

/// <summary>
/// A direction or axis: a linear edge, the axis of a cylindrical or conical
/// face, the normal of a plane, a line in a sketch, or a standard axis ("x", "y", "z").
/// </summary>
public sealed record AxisRef(
    TopoRef? Edge = null,
    TopoRef? Face = null,
    PlaneRef? PlaneNormal = null,
    string? SketchId = null,
    int SketchEntityId = 0,
    string? StandardAxis = null,
    string? AxisId = null)
{
    public static AxisRef X { get; } = new(StandardAxis: "x");
    public static AxisRef Y { get; } = new(StandardAxis: "y");
    public static AxisRef Z { get; } = new(StandardAxis: "z");
}
