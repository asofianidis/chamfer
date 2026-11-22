using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Constraints;

/// <summary>Two points occupy the same location.</summary>
public sealed class CoincidentConstraint(SketchPoint a, SketchPoint b) : SketchConstraint
{
    public SketchPoint A { get; } = a;
    public SketchPoint B { get; } = b;

    public override ConstraintKind Kind => ConstraintKind.Coincident;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    public override int EquationCount => 2;

    public override void Evaluate(IEquationSink sink)
    {
        sink.Equation(A.X - B.X);
        sink.Partial(A.XParam, 1);
        sink.Partial(B.XParam, -1);
        sink.Equation(A.Y - B.Y);
        sink.Partial(A.YParam, 1);
        sink.Partial(B.YParam, -1);
    }
}

/// <summary>A line, or the pair of points, is horizontal (equal Y).</summary>
public sealed class HorizontalConstraint : SketchConstraint
{
    public HorizontalConstraint(SketchLine line)
        : this(line.Start, line.End) => Line = line;

    public HorizontalConstraint(SketchPoint a, SketchPoint b)
    {
        A = a;
        B = b;
    }

    public SketchLine? Line { get; }
    public SketchPoint A { get; }
    public SketchPoint B { get; }

    public override ConstraintKind Kind => ConstraintKind.Horizontal;
    public override IReadOnlyList<SketchEntity> References => Line is null ? [A, B] : [Line];
    public override int EquationCount => 1;

    public override void Evaluate(IEquationSink sink)
    {
        sink.Equation(A.Y - B.Y);
        sink.Partial(A.YParam, 1);
        sink.Partial(B.YParam, -1);
    }
}

/// <summary>A line, or the pair of points, is vertical (equal X).</summary>
public sealed class VerticalConstraint : SketchConstraint
{
    public VerticalConstraint(SketchLine line)
        : this(line.Start, line.End) => Line = line;

    public VerticalConstraint(SketchPoint a, SketchPoint b)
    {
        A = a;
        B = b;
    }

    public SketchLine? Line { get; }
    public SketchPoint A { get; }
    public SketchPoint B { get; }

    public override ConstraintKind Kind => ConstraintKind.Vertical;
    public override IReadOnlyList<SketchEntity> References => Line is null ? [A, B] : [Line];
    public override int EquationCount => 1;

    public override void Evaluate(IEquationSink sink)
    {
        sink.Equation(A.X - B.X);
        sink.Partial(A.XParam, 1);
        sink.Partial(B.XParam, -1);
    }
}

public sealed class ParallelConstraint(SketchLine a, SketchLine b) : SketchConstraint
{
    public SketchLine A { get; } = a;
    public SketchLine B { get; } = b;

    public override ConstraintKind Kind => ConstraintKind.Parallel;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    public override int EquationCount => 1;

    public override void Evaluate(IEquationSink sink)
    {
        sink.Equation(Grad.NormalizedCross(A.Direction, B.Direction, out var ga, out var gb));
        sink.PartialDirection(A, ga);
        sink.PartialDirection(B, gb);
    }
}

public sealed class PerpendicularConstraint(SketchLine a, SketchLine b) : SketchConstraint
{
    public SketchLine A { get; } = a;
    public SketchLine B { get; } = b;

    public override ConstraintKind Kind => ConstraintKind.Perpendicular;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    public override int EquationCount => 1;

    public override void Evaluate(IEquationSink sink)
    {
        sink.Equation(Grad.NormalizedDot(A.Direction, B.Direction, out var ga, out var gb));
        sink.PartialDirection(A, ga);
        sink.PartialDirection(B, gb);
    }
}

/// <summary>Both end points of line B lie on the infinite line through A.</summary>
public sealed class CollinearConstraint(SketchLine a, SketchLine b) : SketchConstraint
{
    public SketchLine A { get; } = a;
    public SketchLine B { get; } = b;

