using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Constraints;

public enum ConstraintKind
{
    // Geometric relations
    Coincident,
    Horizontal,
    Vertical,
    Parallel,
    Perpendicular,
    Collinear,
    PointOnLine,
    PointOnCircle,
    Midpoint,
    Symmetric,
    Tangent,
    Concentric,
    Equal,
    Coradial,
    Fix,

    // Dimensions
    Distance,
    HorizontalDistance,
    VerticalDistance,
    PointLineDistance,
    Angle,
    Radius,
    Diameter,
}

/// <summary>
/// A geometric relation or dimension between sketch entities, expressed as equations for the solver.
/// </summary>
public abstract class SketchConstraint : IEquationSource
{
    /// <summary>Stable id within the owning sketch. Assigned when the constraint is added.</summary>
    public int Id { get; internal set; }

    public abstract ConstraintKind Kind { get; }

    /// <summary>The entities this constraint refers to, in a fixed order used for serialization.</summary>
    public abstract IReadOnlyList<SketchEntity> References { get; }

    public abstract int EquationCount { get; }

    public abstract void Evaluate(IEquationSink sink);

    /// <summary>Extra numeric state (side signs, captured positions) needed to recreate this constraint.</summary>
    internal virtual double[] State => [];

    /// <summary>Human-readable name, for example "Horizontal" or "Distance 25 mm".</summary>
    public virtual string DisplayName => Kind.ToString();

    public override string ToString() => $"{DisplayName} [{string.Join(", ", References)}]";
}

/// <summary>
/// A dimension. Driving dimensions constrain the geometry; driven (reference) dimensions only measure it.
/// Lengths are in millimetres and angles in radians.
/// </summary>
public abstract class DimensionConstraint : SketchConstraint
{
    private double _value;

    public double Value
    {
        get => IsDriving ? _value : Measure();
        set => _value = value;
    }

    private bool _isDriving = true;

    /// <summary>
    /// Whether this dimension constrains geometry. Switching from driven to driving
    /// keeps the currently measured value.
    /// </summary>
    public bool IsDriving
    {
        get => _isDriving;
        set
        {
            if (value && !_isDriving)
            {
                _value = Measure();
            }

            _isDriving = value;
        }
    }

    /// <summary>Where the dimension label sits, relative to the dimension's anchor point. Used by the UI.</summary>
    public Vec2 LabelOffset { get; set; }

    public virtual bool IsAngular => false;

    /// <summary>The current value of the measured quantity.</summary>
    public abstract double Measure();

    public sealed override int EquationCount => IsDriving ? 1 : 0;

    public sealed override void Evaluate(IEquationSink sink)
    {
        if (IsDriving)
        {
            EvaluateDriving(sink, _value);
        }
    }

    protected abstract void EvaluateDriving(IEquationSink sink, double target);

    public override string DisplayName =>
        IsAngular ? $"{Kind} {Value * 180 / Math.PI:0.##}°" : $"{Kind} {Value:0.###} mm";
}
