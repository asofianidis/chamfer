using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Editing.Tools;

/// <summary>Helpers shared by the shape tools.</summary>
internal static class ShapeToolExtensions
{
    /// <summary>Runs <paramref name="build"/> and returns every point belonging to the entities it added.</summary>
    public static List<SketchPoint> PointsAddedBy(this Sketch sketch, Action build)
    {
        var before = sketch.Entities.Count;
        build();
        return [.. sketch.Entities.Skip(before).SelectMany(e => e is SketchPoint p ? [p] : e.Points)];
    }
}

public sealed class PointTool : ClickTool
{
    public override string Id => "point";
    protected override int ClicksNeeded => 1;
    protected override IReadOnlyList<string> Steps => ["Click to place a point."];

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p) => [];

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        var point = Editor.Sketch.AddPoint(clicks[0].Position);
        RelateClicks([point], clicks);
    }
}

public sealed class CornerRectangleTool : ClickTool
{
    public override string Id => "rect-corner";
    protected override int ClicksNeeded => 2;
    protected override IReadOnlyList<string> Steps => ["Click the first corner.", "Click the opposite corner."];

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p) =>
        Polygon([p[0], new Vec2(p[1].X, p[0].Y), p[1], new Vec2(p[0].X, p[1].Y)]);

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        if (Math.Abs(clicks[0].Position.X - clicks[1].Position.X) < 1e-9 || Math.Abs(clicks[0].Position.Y - clicks[1].Position.Y) < 1e-9)
        {
            throw new ArgumentException("A rectangle needs width and height.");
        }

        var points = Editor.Sketch.PointsAddedBy(() => Editor.Sketch.AddCornerRectangle(clicks[0].Position, clicks[1].Position));
        RelateClicks(points, clicks);
    }
}

public sealed class CenterRectangleTool : ClickTool
{
    public override string Id => "rect-center";
    protected override int ClicksNeeded => 2;
    protected override IReadOnlyList<string> Steps => ["Click the center.", "Click a corner."];

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p)
    {
        var c = p[0];
        var k = p[1];
        var o = c * 2 - k;
        return [.. Polygon([k, new Vec2(o.X, k.Y), o, new Vec2(k.X, o.Y)]), new PreviewLine(k, o, true), new PreviewLine(new Vec2(o.X, k.Y), new Vec2(k.X, o.Y), true)];
    }

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        var (c, k) = (clicks[0].Position, clicks[1].Position);
        if (Math.Abs(c.X - k.X) < 1e-9 || Math.Abs(c.Y - k.Y) < 1e-9)
        {
            throw new ArgumentException("A rectangle needs width and height.");
        }

        var points = Editor.Sketch.PointsAddedBy(() => Editor.Sketch.AddCenterRectangle(c, k));
        RelateClicks(points, clicks);
    }
}

public sealed class ThreePointRectangleTool : ClickTool
{
    public override string Id => "rect-3pt";
    protected override int ClicksNeeded => 3;
    protected override IReadOnlyList<string> Steps => ["Click the first corner.", "Click the second corner.", "Click to set the width."];

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p)
    {
        if (p.Count == 2)
        {
            return [new PreviewLine(p[0], p[1])];
        }

        var n = (p[1] - p[0]).Perp().Normalized();
        var w = Vec2.Dot(p[2] - p[0], n);
        return Polygon([p[0], p[1], p[1] + n * w, p[0] + n * w]);
    }

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        var points = Editor.Sketch.PointsAddedBy(() =>
            Editor.Sketch.AddThreePointRectangle(clicks[0].Position, clicks[1].Position, clicks[2].Position));
        RelateClicks(points, [clicks[0], clicks[1]]);
    }
}

public sealed class CircleTool : ClickTool
{
    public override string Id => "circle";
    protected override int ClicksNeeded => 2;
    protected override IReadOnlyList<string> Steps => ["Click the center.", "Click to set the radius."];

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p) =>
        [new PreviewCircle(p[0], Vec2.Distance(p[0], p[1])), new PreviewLine(p[0], p[1], true)];

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        var radius = Vec2.Distance(clicks[0].Position, clicks[1].Position);
        if (radius < 1e-9)
        {
            throw new ArgumentException("A circle needs a radius.");
        }

        var circle = Editor.Sketch.AddCircle(clicks[0].Position, radius);
        RelateClicks(circle, [clicks[0]]);

        // The rim click landing on an existing point puts that point on the circle.
        if (clicks[1] is { Kind: SnapKind.Point, Target: SketchPoint rimPoint })
        {
            Editor.AddAutoRelations([new PointOnCircleConstraint(rimPoint, circle)]);
        }
    }
}