    public override ConstraintKind Kind => ConstraintKind.Collinear;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    public override int EquationCount => 2;

    public override void Evaluate(IEquationSink sink)
    {
        sink.Equation(Grad.SignedDistance(B.Start, A, null));
        Grad.SignedDistance(B.Start, A, sink);
        sink.Equation(Grad.SignedDistance(B.End, A, null));
        Grad.SignedDistance(B.End, A, sink);
    }
}

/// <summary>A point lies on the infinite line through a line segment.</summary>
public sealed class PointOnLineConstraint(SketchPoint point, SketchLine line) : SketchConstraint
{
    public SketchPoint Point { get; } = point;
    public SketchLine Line { get; } = line;

    public override ConstraintKind Kind => ConstraintKind.PointOnLine;
    public override IReadOnlyList<SketchEntity> References => [Point, Line];
    public override int EquationCount => 1;

    public override void Evaluate(IEquationSink sink)
    {
        sink.Equation(Grad.SignedDistance(Point, Line, null));
        Grad.SignedDistance(Point, Line, sink);
    }
}

/// <summary>A point lies on a circle, or on the full circle of an arc.</summary>
public sealed class PointOnCircleConstraint : SketchConstraint
{
    public PointOnCircleConstraint(SketchPoint point, SketchEntity circle)
    {
        Point = point;
        Circle = circle;
        Radial = circle as IRadial ?? throw new ArgumentException("Entity must be a circle or arc.", nameof(circle));
    }

    public SketchPoint Point { get; }
    public SketchEntity Circle { get; }
    private IRadial Radial { get; }

    public override ConstraintKind Kind => ConstraintKind.PointOnCircle;
    public override IReadOnlyList<SketchEntity> References => [Point, Circle];
    public override int EquationCount => 1;

    public override void Evaluate(IEquationSink sink)
    {
        sink.Equation(Grad.Distance(Radial.Center, Point, null) - Radial.Radius);
        Grad.Distance(Radial.Center, Point, sink);
        Radial.PartialRadius(sink, -1);
    }
}

/// <summary>A point sits at the midpoint of a line.</summary>
public sealed class MidpointConstraint(SketchPoint point, SketchLine line) : SketchConstraint
{
    public SketchPoint Point { get; } = point;
    public SketchLine Line { get; } = line;

    public override ConstraintKind Kind => ConstraintKind.Midpoint;
    public override IReadOnlyList<SketchEntity> References => [Point, Line];
    public override int EquationCount => 2;

    public override void Evaluate(IEquationSink sink)
    {
        sink.Equation(Point.X - (Line.Start.X + Line.End.X) / 2);
        sink.Partial(Point.XParam, 1);
        sink.Partial(Line.Start.XParam, -0.5);
        sink.Partial(Line.End.XParam, -0.5);
        sink.Equation(Point.Y - (Line.Start.Y + Line.End.Y) / 2);
        sink.Partial(Point.YParam, 1);
        sink.Partial(Line.Start.YParam, -0.5);
        sink.Partial(Line.End.YParam, -0.5);
    }
}

/// <summary>Points A and B are mirror images of each other about a line.</summary>
public sealed class SymmetricConstraint(SketchPoint a, SketchPoint b, SketchLine axis) : SketchConstraint
{
    public SketchPoint A { get; } = a;
    public SketchPoint B { get; } = b;
    public SketchLine Axis { get; } = axis;

    public override ConstraintKind Kind => ConstraintKind.Symmetric;
    public override IReadOnlyList<SketchEntity> References => [A, B, Axis];
    public override int EquationCount => 2;

