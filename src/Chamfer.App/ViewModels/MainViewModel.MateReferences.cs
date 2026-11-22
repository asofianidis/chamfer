using Chamfer.App.Part;
using Chamfer.Document;
using Chamfer.Kernel.Occt;

namespace Chamfer.App.ViewModels;

/// <summary>Mate references: faces a part offers for automatic mating when it's inserted.</summary>
public sealed partial class MainViewModel
{
    private void StartMateReference()
    {
        if (AssemblyDoc is not null || DrawingDoc is not null)
        {
            return;
        }

        CancelEditor();
        var name = "Default";
        var picks = Selection.Where(s => s.Kind == SelectionKind.Face).Take(2).ToList();
        FeatureEditor? panel = null;
        panel = FeatureEditor.Command("Mate Reference", () => { },
            accept: () =>
            {
                var faces = panel!.Fields.OfType<SelectionField>().SelectMany(f => f.Items.Take(1)).Where(i => i.Ref is not null).ToList();
                if (faces.Count == 0)
                {
                    MessageText = "Pick at least one face for the mate reference.";
                    return;
                }

                Snapshot();
                Document.MateReferences.RemoveAll(r => r.Name == name);
                Document.MateReferences.Add(new MateReference
                {
                    Name = name,
                    Entities = [.. faces.Select(f => new MateReferenceEntity(f.Ref!, KindOf(f.Ref!)))],
                });
                MessageText = $"Mate reference '{name}' with {faces.Count} face(s).";
            },
            cancel: () => { });
        panel.AddField(new TextField("Name", name, v => name = v.Trim()));
        var primary = panel.AddField(new SelectionField("Primary face", [SelectionKind.Face], picks.Take(1), _ => { }, max: 1));
        panel.AddField(new SelectionField("Secondary face", [SelectionKind.Face], picks.Skip(1).Take(1), _ => { }, max: 1));
        panel.AddField(new InfoField("How", "Cylinders mate concentric, flat faces coincident (touching). Parts with references of the same name mate when inserted."));
        panel.ActiveSelectionChanged += (_, _) => LoadActiveField();
        panel.Activate(primary);
        Editor = panel;
    }

    private string KindOf(TopoRef face)
    {
        var body = Document.Bodies.FirstOrDefault(b => b.HasFace(face.Name));
        return body is not null && body.Face(body.ResolveFace(face, out _)).Surface == SurfaceKind.Cylinder ? "concentric" : "coincident";
    }
}
