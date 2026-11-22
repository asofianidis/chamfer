using Chamfer.Document;

namespace Chamfer.App.Part;

public enum SelectionKind
{
    Face,
    Edge,
    Plane,
    Feature,
}

/// <summary>
/// Something selected in the part: a face or edge (by persistent reference),
/// a reference plane, or a feature in the tree. Selections are stored by
/// reference rather than by index so they survive rebuilds.
/// </summary>
public sealed record SelectionItem(SelectionKind Kind, TopoRef? Ref = null, string? PlaneId = null, string? FeatureId = null, string Label = "", string? ComponentId = null)
{
    public static SelectionItem Face(Body body, int index) =>
        new(SelectionKind.Face, body.FaceRef(index), Label: $"Face <{Describe(body.Face(index).Surface.ToString())}>");

    public static SelectionItem Edge(Body body, int index) =>
        new(SelectionKind.Edge, body.EdgeRef(index), Label: $"Edge <{Describe(body.Edge(index).Curve.ToString())}>");

    public static SelectionItem Plane(string planeId, string label) => new(SelectionKind.Plane, PlaneId: planeId, Label: label);

    public static SelectionItem ForFeature(Feature f) => new(SelectionKind.Feature, FeatureId: f.Id, Label: f.Name);

    public bool SameTarget(SelectionItem other) =>
        Kind == other.Kind && ComponentId == other.ComponentId && (Ref?.Name ?? PlaneId ?? FeatureId) == (other.Ref?.Name ?? other.PlaneId ?? other.FeatureId);

    /// <summary>The plane reference this selection stands for, if it's a plane or planar face.</summary>
    public PlaneRef? AsPlane() => Kind switch
    {
        SelectionKind.Plane when PlaneId?.StartsWith("axis:", StringComparison.Ordinal) == true => null,
        SelectionKind.Plane => new PlaneRef(PlaneId),
        SelectionKind.Face => new PlaneRef(Face: Ref),
        _ => null,
    };

    public AxisRef? AsAxis() => Kind switch
    {
        SelectionKind.Edge => new AxisRef(Edge: Ref),
        SelectionKind.Face => new AxisRef(Face: Ref),
        SelectionKind.Plane when PlaneId?.StartsWith("axis:", StringComparison.Ordinal) == true =>
            PlaneId[5..] is "x" or "y" or "z" ? new AxisRef(StandardAxis: PlaneId[5..]) : new AxisRef(AxisId: PlaneId[5..]),
        SelectionKind.Plane => new AxisRef(PlaneNormal: new PlaneRef(PlaneId)),
        _ => null,
    };

    private static string Describe(string kind) => kind.ToLowerInvariant();

    public override string ToString() => Label;
}

/// <summary>What a pick id in the 3D scene refers to.</summary>
public abstract record PickTarget;

/// <summary>Maps renderer pick ids back to model entities.</summary>
public interface IPickSource
{
    PickTarget? Target(int id);
}

/// <summary>A face or edge of a component in an assembly.</summary>
public sealed record ComponentPick(Chamfer.Assembly.Component Component, Body Body, int Index, bool IsEdge) : PickTarget;

public sealed record FacePick(Body Body, int Index) : PickTarget;

public sealed record EdgePick(Body Body, int Index) : PickTarget;

public sealed record PlanePick(string PlaneId, string Label) : PickTarget;

public sealed record SketchPick(string FeatureId) : PickTarget;