    public override void Evaluate(IEquationSink sink)
    {
        var d = Axis.Direction;
        var len2 = Math.Max(d.LengthSquared, 1e-12);
        var len = Math.Sqrt(len2);

        // 1. The midpoint of AB lies on the axis.
        var w = (A.Position + B.Position) / 2 - Axis.Start.Position;
        var f = Vec2.Cross(d, w) / len;
        var gd = new Vec2(w.Y, -w.X) / len - d * (f / len2);
        var gw = new Vec2(-d.Y, d.X) / len;
        sink.Equation(f);
        sink.Partial(A, gw / 2);
        sink.Partial(B, gw / 2);
        sink.Partial(Axis.End, gd);
        sink.Partial(Axis.Start, -gd - gw);

        // 2. AB is perpendicular to the axis.
        var e = B.Position - A.Position;
        var g = Vec2.Dot(e, d) / len;
        var ge = d / len;
        var gd2 = e / len - d * (g / len2);
        sink.Equation(g);
        sink.Partial(B, ge);
        sink.Partial(A, -ge);
        sink.PartialDirection(Axis, gd2);
    }
}

/// <summary>
/// Tangency between a line and a circle/arc, or between two circles/arcs.
/// </summary>
/// <remarks>
/// When the two entities meet at an arc end point (the usual case for connected profiles), tangency is
/// written as "the line is perpendicular to the arc's radius at that point" (or "both centers and the
/// shared point are collinear" for two arcs). The general form, "center-to-line distance equals radius",
/// has a degenerate Jacobian at a touching point that is also constrained coincident, which would make
/// the degrees-of-freedom count wrong. The form and side are captured when the constraint is created.
/// </remarks>
public sealed class TangentConstraint : SketchConstraint
{
    private const int GeneralForm = 0;
    private const int AtEndpointForm = 1;

    private readonly int _form;
    private readonly SketchPoint? _contact;

    public TangentConstraint(SketchEntity a, SketchEntity b)
        : this(a, b, null)
    {
    }

    internal TangentConstraint(SketchEntity a, SketchEntity b, double[]? state)
    {
        // Normalize the order so a line always comes first.
        if (b is SketchLine && a is not SketchLine)
        {
            (a, b) = (b, a);
        }

        if (b is not IRadial)
        {
            throw new ArgumentException("Tangent needs a line and a circle/arc, or two circles/arcs.");
        }

        A = a;
        B = b;
        if (state is { Length: 3 })
        {
            _form = (int)state[0];
            Side = state[1];
            _contact = ContactPoint((int)state[2]);
            return;
        }

        if (FindSharedArcEndpoint() is { } contact)
        {
            _form = AtEndpointForm;
            _contact = contact;
            return;
        }

        _form = GeneralForm;
        if (a is SketchLine line)
        {
            var r = (IRadial)b;
            Side = Grad.SignOf(Grad.SignedDistance(r.Center, line, null));
        }
        else
        {
            var r1 = (IRadial)a;
            var r2 = (IRadial)b;
            var dist = Vec2.Distance(r1.Center.Position, r2.Center.Position);
            var external = Math.Abs(dist - (r1.Radius + r2.Radius));
            var internalGap = Math.Abs(dist - Math.Abs(r1.Radius - r2.Radius));
            // 0 = external contact, ±1 = internal contact with the sign of (r1 - r2).
            Side = external <= internalGap ? 0 : Grad.SignOf(r1.Radius - r2.Radius);
        }
    }

    public SketchEntity A { get; }
    public SketchEntity B { get; }

    /// <summary>General form only. Line–circle: which side of the line the center lies on. Circle–circle: 0 external, ±1 internal.</summary>
    public double Side { get; }

    /// <summary>The arc end point where the two entities meet, when tangency is at an end point.</summary>
    public SketchPoint? ContactPointEntity => _contact;

    public override ConstraintKind Kind => ConstraintKind.Tangent;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    public override int EquationCount => 1;

    internal override double[] State => [_form, Side, ContactIndex()];

    /// <summary>Index of the contact point among B's points, or -1 when there is none.</summary>
    private int ContactIndex()
    {
        for (var i = 0; i < B.Points.Count; i++)
        {
            if (ReferenceEquals(B.Points[i], _contact))
            {
                return i;
            }
        }

        return -1;
    }