public sealed class EllipseTool : ClickTool
{
    public override string Id => "ellipse";
    protected override int ClicksNeeded => 3;
    protected override IReadOnlyList<string> Steps => ["Click the center.", "Click the end of the major axis.", "Click to set the minor axis."];

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p)
    {
        if (p.Count == 2)
        {
            return [new PreviewLine(p[0], p[1], true)];
        }

        var el = new Chamfer.Sketching.Entities.SketchEllipse(p[0], p[1], Minor(p));
        var pts = el.Sample(72).ToList();
        return [.. pts.Select((q, i) => (PreviewItem)new PreviewLine(q, pts[(i + 1) % pts.Count]))];
    }

    /// <summary>The minor semi-axis: the third click's distance from the major axis line.</summary>
    private static double Minor(IReadOnlyList<Vec2> p)
    {
        var axis = (p[1] - p[0]).Normalized();
        var d = p[2] - p[0];
        return Math.Abs((axis.X * d.Y) - (axis.Y * d.X));
    }

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        var p = clicks.Select(c => c.Position).ToList();
        var minor = Minor(p);
        if (Vec2.Distance(p[0], p[1]) < 1e-9 || minor < 1e-9)
        {
            throw new ArgumentException("An ellipse needs two axes.");
        }

        var ellipse = Editor.Sketch.AddEllipse(p[0], p[1], minor);
        RelateClicks(ellipse, [clicks[0]]);
    }
}

public sealed class ThreePointArcTool : ClickTool
{
    public override string Id => "arc-3pt";
    protected override int ClicksNeeded => 3;
    protected override IReadOnlyList<string> Steps => ["Click the start point.", "Click the end point.", "Click a point on the arc to set its curvature."];

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p)
    {
        if (p.Count == 2 || SketchArc.ThroughThreePoints(p[0], p[2], p[1]) is not { } arc)
        {
            return [new PreviewLine(p[0], p[1])];
        }

        return [new PreviewArc(arc.Center.Position, arc.Radius, arc.StartAngle, arc.Sweep)];
    }

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        var arc = SketchArc.ThroughThreePoints(clicks[0].Position, clicks[2].Position, clicks[1].Position)
            ?? throw new ArgumentException("The three points are in a line, so they don't define an arc.");
        Editor.Sketch.Add(arc);
        RelateClicks(arc, [clicks[0], clicks[1]]);
    }
}

/// <summary>Center, start, end. The arc follows the direction the cursor moves around the center.</summary>
public sealed class CenterArcTool : ClickTool
{
    private double _sweep;
    private double _lastAngle;
    private bool _tracking;

    public override string Id => "arc-center";
    protected override int ClicksNeeded => 3;
    protected override IReadOnlyList<string> Steps => ["Click the center.", "Click the start point.", "Move around the center and click the end point."];

    public override void Reset()
    {
        base.Reset();
        _sweep = 0;
        _tracking = false;
    }

