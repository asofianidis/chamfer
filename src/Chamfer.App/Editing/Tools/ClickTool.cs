using Chamfer.Sketching;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Editing.Tools;

/// <summary>
/// Base for tools that place a shape with a fixed number of clicks (for example, center then radius).
/// Click-click and click-drag-release both work. Each finished shape is one undo step, and any
/// click that snapped to existing geometry becomes a relation.
/// </summary>
public abstract class ClickTool : SketchTool
{
    private readonly List<Snap> _clicks = [];
    private Snap _cursor;
    private Vec2? _downAt;

    protected IReadOnlyList<Snap> Clicks => _clicks;

    /// <summary>The current cursor position (snapped).</summary>
    protected Vec2 Cursor => _cursor.Position;

    protected abstract int ClicksNeeded { get; }

    /// <summary>Hints for each click, in order.</summary>
    protected abstract IReadOnlyList<string> Steps { get; }

    public override string Hint => Steps[Math.Min(_clicks.Count, Steps.Count - 1)] + " Esc to cancel.";

    public override IEnumerable<PreviewItem> Preview
    {
        get
        {
            var points = _clicks.Select(c => c.Position).Append(_cursor.Position).ToList();
            var items = _clicks.Count > 0 ? ShapePreview(points).ToList() : [];
            if (_cursor.Glyph is { } glyph)
            {
                items.Add(new PreviewGlyph(_cursor.Position, glyph));
            }

            return items;
        }
    }

    /// <summary>Preview for the clicks so far plus the cursor, in <paramref name="p"/> (always at least two points).</summary>
    protected abstract IEnumerable<PreviewItem> ShapePreview(IReadOnlyList<Vec2> p);

    /// <summary>Adds the shape to the sketch. Called inside an undoable change.</summary>
    protected abstract void Create(IReadOnlyList<Snap> clicks);

    public override void Reset()
    {
        _clicks.Clear();
        _downAt = null;
    }

    public override void PointerMove(ToolPointer p)
    {
        _cursor = p.Snap;
        Editor.Hover = p.Snap.Target;
        Editor.Invalidate();
    }

    public override void PointerDown(ToolPointer p)
    {
        _downAt = p.World;
        AddClick(p.Snap);
    }

    public override void PointerUp(ToolPointer p)
    {
        // Click-drag-release: the release counts as the next click.
        if (_downAt is { } down && _clicks.Count > 0 && Vec2.Distance(down, p.World) > 6 * Editor.PixelSize)
        {
            AddClick(p.Snap);
        }

        _downAt = null;
    }

    public override bool Cancel()
    {
        var busy = _clicks.Count > 0;
        Reset();
        Editor.Invalidate();
        return busy;
    }

    private void AddClick(Snap snap)
    {
        // Ignore a repeated click on the same spot.
        if (_clicks.Count > 0 && Vec2.Distance(_clicks[^1].Position, snap.Position) < 1e-9)
        {
            return;
        }

        _clicks.Add(snap);
        if (_clicks.Count < ClicksNeeded)
        {
            Editor.Invalidate();
            return;
        }

        var clicks = _clicks.ToList();
        Reset();
        Editor.BeginChange();
        try
        {
            Create(clicks);
            Editor.EndChange(solve: true);
        }
        catch (ArgumentException ex)
        {
            Editor.CancelChange();
            Editor.SetMessage(ex.Message);
        }
    }

    /// <summary>Relates the new shape's points to the snapped clicks.</summary>
    protected void RelateClicks(SketchEntity shape, IReadOnlyList<Snap> clicks) =>
        RelateClicks(shape.Points, clicks);

    protected void RelateClicks(IEnumerable<SketchPoint> points, IReadOnlyList<Snap> clicks) =>
        Editor.AddSnapRelations(points, clicks);

    protected static IEnumerable<SketchPoint> PointsOf(IEnumerable<SketchEntity> entities) =>
        entities.SelectMany(e => e.Points);

    protected static IEnumerable<PreviewItem> Polygon(IReadOnlyList<Vec2> corners, bool construction = false)
    {
        for (var i = 0; i < corners.Count; i++)
        {
            yield return new PreviewLine(corners[i], corners[(i + 1) % corners.Count], construction);
        }
    }
}
