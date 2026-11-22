using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.Sketching;

/// <summary>
/// Creates common multi-entity shapes along with the relations a user would expect
/// (for example, a corner rectangle is four lines joined at the corners, two horizontal and two vertical).
/// </summary>
public static class ShapeBuilder
{
    /// <summary>Lines through consecutive points, joined end to start. Closed polylines also join last to first.</summary>
    public static IReadOnlyList<SketchLine> AddPolyline(this Sketch sketch, IReadOnlyList<Vec2> points, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(points);
        var count = closed ? points.Count : points.Count - 1;
        var lines = new List<SketchLine>();
        for (var i = 0; i < count; i++)
        {
            lines.Add(sketch.AddLine(points[i], points[(i + 1) % points.Count]));
        }

        for (var i = 1; i < lines.Count; i++)
        {
            sketch.AddConstraint(new CoincidentConstraint(lines[i - 1].End, lines[i].Start));
        }

        if (closed && lines.Count > 2)
        {
            sketch.AddConstraint(new CoincidentConstraint(lines[^1].End, lines[0].Start));
        }

        return lines;
    }

    /// <summary>An axis-aligned rectangle from two opposite corners. Returns bottom, right, top, left (counter-clockwise).</summary>
    public static IReadOnlyList<SketchLine> AddCornerRectangle(this Sketch sketch, Vec2 corner1, Vec2 corner2)
    {
        var min = new Vec2(Math.Min(corner1.X, corner2.X), Math.Min(corner1.Y, corner2.Y));
        var max = new Vec2(Math.Max(corner1.X, corner2.X), Math.Max(corner1.Y, corner2.Y));
        var lines = sketch.AddPolyline([min, new Vec2(max.X, min.Y), max, new Vec2(min.X, max.Y)], closed: true);
        sketch.AddConstraint(new HorizontalConstraint(lines[0]));
        sketch.AddConstraint(new VerticalConstraint(lines[1]));
        sketch.AddConstraint(new HorizontalConstraint(lines[2]));
        sketch.AddConstraint(new VerticalConstraint(lines[3]));
        return lines;
    }

    /// <summary>
    /// An axis-aligned rectangle around a center point, with construction diagonals that cross at the center.
    /// Returns the four sides; the center point and diagonals are added to the sketch as well.
    /// </summary>
    public static IReadOnlyList<SketchLine> AddCenterRectangle(this Sketch sketch, Vec2 center, Vec2 corner)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        var opposite = center * 2 - corner;
        var sides = sketch.AddCornerRectangle(corner, opposite);
        var diagonal1 = sketch.AddLine(sides[0].Start.Position, sides[1].End.Position, construction: true);
        var diagonal2 = sketch.AddLine(sides[1].Start.Position, sides[2].End.Position, construction: true);
        sketch.AddConstraint(new CoincidentConstraint(diagonal1.Start, sides[0].Start));
        sketch.AddConstraint(new CoincidentConstraint(diagonal1.End, sides[1].End));
        sketch.AddConstraint(new CoincidentConstraint(diagonal2.Start, sides[1].Start));
        sketch.AddConstraint(new CoincidentConstraint(diagonal2.End, sides[2].End));
        // A rectangle's diagonals always bisect each other, so one midpoint relation is enough;
        // a second one would be redundant and show the sketch as over-defined.
        var centerPoint = sketch.AddPoint(center);
        sketch.AddConstraint(new MidpointConstraint(centerPoint, diagonal1));
        return sides;
    }

    /// <summary>
    /// A rectangle at any angle from three points: two corners along the first side, then a point setting the width.
    /// </summary>
    public static IReadOnlyList<SketchLine> AddThreePointRectangle(this Sketch sketch, Vec2 p1, Vec2 p2, Vec2 widthPoint)
    {
        var side = p2 - p1;
        var normal = side.Perp().Normalized();
        var width = Vec2.Dot(widthPoint - p1, normal);
        var p3 = p2 + normal * width;
        var p4 = p1 + normal * width;
        var lines = sketch.AddPolyline([p1, p2, p3, p4], closed: true);
        sketch.AddConstraint(new PerpendicularConstraint(lines[0], lines[1]));
        sketch.AddConstraint(new ParallelConstraint(lines[0], lines[2]));
        sketch.AddConstraint(new ParallelConstraint(lines[1], lines[3]));
        return lines;
    }

    /// <summary>
    /// A regular polygon inscribed in a construction circle: every vertex lies on the circle and every side
    /// has equal length.
    /// </summary>
    public static IReadOnlyList<SketchLine> AddPolygon(this Sketch sketch, Vec2 center, Vec2 firstVertex, int sides)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 3);
        var radius = Vec2.Distance(center, firstVertex);
        var start = (firstVertex - center).Angle;
        var vertices = Enumerable.Range(0, sides)
            .Select(i => center + Vec2.FromAngle(start + 2 * Math.PI * i / sides, radius))
            .ToArray();
        var circle = sketch.AddCircle(center, radius, construction: true);
        var lines = sketch.AddPolyline(vertices, closed: true);
        foreach (var line in lines)
        {
            sketch.AddConstraint(new PointOnCircleConstraint(line.Start, circle));
        }

        for (var i = 1; i < lines.Count; i++)
        {
            sketch.AddConstraint(new EqualConstraint(lines[0], lines[i]));
        }

        return lines;
    }

    /// <summary>
    /// A straight slot: two parallel lines capped by tangent semicircular arcs, built around a construction
    /// centerline from <paramref name="center1"/> to <paramref name="center2"/>.
    /// </summary>
    public static (SketchLine Side1, SketchLine Side2, SketchArc Cap1, SketchArc Cap2, SketchLine Centerline) AddSlot(
        this Sketch sketch, Vec2 center1, Vec2 center2, double radius)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        var axis = (center2 - center1).Normalized();
        if (axis == Vec2.Zero)
        {
            axis = Vec2.UnitX;
        }

        var n = axis.Perp() * radius;
        var centerline = sketch.AddLine(center1, center2, construction: true);
        // Side 1 runs along the right of the axis (center1 → center2), side 2 back along the left.
        var side1 = sketch.AddLine(center1 - n, center2 - n);
        var cap2 = sketch.AddArc(center2, center2 - n, center2 + n);
        var side2 = sketch.AddLine(center2 + n, center1 + n);
        var cap1 = sketch.AddArc(center1, center1 + n, center1 - n);

        sketch.AddConstraint(new CoincidentConstraint(side1.End, cap2.Start));
        sketch.AddConstraint(new CoincidentConstraint(cap2.End, side2.Start));
        sketch.AddConstraint(new CoincidentConstraint(side2.End, cap1.Start));
        sketch.AddConstraint(new CoincidentConstraint(cap1.End, side1.Start));
        sketch.AddConstraint(new CoincidentConstraint(cap1.Center, centerline.Start));
        sketch.AddConstraint(new CoincidentConstraint(cap2.Center, centerline.End));
        sketch.AddConstraint(new TangentConstraint(side1, cap2));
        sketch.AddConstraint(new TangentConstraint(side2, cap2));
        sketch.AddConstraint(new TangentConstraint(side1, cap1));
        sketch.AddConstraint(new TangentConstraint(side2, cap1));
        sketch.AddConstraint(new EqualConstraint(cap1, cap2));
        return (side1, side2, cap1, cap2, centerline);
    }
}