    public override void PointerMove(ToolPointer p)
    {
        if (Clicks.Count == 2)
        {
            // Accumulate how far the cursor has travelled around the center, so arcs can go either
            // way and past 180°.
            var center = Clicks[0].Position;
            if (!_tracking)
            {
                _tracking = true;
                _sweep = 0;
                _lastAngle = (Clicks[1].Position - center).Angle;
            }

            var angle = (p.Snap.Position - center).Angle;
            var delta = Math.IEEERemainder(angle - _lastAngle, 2 * Math.PI);
            _sweep = Math.Clamp(_sweep + delta, -2 * Math.PI + 1e-3, 2 * Math.PI - 1e-3);
            _lastAngle = angle;
        }
        else
        {
            _tracking = false;
            _sweep = 0;
        }

        base.PointerMove(p);
    }

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p)
    {
        if (p.Count == 2)
        {
            return [new PreviewLine(p[0], p[1], true), new PreviewCircle(p[0], Vec2.Distance(p[0], p[1]), true)];
        }

        var (start, sweep) = StartAndSweep(p[0], p[1]);
        return [new PreviewArc(p[0], Vec2.Distance(p[0], p[1]), start, sweep), new PreviewLine(p[0], p[1], true)];
    }

    private (double Start, double Sweep) StartAndSweep(Vec2 center, Vec2 startPoint)
    {
        var a0 = (startPoint - center).Angle;
        return _sweep >= 0 ? (a0, _sweep) : (a0 + _sweep, -_sweep);
    }

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        var center = clicks[0].Position;
        var radius = Vec2.Distance(center, clicks[1].Position);
        if (radius < 1e-9 || Math.Abs(_sweep) < 1e-6)
        {
            throw new ArgumentException("Move around the center to give the arc some length.");
        }

        var (start, sweep) = StartAndSweep(center, clicks[1].Position);
        var arc = Editor.Sketch.AddArc(center, center + Vec2.FromAngle(start, radius), center + Vec2.FromAngle(start + sweep, radius));
        // The end click is projected onto the circle, so only the center and start can match a snap exactly.
        RelateClicks(arc, [clicks[0], clicks[1]]);
    }
}

public sealed class PolygonTool(Func<int> sides) : ClickTool
{
    public override string Id => "polygon";
    protected override int ClicksNeeded => 2;
    protected override IReadOnlyList<string> Steps => ["Click the center.", "Click to place a vertex."];

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p)
    {
        var n = Math.Max(3, sides());
        var r = Vec2.Distance(p[0], p[1]);
        var a0 = (p[1] - p[0]).Angle;
        var corners = Enumerable.Range(0, n).Select(i => p[0] + Vec2.FromAngle(a0 + 2 * Math.PI * i / n, r)).ToList();
        return [.. Polygon(corners), new PreviewCircle(p[0], r, true)];
    }

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        if (Vec2.Distance(clicks[0].Position, clicks[1].Position) < 1e-9)
        {
            throw new ArgumentException("A polygon needs a size.");
        }

        var points = Editor.Sketch.PointsAddedBy(() =>
            Editor.Sketch.AddPolygon(clicks[0].Position, clicks[1].Position, Math.Max(3, sides())));
        RelateClicks(points, clicks);
    }
}

public sealed class SlotTool : ClickTool
{
    public override string Id => "slot";
    protected override int ClicksNeeded => 3;
    protected override IReadOnlyList<string> Steps => ["Click the first arc center.", "Click the second arc center.", "Click to set the slot width."];

    protected override IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p)
    {
        if (p.Count == 2)
        {
            return [new PreviewLine(p[0], p[1], true)];
        }

        var r = Radius(p[0], p[1], p[2]);
        var axis = (p[1] - p[0]).Normalized();
        var n = axis.Perp() * r;
        var a = axis.Angle;
        return
        [
            new PreviewLine(p[0], p[1], true),
            new PreviewLine(p[0] - n, p[1] - n),
            new PreviewLine(p[0] + n, p[1] + n),
            new PreviewArc(p[1], r, a - Math.PI / 2, Math.PI),
            new PreviewArc(p[0], r, a + Math.PI / 2, Math.PI),
        ];
    }

    private static double Radius(Vec2 c1, Vec2 c2, Vec2 widthPoint)
    {
        var axis = (c2 - c1).Normalized();
        return Math.Abs(Vec2.Cross(axis, widthPoint - c1));
    }

    protected override void Create(IReadOnlyList<Snap> clicks)
    {
        var (c1, c2) = (clicks[0].Position, clicks[1].Position);
        var r = Radius(c1, c2, clicks[2].Position);
        if (Vec2.Distance(c1, c2) < 1e-9 || r < 1e-9)
        {
            throw new ArgumentException("A slot needs a length and a width.");
        }

        var points = Editor.Sketch.PointsAddedBy(() => Editor.Sketch.AddSlot(c1, c2, r));
        RelateClicks(points, [clicks[0], clicks[1]]);
    }
}
