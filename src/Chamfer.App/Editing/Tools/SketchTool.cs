using Chamfer.Sketching;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Editing.Tools;

/// <summary>Pointer input already converted to sketch coordinates.</summary>
/// <param name="World">Cursor position in sketch coordinates.</param>
/// <param name="Snap">Where the cursor snaps to (only computed for tools that use snapping).</param>
/// <param name="Toggle">The add-to-selection modifier (Ctrl on Windows, Cmd on macOS) or Shift.</param>
/// <param name="ClickCount">2 for a double-click.</param>
public readonly record struct ToolPointer(Vec2 World, Snap Snap, bool Toggle, int ClickCount);

/// <summary>An interactive sketch tool. The active tool receives all pointer input on the canvas.</summary>
public abstract class SketchTool
{
    protected SketchEditor Editor { get; private set; } = null!;

    /// <summary>Stable id used for toolbar state and shortcuts.</summary>
    public abstract string Id { get; }

    /// <summary>Instruction shown in the status bar.</summary>
    public virtual string Hint => string.Empty;

    /// <summary>Whether the canvas should compute snaps for this tool.</summary>
    public virtual bool UsesSnapping => true;

    /// <summary>Temporary geometry to draw on top of the sketch.</summary>
    public virtual IEnumerable<PreviewItem> Preview => [];

    internal void Attach(SketchEditor editor)
    {
        Editor = editor;
        Reset();
    }

    /// <summary>Forgets any half-finished operation without touching the sketch.</summary>
    public virtual void Reset()
    {
    }

    public virtual void PointerDown(ToolPointer p)
    {
    }

    public virtual void PointerMove(ToolPointer p)
    {
    }

    public virtual void PointerUp(ToolPointer p)
    {
    }

    /// <summary>Escape. Returns true if there was something to cancel; false lets the editor switch to Select.</summary>
    public virtual bool Cancel()
    {
        Reset();
        return false;
    }

    /// <summary>Enter, or right-click: finish the current operation (for example, end a line chain).</summary>
    public virtual void Finish() => Reset();

    /// <summary>Sample points along an entity, used for box selection.</summary>
    protected static IEnumerable<Vec2> Sample(SketchEntity e, int n = 24)
    {
        switch (e)
        {
            case SketchPoint p:
                yield return p.Position;
                break;
            case SketchLine l:
                for (var i = 0; i <= n; i++)
                {
                    yield return Vec2.Lerp(l.Start.Position, l.End.Position, (double)i / n);
                }

                break;
            case SketchCircle c:
                for (var i = 0; i < n; i++)
                {
                    yield return c.Center.Position + Vec2.FromAngle(2 * Math.PI * i / n, c.Radius);
                }

                break;
            case SketchEllipse el:
                foreach (var q in el.Sample(n))
                {
                    yield return q;
                }

                break;
            case SketchSpline sp:
                foreach (var q in sp.Sample(Math.Max(2, n / Math.Max(1, sp.Points.Count - 1))))
                {
                    yield return q;
                }

                break;
            case SketchArc a:
                for (var i = 0; i <= n; i++)
                {
                    yield return a.Center.Position + Vec2.FromAngle(a.StartAngle + a.Sweep * i / n, a.Radius);
                }

                break;
        }
    }
}
