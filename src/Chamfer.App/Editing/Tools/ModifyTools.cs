using Chamfer.Sketching;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Operations;

namespace Chamfer.App.Editing.Tools;

/// <summary>Base for tools that act on whatever curve is under a click.</summary>
public abstract class CurveClickTool : SketchTool
{
    public override bool UsesSnapping => false;

    /// <summary>The curve nearest the cursor, ignoring points.</summary>
    protected SketchEntity? CurveAt(Vec2 p)
    {
        SketchEntity? best = null;
        var bestD = Editor.PickTolerance;
        foreach (var e in Editor.Sketch.Entities.Where(Curves.IsCurve))
        {
            var d = e.DistanceTo(p);
            if (d < bestD)
            {
                best = e;
                bestD = d;
            }
        }

        return best;
    }

    public override void PointerMove(ToolPointer p)
    {
        Editor.Hover = CurveAt(p.World);
        Editor.Invalidate();
    }

    public override void PointerDown(ToolPointer p)
    {
        if (CurveAt(p.World) is not { } target)
        {
            return;
        }

        Editor.BeginChange();
        try
        {
            if (Apply(target, p.World))
            {
                Editor.Hover = null;
                Editor.EndChange(solve: true);
            }
            else
            {
                Editor.CancelChange();
            }
        }
        catch (ArgumentException ex)
        {
            Editor.CancelChange();
            Editor.SetMessage(ex.Message);
        }
    }

    /// <summary>Edits the sketch; returns false if nothing changed.</summary>
    protected abstract bool Apply(SketchEntity target, Vec2 at);
}

/// <summary>Power trim: click the piece of a curve to remove, up to its nearest intersections.</summary>
public sealed class TrimTool : CurveClickTool
{
    public override string Id => "trim";
    public override string Hint => "Click the part of a curve to trim away. Curves that cross nothing are deleted.";

    protected override bool Apply(SketchEntity target, Vec2 at) => SketchOperations.Trim(Editor.Sketch, target, at);
}

/// <summary>Extends a line or arc to the next curve it meets.</summary>
public sealed class ExtendTool : CurveClickTool
{
    public override string Id => "extend";
    public override string Hint => "Click near the end of a line or arc to extend it to the next curve.";

    protected override bool Apply(SketchEntity target, Vec2 at)
    {
        if (!SketchOperations.Extend(Editor.Sketch, target, at))
        {
            Editor.SetMessage("Nothing to extend to in that direction.");
            return false;
        }

        return true;
    }
}

/// <summary>Rounds or bevels the corner where two lines meet.</summary>
public sealed class CornerTool(bool chamfer, Func<double> size) : SketchTool
{
    private Vec2 _cursor;

    public override string Id => chamfer ? "sketch-chamfer" : "sketch-fillet";

    public override string Hint => chamfer
        ? "Click a corner where two lines meet to chamfer it by the distance in the toolbar."
        : "Click a corner where two lines meet to round it with the radius in the toolbar.";

    public override bool UsesSnapping => false;

    public override IEnumerable<PreviewItem> Preview =>
        SketchOperations.FindCorner(Editor.Sketch, _cursor, Editor.PickTolerance * 2) is { } c
            ? [new PreviewCircle(c.Corner, Editor.PickTolerance)]
            : [];

    public override void PointerMove(ToolPointer p)
    {
        _cursor = p.World;
        Editor.Invalidate();
    }

    public override void PointerDown(ToolPointer p)
    {
        if (SketchOperations.FindCorner(Editor.Sketch, p.World, Editor.PickTolerance * 2) is not { } corner)
        {
            Editor.SetMessage("Click where two lines meet.");
            return;
        }

        Editor.BeginChange();
        try
        {
            if (chamfer)
            {
                SketchOperations.Chamfer(Editor.Sketch, corner.A, corner.B, size());
            }
            else
            {
                SketchOperations.Fillet(Editor.Sketch, corner.A, corner.B, size());
            }

            Editor.EndChange(solve: true);
        }
        catch (ArgumentException ex)
        {
            Editor.CancelChange();
            Editor.SetMessage(ex.Message);
        }
    }
}

/// <summary>Clicks model edges (drawn behind the sketch) to project them into the sketch.</summary>
public sealed class ConvertTool(Func<Vec2, bool> convertAt) : SketchTool
{
    public override string Id => "convert";
    public override string Hint => "Click model edges behind the sketch to convert them into fixed sketch geometry.";
    public override bool UsesSnapping => false;

    public override void PointerDown(ToolPointer p)
    {
        if (!convertAt(p.World))
        {
            Editor.SetMessage("Click on a model edge.");
        }
    }
}
