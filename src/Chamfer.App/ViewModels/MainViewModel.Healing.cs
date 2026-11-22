using Chamfer.Document.Features;
using Chamfer.Kernel.Occt;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Import diagnostics and healing.</summary>
public sealed partial class MainViewModel
{
    private void ShowDiagnostics()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Import Diagnostics");
        var anyProblem = false;
        foreach (var body in Document.Bodies)
        {
            ShapeDiagnostics d;
            try
            {
                d = body.Shape.Diagnose(0.01);
            }
            catch (KernelException ex)
            {
                panel.AddInfo(new InfoField(body.Name, $"Couldn't check: {ex.Message}"));
                continue;
            }

            anyProblem |= !d.IsClean;
            panel.AddInfo(new InfoField(body.Name, d.IsClean
                ? $"OK: {d.Solids} solid(s), {d.Faces} faces."
                : $"{d.Faces} faces, {d.Solids} solid(s), {d.Shells} shell(s); {d.FreeEdges} open edge(s), {d.InvalidFaces} bad face(s), {d.InvalidEdges} bad edge(s), {d.SmallEdges} tiny edge(s)."));
        }

        if (Document.Bodies.Count == 0)
        {
            panel.AddInfo(new InfoField("Bodies", "There is nothing to check."));
        }

        if (anyProblem)
        {
            panel.AddInfo(new ButtonField("Heal", new RelayCommand(() => StartFeature(new HealFeature()))));
        }

        Editor = panel;
    }
}
