using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Editing;

public enum SnapKind
{
    None,

    /// <summary>An existing point: end point, center, standalone point or the origin.</summary>
    Point,

    /// <summary>The midpoint of a line.</summary>
    Midpoint,

    /// <summary>Somewhere along a line.</summary>
    OnLine,

    /// <summary>Somewhere along a circle or arc.</summary>
    OnCircle,
}

/// <summary>Where the cursor snapped to, and which entity that implies a relation with.</summary>
public readonly record struct Snap(SnapKind Kind, Vec2 Position, SketchEntity? Target)
{
    public static Snap None(Vec2 position) => new(SnapKind.None, position, null);

    /// <summary>A point the cursor lines up with vertically (an inference line is drawn from it).</summary>
    public Vec2? AlignX { get; init; }

    /// <summary>A point the cursor lines up with horizontally.</summary>
    public Vec2? AlignY { get; init; }

    /// <summary>The relation a new point placed at this snap should get, if any.</summary>
    public SketchConstraint? RelationFor(SketchPoint newPoint) => Kind switch
    {
        SnapKind.Point when Target is SketchPoint p => new CoincidentConstraint(newPoint, p),
        SnapKind.Midpoint when Target is SketchLine l => new MidpointConstraint(newPoint, l),
        SnapKind.OnLine when Target is SketchLine l => new PointOnLineConstraint(newPoint, l),
        SnapKind.OnCircle when Target is not null => new PointOnCircleConstraint(newPoint, Target),
        _ => null,
    };

    /// <summary>Short glyph drawn next to the cursor to show the inferred relation.</summary>
    public string? Glyph => Kind switch
    {
        SnapKind.Point => "●",
        SnapKind.Midpoint => "M",
        SnapKind.OnLine or SnapKind.OnCircle => "on",
        _ => null,
    };
}
