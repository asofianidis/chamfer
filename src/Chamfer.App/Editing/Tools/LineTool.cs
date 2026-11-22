using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Editing.Tools;

/// <summary>
/// Draws connected lines: each click ends one segment and starts the next. Lines within a few degrees
/// of horizontal or vertical snap to it and get that relation. The chain ends on Esc, Enter,
/// right-click, a double-click, or when a segment ends on an existing point.
/// </summary>
public sealed class LineTool(bool construction) : SketchTool
{
    private static readonly double InferenceSlope = Math.Tan(3 * Math.PI / 180);

    private Snap? _start;
    private SketchPoint? _chainFrom;
    private Snap _cursor;
    private Vec2 _end;
    private ConstraintKind? _axis;
    private Vec2? _downAt;
    private bool _startedOnThisPress;

    public override string Id => construction ? "centerline" : "line";

    public override string Hint => _start is null
        ? $"Click to start the {(construction ? "centerline" : "line")}."
        : "Click to end this segment and start the next. Esc, Enter or right-click to stop.";

    public override IEnumerable<PreviewItem> Preview
    {
        get
        {
            var items = new List<PreviewItem>();
            if (_start is { } start)
            {
                items.Add(new PreviewLine(start.Position, _end, construction));
            }

            var glyph = _axis switch
            {
                ConstraintKind.Horizontal => "H",
                ConstraintKind.Vertical => "V",
                _ => _cursor.Glyph,
            };
            if (glyph is not null)
            {
                items.Add(new PreviewGlyph(_end, glyph));
            }

            return items;
        }
    }

    public override void Reset()
    {
        _start = null;
        _chainFrom = null;
        _axis = null;
        _downAt = null;
    }

    public override void PointerMove(ToolPointer p)
    {
        _cursor = p.Snap;
        _end = p.Snap.Position;
        _axis = null;
        if (_start is { } start && p.Snap.Kind == SnapKind.None)
        {
            var d = p.World - start.Position;
            if (Math.Abs(d.Y) <= InferenceSlope * Math.Abs(d.X))
            {
                _end = new Vec2(p.World.X, start.Position.Y);
                _axis = ConstraintKind.Horizontal;
            }
            else if (Math.Abs(d.X) <= InferenceSlope * Math.Abs(d.Y))
            {
                _end = new Vec2(start.Position.X, p.World.Y);
                _axis = ConstraintKind.Vertical;
            }
        }

        Editor.Hover = p.Snap.Target;
        Editor.Invalidate();
    }

    public override void PointerDown(ToolPointer p)
    {
        PointerMove(p);
        _downAt = p.World;
        _startedOnThisPress = _start is null;
        if (p.ClickCount >= 2)
        {
            Reset();
            Editor.Invalidate();
            return;
        }

        Place();
    }

    public override void PointerUp(ToolPointer p)
    {
        // Click-drag-release draws a single segment.
        if (_startedOnThisPress && _downAt is { } down && Vec2.Distance(down, p.World) > 6 * Editor.PixelSize)
        {
            PointerMove(p);
            Place();
        }

        _downAt = null;
        _startedOnThisPress = false;
    }

    public override bool Cancel()
    {
        var busy = _start is not null;
        Reset();
        Editor.Invalidate();
        return busy;
    }

    private void Place()
    {
        if (_start is not { } start)
        {
            _start = _cursor;
            Editor.Invalidate();
            return;
        }

        if (Vec2.Distance(start.Position, _end) < 1e-9)
        {
            return;
        }

        Editor.BeginChange();
        var line = Editor.Sketch.AddLine(start.Position, _end, construction);
        var relations = new List<SketchConstraint>();
        if (_chainFrom is not null)
        {
            relations.Add(new CoincidentConstraint(line.Start, _chainFrom));
        }
        else if (start.RelationFor(line.Start) is { } startRelation)
        {
            relations.Add(startRelation);
        }

        if (_axis is null && _cursor.RelationFor(line.End) is { } endRelation)
        {
            relations.Add(endRelation);
        }

        if (_axis == ConstraintKind.Horizontal)
        {
            relations.Add(new HorizontalConstraint(line));
        }
        else if (_axis == ConstraintKind.Vertical)
        {
            relations.Add(new VerticalConstraint(line));
        }

        Editor.AddAutoRelations(relations);
        Editor.EndChange(solve: true);

        if (_axis is null && _cursor.Kind == SnapKind.Point)
        {
            // Closed onto existing geometry: stop chaining.
            Reset();
        }
        else
        {
            _chainFrom = line.End;
            _start = new Snap(SnapKind.Point, line.End.Position, line.End);
        }

        Editor.Invalidate();
    }
}
