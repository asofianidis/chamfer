using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching.Entities;
using Chamfer.Viewport;

namespace Chamfer.App.Part;

/// <summary>Colours for the 3D view in one theme.</summary>
public sealed record ViewPalette(Rgba Top, Rgba Bottom, Rgba Body, Rgba Edge, Rgba Hover, Rgba Selected, Rgba Sketch, Rgba Plane, Rgba PlaneEdge, Rgba Preview)
{
    public static ViewPalette Light { get; } = new(
        Rgba.FromHex(0xF5F7FA), Rgba.FromHex(0xC3CCD6), Rgba.FromHex(0xB9C3CE), Rgba.FromHex(0x1F242B),
        Rgba.FromHex(0xF59E0B), Rgba.FromHex(0x2563EB), Rgba.FromHex(0x1D4ED8), Rgba.FromHex(0x60A5FA, 36), Rgba.FromHex(0x3B82F6), Rgba.FromHex(0xFACC15));

    public static ViewPalette Dark { get; } = new(
        Rgba.FromHex(0x3A4048), Rgba.FromHex(0x1B1E23), Rgba.FromHex(0x9AA5B1), Rgba.FromHex(0x0E1013),
        Rgba.FromHex(0xF59E0B), Rgba.FromHex(0x60A5FA), Rgba.FromHex(0x93C5FD), Rgba.FromHex(0x60A5FA, 40), Rgba.FromHex(0x93C5FD), Rgba.FromHex(0xFACC15));
}

/// <summary>
/// Builds the 3D scene for a part: bodies with pickable faces and edges,
/// visible reference planes and sketches, with hover and selection colours.
/// Keeps the pick-id table that maps renderer ids back to model entities.
/// </summary>
public sealed class PartScene : IPickSource
{
    private readonly List<PickTarget> _targets = [];

    public PickTarget? Target(int id) => id > 0 && id <= _targets.Count ? _targets[id - 1] : null;

    private int Register(PickTarget t)
    {
        _targets.Add(t);
        return _targets.Count;
    }

    /// <summary>Planes shown in the view.</summary>
    public HashSet<string> VisiblePlanes { get; } = [];

