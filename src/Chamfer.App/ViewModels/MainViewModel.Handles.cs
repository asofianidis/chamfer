using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Viewport;

namespace Chamfer.App.ViewModels;

/// <summary>Drag handles on features being edited (Instant3D-style): drag the arrow to change a size.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Pick id of the active drag handle.</summary>
    public const int HandlePickId = 2_000_000_000;

    /// <summary>The handle's axis: where the measured size starts, and the direction it grows.</summary>
    private (Vec3 Base, Vec3 Direction, double Scale, Func<double> Read, Action<double> Write)? _handle;

    public bool IsHandle(int id) => id == HandlePickId && _handle is not null;

    /// <summary>The current handle as (tip, direction), if any (for tests and the scene).</summary>
    public (Vec3 Tip, Vec3 Direction)? ActiveHandle =>
        _handle is { } h ? (h.Base + (h.Direction * (h.Read() * h.Scale)), h.Direction) : null;

    /// <summary>Moves the handle to where the mouse ray passes closest to its axis.</summary>
    public void DragHandle(Vec3 rayOrigin, Vec3 rayDirection)
    {
        if (_handle is not { } h)
        {
            return;
        }

        // Closest point on the axis (Base + s·d) to the ray (O + u·e).
        var d = h.Direction;
        var e = rayDirection.Normalized();
        var de = Vec3.Dot(d, e);
        var denom = 1 - (de * de);
        if (denom < 1e-9)
        {
            return; // looking straight down the axis
        }

        var w = rayOrigin - h.Base;
        var s = (Vec3.Dot(w, d) - (Vec3.Dot(w, e) * de)) / denom;
        var value = Math.Max(0.01, s / h.Scale);
        h.Write(Math.Round(value, 3));
    }

    /// <summary>Sets up the handle for the feature being edited, and adds it to the scene.</summary>
    private void AddHandle(Scene scene)
    {
        _handle = null;
        if (Editor?.Feature is not ExtrudeFeature ex || !Document.State.Sketches.TryGetValue(ex.SketchId, out var sketch))
        {
            return;
        }

        var depthField = Editor.Fields.OfType<NumberField>().FirstOrDefault(f => f.Label == "Depth");
        if (depthField is null)
        {
            return;
        }

        // From the middle of the sketch, along the extrusion.
        var pts = sketch.Sketch.Entities.SelectMany(en => en.Points).Select(p => sketch.Plane.ToWorld(p.X, p.Y)).ToList();
        if (pts.Count == 0)
        {
            return;
        }

        var center = pts.Aggregate(Vec3.Zero, (a, b) => a + b) / pts.Count;
        var dir = sketch.Plane.Normal * (ex.Reverse ? -1 : 1);
        var scale = 1.0;
        if (ex.EndCondition != EndCondition.Blind)
        {
            return; // only blind depths are dragged
        }

        _handle = (center, dir, scale, () => ex.Depth, v =>
        {
            depthField.Text = v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "mm"; // explicit: the field may show inches
            depthField.Commit();
        });

        var tip = center + (dir * (ex.Depth * scale));
        var size = Math.Max(5, scene.Bounds.Diagonal * 0.12);
        var head = tip + (dir * size);
        var side = dir.AnyPerpendicular().Normalized() * (size * 0.2);
        var color = Rgba.FromHex(0xF59E0B);
        scene.Lines.Add(new LineItem { Points = [tip, head], Color = color, Width = 4, PickId = HandlePickId, OnTop = true });
        scene.Lines.Add(new LineItem { Points = [head - (dir * (size * 0.35)) + side, head, head - (dir * (size * 0.35)) - side], Color = color, Width = 3, PickId = HandlePickId, OnTop = true });
    }
}
