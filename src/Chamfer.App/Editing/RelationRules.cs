using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Editing;

/// <summary>Turns "add relation X to the selected entities" into concrete constraints.</summary>
public static class RelationRules
{
    /// <summary>Relations offered in the UI, in display order.</summary>
    public static readonly IReadOnlyList<ConstraintKind> Offered =
    [
        ConstraintKind.Coincident,
        ConstraintKind.Horizontal,
        ConstraintKind.Vertical,
        ConstraintKind.Parallel,
        ConstraintKind.Perpendicular,
        ConstraintKind.Collinear,
        ConstraintKind.Tangent,
        ConstraintKind.Concentric,
        ConstraintKind.Equal,
        ConstraintKind.Midpoint,
        ConstraintKind.Symmetric,
        ConstraintKind.Coradial,
        ConstraintKind.Fix,
    ];

    /// <summary>
    /// The constraints that <paramref name="kind"/> means for this selection (in selection order),
    /// or null if the relation doesn't apply to it.
    /// </summary>
    public static IReadOnlyList<SketchConstraint>? Build(ConstraintKind kind, IReadOnlyList<SketchEntity> selection)
    {
        var points = selection.OfType<SketchPoint>().ToList();
        var lines = selection.OfType<SketchLine>().ToList();
        var radials = selection.Where(e => e is IRadial).ToList();
        var n = selection.Count;
        var allLines = n > 0 && lines.Count == n;
        var allPoints = n > 0 && points.Count == n;
        var allRadial = n > 0 && radials.Count == n;

        return kind switch
        {
            ConstraintKind.Coincident when allPoints && n >= 2 =>
                [.. points.Skip(1).Select(p => new CoincidentConstraint(points[0], p))],
            ConstraintKind.Coincident when lines.Count == 1 && points.Count == n - 1 && n >= 2 =>
                [.. points.Select(p => new PointOnLineConstraint(p, lines[0]))],
            ConstraintKind.Coincident when radials.Count == 1 && points.Count == n - 1 && n >= 2 =>
                [.. points.Select(p => new PointOnCircleConstraint(p, radials[0]))],

            ConstraintKind.Horizontal when allLines => [.. lines.Select(l => new HorizontalConstraint(l))],
            ConstraintKind.Horizontal when allPoints && n >= 2 =>
                [.. points.Skip(1).Select(p => new HorizontalConstraint(points[0], p))],
            ConstraintKind.Vertical when allLines => [.. lines.Select(l => new VerticalConstraint(l))],
            ConstraintKind.Vertical when allPoints && n >= 2 =>
                [.. points.Skip(1).Select(p => new VerticalConstraint(points[0], p))],

            ConstraintKind.Parallel when allLines && n >= 2 =>
                [.. lines.Skip(1).Select(l => new ParallelConstraint(lines[0], l))],
            ConstraintKind.Perpendicular when allLines && n == 2 => [new PerpendicularConstraint(lines[0], lines[1])],
            ConstraintKind.Collinear when allLines && n >= 2 =>
                [.. lines.Skip(1).Select(l => new CollinearConstraint(lines[0], l))],

            ConstraintKind.Tangent when n == 2 && radials.Count >= 1 && (lines.Count == 1 || radials.Count == 2) =>
                [new TangentConstraint(selection[0], selection[1])],
            ConstraintKind.Concentric when n == 2 && radials.Count >= 1 && radials.Count + points.Count == 2 =>
                [new ConcentricConstraint(selection[0], selection[1])],

            ConstraintKind.Equal when (allLines || allRadial) && n >= 2 =>
                [.. selection.Skip(1).Select(e => new EqualConstraint(selection[0], e))],
            ConstraintKind.Coradial when allRadial && n == 2 => [new CoradialConstraint(selection[0], selection[1])],

            ConstraintKind.Midpoint when n == 2 && points.Count == 1 && lines.Count == 1 =>
                [new MidpointConstraint(points[0], lines[0])],
            ConstraintKind.Symmetric when n == 3 && points.Count == 2 && lines.Count == 1 =>
                [new SymmetricConstraint(points[0], points[1], lines[0])],

            ConstraintKind.Fix when n >= 1 && selection.All(e => e.Id != Chamfer.Sketching.Sketch.OriginId) =>
                [.. selection.Select(e => new FixConstraint(e))],

            _ => null,
        };
    }

    public static string Glyph(ConstraintKind kind) => kind switch
    {
        ConstraintKind.Coincident => "●",
        ConstraintKind.Horizontal => "H",
        ConstraintKind.Vertical => "V",
        ConstraintKind.Parallel => "//",
        ConstraintKind.Perpendicular => "⊥",
        ConstraintKind.Collinear => "—",
        ConstraintKind.PointOnLine => "on",
        ConstraintKind.PointOnCircle => "on",
        ConstraintKind.Midpoint => "M",
        ConstraintKind.Symmetric => "><",
        ConstraintKind.Tangent => "T",
        ConstraintKind.Concentric => "(o)",
        ConstraintKind.Equal => "=",
        ConstraintKind.Coradial => "(o)=",
        ConstraintKind.Fix => "Fix",
        _ => "",
    };

    public static string Label(ConstraintKind kind) => kind switch
    {
        ConstraintKind.PointOnLine => "On Line",
        ConstraintKind.PointOnCircle => "On Circle",
        ConstraintKind.HorizontalDistance => "Horizontal Distance",
        ConstraintKind.VerticalDistance => "Vertical Distance",
        ConstraintKind.PointLineDistance => "Distance to Line",
        _ => kind.ToString(),
    };
}