    private SketchPoint? ContactPoint(int index) => index >= 0 && index < B.Points.Count ? B.Points[index] : null;

    /// <summary>An end point of arc B that coincides with an end point of A (line or arc).</summary>
    private SketchPoint? FindSharedArcEndpoint()
    {
        if (B is not SketchArc arcB)
        {
            return null;
        }

        var scale = Math.Max(1, arcB.Radius);
        SketchPoint[] candidates = A switch
        {
            SketchLine l => [l.Start, l.End],
            SketchArc arcA => [arcA.Start, arcA.End],
            _ => [],
        };
        foreach (var arcEnd in (SketchPoint[])[arcB.Start, arcB.End])
        {
            if (candidates.Any(c => Vec2.Distance(c.Position, arcEnd.Position) < 1e-6 * scale))
            {
                return arcEnd;
            }
        }

        return null;
    }

    public override void Evaluate(IEquationSink sink)
    {
        var rb = (IRadial)B;
        if (_form == AtEndpointForm && _contact is not null)
        {
            var radius = _contact.Position - rb.Center.Position;
            if (A is SketchLine l)
            {
                // Line direction ⟂ radius at the contact point.
                sink.Equation(Grad.NormalizedDot(l.Direction, radius, out var gl, out var gr));
                sink.PartialDirection(l, gl);
                sink.Partial(_contact, gr);
                sink.Partial(rb.Center, -gr);
            }
            else
            {
                // Both centers and the contact point are collinear.
                var arcA = (IRadial)A;
                var otherRadius = _contact.Position - arcA.Center.Position;
                sink.Equation(Grad.NormalizedCross(radius, otherRadius, out var g1, out var g2));
                sink.Partial(_contact, g1 + g2);
                sink.Partial(rb.Center, -g1);
                sink.Partial(arcA.Center, -g2);
            }

            return;
        }

        if (A is SketchLine line)
        {
            // Side * signedDistance(center, line) - R = 0
            sink.Equation(Side * Grad.SignedDistance(rb.Center, line, null) - rb.Radius);
            Grad.SignedDistance(rb.Center, line, sink, Side);
            rb.PartialRadius(sink, -1);
            return;
        }

        var ra = (IRadial)A;
        var dist = Grad.Distance(ra.Center, rb.Center, null);
        if (Side == 0)
        {
            sink.Equation(dist - ra.Radius - rb.Radius);
            Grad.Distance(ra.Center, rb.Center, sink);
            ra.PartialRadius(sink, -1);
            rb.PartialRadius(sink, -1);
        }
        else
        {
            sink.Equation(dist - Side * (ra.Radius - rb.Radius));
            Grad.Distance(ra.Center, rb.Center, sink);
            ra.PartialRadius(sink, -Side);
            rb.PartialRadius(sink, Side);
        }
    }
}

/// <summary>Two circles/arcs share a center.</summary>
public sealed class ConcentricConstraint : SketchConstraint
{
    public ConcentricConstraint(SketchEntity a, SketchEntity b)
    {
        A = a;
        B = b;
        CenterA = a as IRadial is { } ra ? ra.Center : a as SketchPoint ?? throw new ArgumentException("Expected a circle, arc or point.", nameof(a));
        CenterB = b as IRadial is { } rb ? rb.Center : b as SketchPoint ?? throw new ArgumentException("Expected a circle, arc or point.", nameof(b));
    }

    public SketchEntity A { get; }
    public SketchEntity B { get; }
    private SketchPoint CenterA { get; }
    private SketchPoint CenterB { get; }

    public override ConstraintKind Kind => ConstraintKind.Concentric;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    public override int EquationCount => 2;

    public override void Evaluate(IEquationSink sink) => new CoincidentConstraint(CenterA, CenterB).Evaluate(sink);
}