    public Scene Build(
        PartDocument doc,
        IReadOnlyList<SelectionItem> selection,
        SelectionItem? hover,
        ViewPalette palette,
        DisplayStyle style,
        string? hiddenSketchId = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(selection);
        _targets.Clear();
        var scene = new Scene { Style = style, BackgroundTop = palette.Top, BackgroundBottom = palette.Bottom };

        bool IsSelected(SelectionItem s) => selection.Any(x => x.SameTarget(s));
        bool IsHover(SelectionItem s) => hover is not null && hover.SameTarget(s);

        foreach (var body in doc.Bodies)
        {
            var baseColor = body.Color is { } c ? Rgba.FromHex(c) : palette.Body;
            var item = new MeshItem { Mesh = body.Mesh, Color = baseColor, EdgeColor = palette.Edge };
            item.FacePickBase = _targets.Count + 1;
            for (var f = 1; f <= body.FaceCount; f++)
            {
                Register(new FacePick(body, f));
            }

            item.EdgePickBase = _targets.Count + 1;
            for (var e = 1; e <= body.EdgeCount; e++)
            {
                Register(new EdgePick(body, e));
            }

            foreach (var s in selection.Concat(hover is null ? [] : [hover]))
            {
                if (s.Ref is null)
                {
                    continue;
                }

                var color = IsSelected(s) ? palette.Selected : palette.Hover;
                if (s.Kind == SelectionKind.Face && body.HasFace(s.Ref.Name))
                {
                    item.FaceColors[body.ResolveFace(s.Ref, out _)] = Rgba.Lerp(baseColor, color, 0.75);
                }
                else if (s.Kind == SelectionKind.Edge && body.HasEdge(s.Ref.Name))
                {
                    item.EdgeColors[body.ResolveEdge(s.Ref, out _)] = color;
                }
            }

            scene.Meshes.Add(item);
        }

        // Reference planes: standard planes when the part is empty or when selected/hovered; user planes always.
        var size = doc.Bodies.Count == 0 ? 60 : Math.Max(20, doc.State.Bodies.Aggregate(Box3.Empty, (b, x) => b.Union(x.Shape.Bounds)).Diagonal * 0.6);
        foreach (var (id, plane) in doc.State.Planes)
        {
            var label = id switch
            {
                "front" => "Front Plane",
                "top" => "Top Plane",
                "right" => "Right Plane",
                _ => doc.Find(id)?.Name ?? id,
            };
            var sel = SelectionItem.Plane(id, label);
            var standard = id is "front" or "top" or "right";
            var show = !standard || doc.Bodies.Count == 0 || IsSelected(sel) || IsHover(sel) || VisiblePlanes.Contains(id);
            if (!show)
            {
                continue;
            }

            var pick = Register(new PlanePick(id, label));
            var a = plane.ToWorld(-size / 2, -size / 2);
            var b = plane.ToWorld(size / 2, -size / 2);
            var c = plane.ToWorld(size / 2, size / 2);
            var d = plane.ToWorld(-size / 2, size / 2);
            var edgeColor = IsSelected(sel) ? palette.Selected : IsHover(sel) ? palette.Hover : palette.PlaneEdge;
            scene.Quads.Add(new QuadItem { A = a, B = b, C = c, D = d, Fill = IsSelected(sel) ? palette.Selected.WithAlpha(50) : palette.Plane });
            scene.Lines.Add(new LineItem { Points = [a, b, c, d], Closed = true, Color = edgeColor, Width = IsSelected(sel) || IsHover(sel) ? 2 : 1.2, PickId = pick, ShowHidden = true });
        }

        // 3D curves (helices) are drawn and can be picked like sketches.
        foreach (var (id, curve) in doc.State.Curves)
        {
            if (doc.Find(id) is not { } curveFeature)
            {
                continue;
            }

            var sel = SelectionItem.ForFeature(curveFeature);
            var pick = Register(new SketchPick(id));
            var color = IsSelected(sel) ? palette.Selected : IsHover(sel) ? palette.Hover : palette.Sketch;
            for (var e = 1; e <= curve.EdgeCount; e++)
            {
                scene.Lines.Add(new LineItem { Points = curve.SampleEdge(e, 0.05), Color = color, Width = 1.4, PickId = pick, OnTop = true });
            }
        }

        // Sketches that no feature has consumed stay visible, like an unused sketch in other CAD tools.
        var consumed = doc.Features.SelectMany(f => f is SketchFeature ? [] : f.Dependencies).ToHashSet();
        foreach (var f in doc.Features.OfType<SketchFeature>())
        {
            if (f.Id == hiddenSketchId || f.IsSuppressed || !doc.State.Sketches.TryGetValue(f.Id, out var placed))
            {
                continue;
            }

            var sel = SelectionItem.ForFeature(f);
            var highlighted = IsSelected(sel) || IsHover(sel);
            if (consumed.Contains(f.Id) && !highlighted)
            {
                continue;
            }

            var pick = Register(new SketchPick(f.Id));
            var color = IsSelected(sel) ? palette.Selected : IsHover(sel) ? palette.Hover : palette.Sketch;
            foreach (var e in placed.Sketch.Entities)
            {
                var pts = SketchPolyline(e).Select(p => placed.Plane.ToWorld(p.X, p.Y)).ToList();
                if (pts.Count >= 2)
                {
                    scene.Lines.Add(new LineItem { Points = pts, Color = color, Width = 1.6, Dashed = e.IsConstruction, PickId = pick, OnTop = true });
                }
                else if (pts.Count == 1)
                {
                    scene.Points.Add(new PointItem { Position = pts[0], Color = color, PickId = pick });
                }
            }
        }

        return scene;
    }

    /// <summary>Points along a sketch entity, in sketch coordinates.</summary>
    public static IReadOnlyList<Sketching.Vec2> SketchPolyline(SketchEntity e)
    {
        switch (e)
        {
            case SketchLine l:
                return [l.Start.Position, l.End.Position];
            case SketchCircle c:
                return [.. Enumerable.Range(0, 73).Select(i => c.Center.Position + Sketching.Vec2.FromAngle(i * Math.PI / 36, c.Radius))];
            case SketchEllipse el:
                return [.. el.Sample(96), el.PointAt(0)];
            case SketchArc a:
                var n = Math.Max(4, (int)Math.Ceiling(a.Sweep / (Math.PI / 36)));
                return [.. Enumerable.Range(0, n + 1).Select(i => a.Center.Position + Sketching.Vec2.FromAngle(a.StartAngle + (a.Sweep * i / n), a.Radius))];
            case SketchPoint p:
                return [p.Position];
            default:
                return [];
        }
    }
}
