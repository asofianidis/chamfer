using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Editing.Tools;

/// <summary>
/// Smart Dimension: pick one or two entities, then click to place. The kind of dimension follows from
/// what was picked and where the label goes. For a line or two points, placing the label above or below
/// gives a horizontal dimension, left or right gives a vertical one, and anywhere else gives an aligned one.
/// </summary>
public sealed class DimensionTool : SketchTool
{
    private readonly List<SketchEntity> _picks = [];
    private DimensionConstraint? _candidate;

    public override string Id => "dimension";

    public override string Hint => _picks.Count switch
    {
        0 => "Pick a line, circle, arc or point to dimension. Click an existing dimension to edit it.",
        1 when _candidate is null => "Pick a second entity.",
        _ => "Pick another entity, or click empty space to place the dimension.",
    };

    public override bool UsesSnapping => false;

    public override IEnumerable<PreviewItem> Preview => _candidate is null ? [] : [new PreviewDimension(_candidate)];

    public override void Reset()
    {
        _picks.Clear();
        _candidate = null;
    }

    public override void PointerMove(ToolPointer p)
    {
        var hit = Editor.HitEntity(p.World);
        Editor.Hover = hit;
        Editor.HoverDimension = _picks.Count == 0 ? Editor.HitDimension(p.World) : null;
        _candidate = Build(_picks, p.World);
        if (_candidate is not null)
        {
            _candidate.LabelOffset = p.World - DimensionGeometry.Anchor(_candidate);
        }

        Editor.Invalidate();
    }

    public override void PointerDown(ToolPointer p)
    {
        if (_picks.Count == 0 && Editor.HitDimension(p.World) is { } existing)
        {
            Editor.RequestDimensionEdit(existing);
            return;
        }

        // Any entity can be the first pick (a lone point waits for a second one); a second pick must
        // combine with the first into a valid dimension.
        var hit = Editor.HitEntity(p.World);
        var canPick = hit is not null && !_picks.Contains(hit)
            && (_picks.Count == 0 || (_picks.Count == 1 && Build([_picks[0], hit], p.World) is not null));
        if (canPick)
        {
            _picks.Add(hit!);
            Editor.SelectMany(_picks, add: false);
            PointerMove(p);
            return;
        }

        if (_candidate is { } dim)
        {
            Reset();
            Editor.ClearSelection();
            Editor.AddDimension(dim);
        }
    }

    public override bool Cancel()
    {
        var busy = _picks.Count > 0;
        Reset();
        Editor.ClearSelection();
        return busy;
    }

    /// <summary>The dimension implied by the picks and the label position, or null if there isn't one yet.</summary>
    internal static DimensionConstraint? Build(IReadOnlyList<SketchEntity> picks, Vec2 label)
    {
        switch (picks)
        {
            case [SketchLine line]:
                return Linear(line.Start, line.End, label, line);
            case [SketchCircle circle]:
                return new RadialDimensionConstraint(circle, diameter: true);
            case [SketchArc arc]:
                return new RadialDimensionConstraint(arc, diameter: false);
            case [SketchPoint a, SketchPoint b]:
                return Linear(a, b, label, null);
            case [SketchPoint p, SketchLine l]:
                return new PointLineDistanceConstraint(p, l);
            case [SketchLine l, SketchPoint p]:
                return new PointLineDistanceConstraint(p, l);
            case [SketchLine a, SketchLine b]:
            {
                var sin = Math.Abs(Vec2.Cross(a.Direction.Normalized(), b.Direction.Normalized()));
                return sin < 1e-6 ? new PointLineDistanceConstraint(b.Start, a) : new AngleConstraint(a, b);
            }

            case [SketchPoint p, IRadial r]:
                return Linear(p, r.Center, label, null);
            case [IRadial r, SketchPoint p]:
                return Linear(r.Center, p, label, null);
            case [IRadial r1, IRadial r2]:
                return Linear(r1.Center, r2.Center, label, null);
            case [SketchLine l, IRadial r]:
                return new PointLineDistanceConstraint(r.Center, l);
            case [IRadial r, SketchLine l]:
                return new PointLineDistanceConstraint(r.Center, l);
            default:
                return null;
        }
    }

    private static DimensionConstraint Linear(SketchPoint a, SketchPoint b, Vec2 label, SketchLine? line)
    {
        var min = new Vec2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        var max = new Vec2(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var size = max - min;
        var axisAligned = size.X < 1e-9 * Math.Max(1, size.Y) || size.Y < 1e-9 * Math.Max(1, size.X);
        if (!axisAligned)
        {
            var withinX = label.X >= min.X && label.X <= max.X;
            var withinY = label.Y >= min.Y && label.Y <= max.Y;
            if (withinX && !withinY)
            {
                return new AxisDistanceConstraint(a, b, vertical: false);
            }

            if (withinY && !withinX)
            {
                return new AxisDistanceConstraint(a, b, vertical: true);
            }
        }

        return line is null ? new DistanceConstraint(a, b) : new DistanceConstraint(line);
    }
}
