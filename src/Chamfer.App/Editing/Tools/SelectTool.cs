using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Editing.Tools;

/// <summary>
/// Selects entities and dimensions, drags geometry (the solver keeps relations satisfied), moves
/// dimension labels, and box-selects. Dragging left-to-right selects what's fully inside the box;
/// right-to-left selects anything the box touches.
/// </summary>
public sealed class SelectTool : SketchTool
{
    private enum State
    {
        Idle,
        PressedEntity,
        DraggingEntity,
        PressedDimension,
        DraggingDimension,
        PressedEmpty,
        BoxSelecting,
    }

    private State _state;
    private Vec2 _down;
    private Vec2 _current;
    private bool _toggle;
    private SketchEntity? _pressed;
    private DimensionConstraint? _pressedDimension;
    private Dictionary<Param, double> _start = [];

    public override string Id => "select";

    public override string Hint => _state switch
    {
        State.DraggingEntity => "Dragging: relations and dimensions are kept.",
        State.BoxSelecting => "Drag right to select enclosed entities, left to select anything touched.",
        _ => "Click to select, drag geometry to move it, double-click a dimension to edit its value.",
    };

    public override bool UsesSnapping => false;

    public override IEnumerable<PreviewItem> Preview =>
        _state == State.BoxSelecting ? [new PreviewBox(_down, _current, Crossing: _current.X < _down.X)] : [];

    public override void Reset()
    {
        _state = State.Idle;
        _pressed = null;
        _pressedDimension = null;
    }

    public override void PointerDown(ToolPointer p)
    {
        _down = p.World;
        _current = p.World;
        _toggle = p.Toggle;

        if (Editor.HitDimension(p.World) is { } dim)
        {
            if (p.ClickCount >= 2)
            {
                Editor.RequestDimensionEdit(dim);
                _state = State.Idle;
                return;
            }

            if (!p.Toggle && !Editor.IsSelected(dim))
            {
                Editor.ClearSelection();
            }

            Editor.Select(dim, toggle: p.Toggle);
            _pressedDimension = dim;
            _state = State.PressedDimension;
            return;
        }

        if (Editor.HitEntity(p.World) is { } entity)
        {
            if (p.Toggle)
            {
                Editor.Select(entity, toggle: true);
            }
            else if (!Editor.IsSelected(entity))
            {
                Editor.ClearSelection();
                Editor.Select(entity);
            }

            _pressed = entity;
            _state = State.PressedEntity;
            return;
        }

        _state = State.PressedEmpty;
    }

    public override void PointerMove(ToolPointer p)
    {
        _current = p.World;
        var moved = Vec2.Distance(p.World, _down) > 4 * Editor.PixelSize;
        switch (_state)
        {
            case State.Idle:
                UpdateHover(p.World);
                break;

            case State.PressedEntity when moved && _pressed is not null && !ReferenceEquals(_pressed, Editor.Sketch.Origin):
                Editor.BeginChange();
                _start = Editor.Sketch.TakeSnapshot();
                _state = State.DraggingEntity;
                DragTo(p.World);
                break;

            case State.DraggingEntity:
                DragTo(p.World);
                break;

            case State.PressedDimension when moved:
                Editor.BeginChange();
                _state = State.DraggingDimension;
                MoveLabel(p.World);
                break;

            case State.DraggingDimension:
                MoveLabel(p.World);
                break;

            case State.PressedEmpty when moved:
                _state = State.BoxSelecting;
                Editor.Invalidate();
                break;

            case State.BoxSelecting:
                Editor.Invalidate();
                break;
        }
    }

    public override void PointerUp(ToolPointer p)
    {
        switch (_state)
        {
            case State.DraggingEntity:
            case State.DraggingDimension:
                Editor.EndChange(solve: false);
                break;

            case State.PressedEntity when !_toggle && _pressed is not null:
                // A plain click on part of a multi-selection selects just that entity.
                Editor.ClearSelection();
                Editor.Select(_pressed);
                break;

            case State.PressedEmpty when !_toggle:
                Editor.ClearSelection();
                break;

            case State.BoxSelecting:
                SelectInBox(_down, p.World, crossing: p.World.X < _down.X, add: _toggle);
                break;
        }

        Reset();
        Editor.Invalidate();
    }

    public override bool Cancel()
    {
        var busy = _state is State.DraggingEntity or State.DraggingDimension;
        if (busy)
        {
            Editor.CancelChange();
        }

        Reset();
        if (!busy && (Editor.Selection.Count > 0 || Editor.SelectedConstraints.Count > 0))
        {
            Editor.ClearSelection();
            return true;
        }

        return busy;
    }

    private void UpdateHover(Vec2 world)
    {
        var dim = Editor.HitDimension(world);
        var entity = dim is null ? Editor.HitEntity(world) : null;
        if (!ReferenceEquals(dim, Editor.HoverDimension) || !ReferenceEquals(entity, Editor.Hover))
        {
            Editor.HoverDimension = dim;
            Editor.Hover = entity;
            Editor.Invalidate();
        }
    }

    private void DragTo(Vec2 world)
    {
        var delta = world - _down;
        List<(Param, double)> targets = [];

        void Translate(SketchPoint point)
        {
            targets.Add((point.XParam, _start[point.XParam] + delta.X));
            targets.Add((point.YParam, _start[point.YParam] + delta.Y));
        }

        switch (_pressed)
        {
            case SketchPoint point:
                Translate(point);
                break;
            case SketchLine line:
                Translate(line.Start);
                Translate(line.End);
                break;
            case SketchCircle circle:
                // Grabbing the rim resizes the circle.
                targets.Add((circle.RadiusParam, Vec2.Distance(world, circle.Center.Position)));
                break;
            case SketchEllipse ellipse:
                Translate(ellipse.Center);
                Translate(ellipse.MajorEnd);
                break;
            case SketchArc arc:
                Translate(arc.Center);
                Translate(arc.Start);
                Translate(arc.End);
                break;
        }

        if (targets.Count > 0)
        {
            // Each drag step starts from the previous solution, which keeps motion smooth.
            Editor.Sketch.Drag(targets);
            Editor.Invalidate();
        }
    }

    private void MoveLabel(Vec2 world)
    {
        if (_pressedDimension is { } dim)
        {
            dim.LabelOffset = world - DimensionGeometry.Anchor(dim);
            Editor.Invalidate();
        }
    }

    private void SelectInBox(Vec2 a, Vec2 b, bool crossing, bool add)
    {
        var min = new Vec2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        var max = new Vec2(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        bool Inside(Vec2 v) => v.X >= min.X && v.X <= max.X && v.Y >= min.Y && v.Y <= max.Y;

        var hits = Editor.Sketch.Entities.Where(e =>
        {
            var samples = Sample(e).ToList();
            return crossing ? samples.Any(Inside) : samples.All(Inside);
        });
        Editor.SelectMany(hits, add);
    }
}
