namespace Chamfer.Sketching.Entities;

public enum EntityKind
{
    Point,
    Line,
    Circle,
    Arc,
}

/// <summary>Base class for all sketch geometry.</summary>
public abstract class SketchEntity
{
    /// <summary>Stable id within the owning sketch. Assigned when the entity is added.</summary>
    public int Id { get; internal set; }

    public abstract EntityKind Kind { get; }

    /// <summary>Construction geometry helps define a sketch but is not used by features.</summary>
    public bool IsConstruction { get; set; }

    /// <summary>The entity that owns this one (a line owns its end points). Null for top-level entities.</summary>
    public SketchEntity? Owner { get; internal set; }

    /// <summary>Every solver parameter that defines this entity, including those of owned points.</summary>
    public abstract IEnumerable<Param> Params { get; }

    /// <summary>The points owned by this entity (end points, centers).</summary>
    public virtual IReadOnlyList<SketchPoint> Points => [];

    /// <summary>The top-level entity this belongs to (itself when it has no owner).</summary>
    public SketchEntity Root => Owner ?? this;

    /// <summary>Shortest distance from <paramref name="p"/> to this entity's geometry.</summary>
    public abstract double DistanceTo(Vec2 p);

    /// <summary>The nearest point on this entity's geometry.</summary>
    public abstract Vec2 ClosestPoint(Vec2 p);

    public override string ToString() => $"{Kind}{Id}";
}

/// <summary>An entity with a center and radius (circle or arc).</summary>
public interface IRadial
{
    SketchPoint Center { get; }

    double Radius { get; }

    /// <summary>Adds scale × ∂Radius/∂p for every parameter the radius depends on.</summary>
    void PartialRadius(Solving.IEquationSink sink, double scale);
}