/// <summary>Two lines have equal length, or two circles/arcs have equal radius.</summary>
public sealed class EqualConstraint : SketchConstraint
{
    public EqualConstraint(SketchEntity a, SketchEntity b)
    {
        var bothLines = a is SketchLine && b is SketchLine;
        var bothRadial = a is IRadial && b is IRadial;
        if (!bothLines && !bothRadial)
        {
            throw new ArgumentException("Equal needs two lines or two circles/arcs.");
        }

        A = a;
        B = b;
    }

    public SketchEntity A { get; }
    public SketchEntity B { get; }

    public override ConstraintKind Kind => ConstraintKind.Equal;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    public override int EquationCount => 1;

    public override void Evaluate(IEquationSink sink)
    {
        if (A is SketchLine la && B is SketchLine lb)
        {
            sink.Equation(la.Length - lb.Length);
            Grad.Distance(la.Start, la.End, sink);
            Grad.Distance(lb.Start, lb.End, sink, -1);
            return;
        }

        var ra = (IRadial)A;
        var rb = (IRadial)B;
        sink.Equation(ra.Radius - rb.Radius);
        ra.PartialRadius(sink, 1);
        rb.PartialRadius(sink, -1);
    }
}

/// <summary>Two circles/arcs share a center and radius.</summary>
public sealed class CoradialConstraint(SketchEntity a, SketchEntity b) : SketchConstraint
{
    private readonly ConcentricConstraint _concentric = new(a, b);
    private readonly EqualConstraint _equal = new(a, b);

    public SketchEntity A { get; } = a;
    public SketchEntity B { get; } = b;

    public override ConstraintKind Kind => ConstraintKind.Coradial;
    public override IReadOnlyList<SketchEntity> References => [A, B];
    public override int EquationCount => 3;

    public override void Evaluate(IEquationSink sink)
    {
        _concentric.Evaluate(sink);
        _equal.Evaluate(sink);
    }
}

/// <summary>
/// Locks an entity where it was when the constraint was created.
/// Arcs are locked by center, start point and end angle, because their end point already has
/// one built-in equation (equal radius) and locking all six coordinates would over-define them.
/// </summary>
public sealed class FixConstraint : SketchConstraint
{
    private readonly Param[] _params;
    private readonly double[] _values;
    private readonly SketchArc? _arc;

    public FixConstraint(SketchEntity entity)
        : this(entity, null)
    {
    }

    internal FixConstraint(SketchEntity entity, double[]? state)
    {
        Entity = entity;
        _arc = entity as SketchArc;
        _params = _arc is null
            ? [.. entity.Params]
            : [_arc.Center.XParam, _arc.Center.YParam, _arc.Start.XParam, _arc.Start.YParam];

        var count = _params.Length + (_arc is null ? 0 : 1);
        _values = state is not null && state.Length == count
            ? state
            : [.. _params.Select(p => p.Value), .. _arc is null ? Array.Empty<double>() : [_arc.EndAngle]];
    }

    public SketchEntity Entity { get; }

    public override ConstraintKind Kind => ConstraintKind.Fix;
    public override IReadOnlyList<SketchEntity> References => [Entity];
    public override int EquationCount => _values.Length;
    internal override double[] State => [.. _values];

    public override void Evaluate(IEquationSink sink)
    {
        for (var i = 0; i < _params.Length; i++)
        {
            sink.Equation(_params[i].Value - _values[i]);
            sink.Partial(_params[i], 1);
        }

        if (_arc is not null)
        {
            // atan2(v.y, v.x) has gradient (-v.y, v.x) / |v|² with respect to v = End - Center.
            var v = _arc.End.Position - _arc.Center.Position;
            var g = v.Perp() / Math.Max(v.LengthSquared, 1e-12);
            sink.Equation(Grad.WrapAngle(v.Angle - _values[^1]));
            sink.Partial(_arc.End, g);
            sink.Partial(_arc.Center, -g);
        }
    }
}
