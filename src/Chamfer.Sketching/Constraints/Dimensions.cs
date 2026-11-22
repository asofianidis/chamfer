using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Constraints;

/// <summary>Straight-line distance between two points, or the length of a line.</summary>
public sealed class DistanceConstraint : DimensionConstraint
{
    public DistanceConstraint(SketchLine line, double? value = null)
        : this(line.Start, line.End, value) => Line = line;

    public DistanceConstraint(SketchPoint a, SketchPoint b, double? value = null)
    {
        A = a;
        B = b;
        Value = value ?? Measure();
    }

    /// <summary>Set when this dimension measures the length of a line.</summary>
    public SketchLine? Line { get; }
    public SketchPoint A { get; }
    public SketchPoint B { get; }

    public override ConstraintKind Kind => ConstraintKind.Distance;
    public override IReadOnlyList<SketchEntity> References => Line is null ? [A, B] : [Line];

    public override double Measure() => Vec2.Distance(A.Position, B.Position);

    protected override void EvaluateDriving(IEquationSink sink, double target)
    {
        sink.Equation(Measure() - target);
        Grad.Distance(A, B, sink);
    }
}

/// <summary>Horizontal (X) or vertical (Y) distance between two points.</summary>
public sealed class AxisDistanceConstraint : DimensionConstraint
{
    public AxisDistanceConstraint(SketchPoint a, SketchPoint b, bool vertical, double? value = null)
        : this(a, b, vertical, value, null)
    {
    }

    internal AxisDistanceConstraint(SketchPoint a, SketchPoint b, bool vertical, double? value, double[]? state)
    {
        A = a;
        B = b;
        IsVertical = vertical;
        Sign = state is { Length: 1 } ? state[0] : Grad.SignOf(Delta);
        Value = value ?? Measure();
    }

    public SketchPoint A { get; }
    public SketchPoint B { get; }
    public bool IsVertical { get; }

    /// <summary>Which way B lies from A along the axis, captured at creation so the solver doesn't flip it.</summary>
    public double Sign { get; }

    private double Delta => IsVertical ? B.Y - A.Y : B.X - A.X;

    public override ConstraintKind Kind => IsVertical ? ConstraintKind.VerticalDistance : ConstraintKind.HorizontalDistance;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    internal override double[] State => [Sign];

    public override double Measure() => Math.Abs(Delta);

    protected override void EvaluateDriving(IEquationSink sink, double target)
    {
        sink.Equation(Sign * Delta - target);
        if (IsVertical)
        {
            sink.Partial(B.YParam, Sign);
            sink.Partial(A.YParam, -Sign);
        }
        else
        {
            sink.Partial(B.XParam, Sign);
            sink.Partial(A.XParam, -Sign);
        }
    }
}

/// <summary>Perpendicular distance from a point to a line. Also used for the gap between two parallel lines.</summary>
public sealed class PointLineDistanceConstraint : DimensionConstraint
{
    public PointLineDistanceConstraint(SketchPoint point, SketchLine line, double? value = null)
        : this(point, line, value, null)
    {
    }

    internal PointLineDistanceConstraint(SketchPoint point, SketchLine line, double? value, double[]? state)
    {
        Point = point;
        Line = line;
        Side = state is { Length: 1 } ? state[0] : Grad.SignOf(Grad.SignedDistance(point, line, null));
        Value = value ?? Measure();
    }

    public SketchPoint Point { get; }
    public SketchLine Line { get; }

    /// <summary>Which side of the line the point is on, captured at creation.</summary>
    public double Side { get; }

    public override ConstraintKind Kind => ConstraintKind.PointLineDistance;
    public override IReadOnlyList<SketchEntity> References => [Point, Line];
    internal override double[] State => [Side];

    public override double Measure() => Math.Abs(Grad.SignedDistance(Point, Line, null));

    protected override void EvaluateDriving(IEquationSink sink, double target)
    {
        sink.Equation(Side * Grad.SignedDistance(Point, Line, null) - target);
        Grad.SignedDistance(Point, Line, sink, Side);
    }
}

/// <summary>Angle between two lines, in radians, in [0, π].</summary>
public sealed class AngleConstraint : DimensionConstraint
{
    public AngleConstraint(SketchLine a, SketchLine b, double? value = null)
        : this(a, b, value, null)
    {
    }

    internal AngleConstraint(SketchLine a, SketchLine b, double? value, double[]? state)
    {
        A = a;
        B = b;
        Sign = state is { Length: 1 } ? state[0] : Grad.SignOf(Grad.Angle(a, b, null));
        Value = value ?? Measure();
    }

    public SketchLine A { get; }
    public SketchLine B { get; }

    /// <summary>Rotation direction from A to B (counter-clockwise = +1), captured at creation.</summary>
    public double Sign { get; }

    public override ConstraintKind Kind => ConstraintKind.Angle;
    public override bool IsAngular => true;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    internal override double[] State => [Sign];

    public override double Measure() => Math.Abs(Grad.Angle(A, B, null));

    protected override void EvaluateDriving(IEquationSink sink, double target)
    {
        sink.Equation(Grad.WrapAngle(Sign * Grad.Angle(A, B, null) - target));
        Grad.Angle(A, B, sink, Sign);
    }
}

/// <summary>Radius or diameter of a circle or arc.</summary>
public sealed class RadialDimensionConstraint : DimensionConstraint
{
    public RadialDimensionConstraint(SketchEntity circle, bool diameter, double? value = null)
    {
        Circle = circle;
        Radial = circle as IRadial ?? throw new ArgumentException("Entity must be a circle or arc.", nameof(circle));
        IsDiameter = diameter;
        Value = value ?? Measure();
    }

    public SketchEntity Circle { get; }
    private IRadial Radial { get; }
    public bool IsDiameter { get; }

    public override ConstraintKind Kind => IsDiameter ? ConstraintKind.Diameter : ConstraintKind.Radius;
    public override IReadOnlyList<SketchEntity> References => [Circle];

    private double Factor => IsDiameter ? 2 : 1;

    public override double Measure() => Radial.Radius * Factor;

    protected override void EvaluateDriving(IEquationSink sink, double target)
    {
        sink.Equation(Measure() - target);
        Radial.PartialRadius(sink, Factor);
    }
}
