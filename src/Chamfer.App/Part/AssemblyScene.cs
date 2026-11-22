using Chamfer.Assembly;
using Chamfer.Geometry;
using Chamfer.Viewport;

namespace Chamfer.App.Part;

/// <summary>
/// Builds the 3D scene for an assembly: every visible component's bodies,
/// moved to its placement (plus its explode offset when exploded), with
/// pickable faces and edges tagged by component.
/// </summary>
public sealed class AssemblyScene : IPickSource
{
    private readonly List<PickTarget> _targets = [];

    private static readonly Rgba[] Tints =
    [
        Rgba.FromHex(0xB9C3CE), Rgba.FromHex(0xC9B89A), Rgba.FromHex(0xA7C4B5), Rgba.FromHex(0xB7B0CC),
        Rgba.FromHex(0xCDB0B0), Rgba.FromHex(0xA9BED3), Rgba.FromHex(0xC7C7A4),
    ];

    public PickTarget? Target(int id) => id > 0 && id <= _targets.Count ? _targets[id - 1] : null;

    public Scene Build(AssemblyDocument asm, IReadOnlyList<SelectionItem> selection, SelectionItem? hover, ViewPalette palette, DisplayStyle style, bool exploded)
    {
        ArgumentNullException.ThrowIfNull(asm);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(palette);
        _targets.Clear();

        var scene = new Scene { Style = style, BackgroundTop = palette.Top, BackgroundBottom = palette.Bottom };
        var paths = asm.Components.Select(c => c.Path).Distinct().ToList();
        foreach (var c in asm.Components.Where(c => !c.Suppressed && !c.Hidden))
        {
            var placement = exploded ? Transform3.Translation(c.ExplodeOffset) * c.Transform : c.Transform;
            var tint = Tints[paths.IndexOf(c.Path) % Tints.Length];
            var componentSelected = selection.Any(s => s.Kind == SelectionKind.Feature && s.FeatureId == c.Id);
            foreach (var body in asm.BodiesOf(c))
            {
                // Instanced: every copy of a part shares its body's mesh, placed by a transform.
                var item = new MeshItem
                {
                    Mesh = body.Mesh,
                    Transform = placement,
                    Color = componentSelected ? Rgba.Lerp(tint, palette.Selected, 0.45) : tint,
                    EdgeColor = palette.Edge,
                    FacePickBase = _targets.Count + 1,
                };
                for (var f = 1; f <= body.FaceCount; f++)
                {
                    _targets.Add(new ComponentPick(c, body, f, IsEdge: false));
                }

                item.EdgePickBase = _targets.Count + 1;
                for (var e = 1; e <= body.EdgeCount; e++)
                {
                    _targets.Add(new ComponentPick(c, body, e, IsEdge: true));
                }

                foreach (var s in selection.Concat(hover is null ? [] : [hover]))
                {
                    if (s.ComponentId != c.Id || s.Ref is null)
                    {
                        continue;
                    }

                    var color = selection.Any(x => x.SameTarget(s)) ? palette.Selected : palette.Hover;
                    if (s.Kind == SelectionKind.Face && body.HasFace(s.Ref.Name))
                    {
                        item.FaceColors[body.ResolveFace(s.Ref, out _)] = Rgba.Lerp(tint, color, 0.75);
                    }
                    else if (s.Kind == SelectionKind.Edge && body.HasEdge(s.Ref.Name))
                    {
                        item.EdgeColors[body.ResolveEdge(s.Ref, out _)] = color;
                    }
                }

                scene.Meshes.Add(item);
            }
        }

        return scene;
    }
}
