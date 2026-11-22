using Chamfer.Document;
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

    public bool IsHandle(int id) => (id == HandlePickId && _handle is not null) || BeginComponentDrag(id);

    /// <summary>The current handle as (tip, direction), if any (for tests and the scene).</summary>
    public (Vec3 Tip, Vec3 Direction)? ActiveHandle =>
        _handle is { } h ? (h.Base + (h.Direction * (h.Read() * h.Scale)), h.Direction) : null;

    /// <summary>Moves the handle to where the mouse ray passes closest to its axis.</summary>
    public void DragHandle(Vec3 rayOrigin, Vec3 rayDirection)
    {
        if (_drag is not null)
        {
            DragComponent(rayOrigin, rayDirection);
            return;
        }

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
        if (Editor?.Feature is not { } feature || HandleFor(feature) is not { } h)
        {
            return;
        }

        var field = Editor.Fields.OfType<NumberField>().FirstOrDefault(f => f.Label == h.Field);
        if (field is null)
        {
            return;
        }

        var dir = h.Direction.Normalized();
        _handle = (h.Base, dir, 1.0, h.Read, v =>
        {
            field.Text = v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "mm"; // explicit: the field may show inches
            field.Commit();
        });

        var tip = h.Base + (dir * h.Read());
        var size = Math.Max(5, scene.Bounds.Diagonal * 0.12);
        var head = tip + (dir * size);
        var side = dir.AnyPerpendicular().Normalized() * (size * 0.2);
        var color = Rgba.FromHex(0xF59E0B);
        scene.Lines.Add(new LineItem { Points = [tip, head], Color = color, Width = 4, PickId = HandlePickId, OnTop = true });
        scene.Lines.Add(new LineItem { Points = [head - (dir * (size * 0.35)) + side, head, head - (dir * (size * 0.35)) - side], Color = color, Width = 3, PickId = HandlePickId, OnTop = true });
    }

    /// <summary>
    /// Where a feature's size handle sits: the point the size is measured from, the way it grows,
    /// the size now, and the panel field it sets. Null when the feature has no draggable size.
    /// </summary>
    private (Vec3 Base, Vec3 Direction, Func<double> Read, string Field)? HandleFor(Feature feature)
    {
        var before = Document.StateBefore(feature);
        (Body Body, int Edge)? Edge(TopoRef? r) =>
            r is null ? null : before.Bodies.FirstOrDefault(b => b.HasEdge(r.Name)) is { } b ? (b, b.ResolveEdge(r, out _)) : null;
        (Body Body, int Face)? Face(TopoRef? r) =>
            r is null ? null : before.Bodies.FirstOrDefault(b => b.HasFace(r.Name)) is { } b ? (b, b.ResolveFace(r, out _)) : null;

        // Out from the middle of an edge, between its two faces (the way a fillet or chamfer grows).
        (Vec3, Vec3)? FromEdge(TopoRef? r)
        {
            if (Edge(r) is not { } e)
            {
                return null;
            }

            var normals = e.Body.Shape.FacesOfEdge(e.Edge).Select(f => e.Body.Face(f).Normal).ToList();
            var outward = normals.Aggregate(Vec3.Zero, (a, n) => a + n);
            return outward.LengthSquared < 1e-12 ? null : (e.Body.Edge(e.Edge).Midpoint, outward);
        }

        switch (feature)
        {
            case ExtrudeFeature ex when ex.EndCondition == EndCondition.Blind && before.Sketches.TryGetValue(ex.SketchId, out var sketch):
            {
                // From the middle of the sketch, along the extrusion.
                var pts = sketch.Sketch.Entities.SelectMany(en => en.Points).Select(p => sketch.Plane.ToWorld(p.X, p.Y)).ToList();
                if (pts.Count == 0)
                {
                    return null;
                }

                var center = pts.Aggregate(Vec3.Zero, (a, b) => a + b) / pts.Count;
                return (center, sketch.Plane.Normal * (ex.Reverse ? -1 : 1), () => ex.Depth, "Depth");
            }

            case FilletFeature fil when fil.EndRadius is null && FromEdge(fil.Edges.FirstOrDefault()) is var (filletAt, filletDir):
                return (filletAt, filletDir, () => fil.Radius, "Radius");

            case ChamferFeature ch when FromEdge(ch.Edges.FirstOrDefault()) is var (chamferAt, chamferDir):
                return (chamferAt, chamferDir, () => ch.Distance, "Distance");

            case ShellFeature sh when Face(sh.FacesToRemove.FirstOrDefault()) is { } open:
            {
                // From the open face, into the part: the wall grows inward.
                var info = open.Body.Face(open.Face);
                return (info.Centroid, info.Normal * (sh.Outward ? 1 : -1), () => sh.Thickness, "Thickness");
            }

            case HoleFeature hole when hole.EndCondition == EndCondition.Blind && before.Sketches.TryGetValue(hole.SketchId, out var at)
                && at.Sketch.Entities.OfType<Chamfer.Sketching.Entities.SketchPoint>().FirstOrDefault(p => !p.IsConstruction) is { } first:
                return (at.Plane.ToWorld(first.Position.X, first.Position.Y), -at.Plane.Normal, () => hole.Depth, "Depth");

            default:
                return null;
        }
    }
}
