using Chamfer.Document.Features;
using Chamfer.App.Part;
using Chamfer.Assembly;
using Chamfer.Sketching;

namespace Chamfer.App.ViewModels;

/// <summary>Assembly features: cuts through components made in the assembly.</summary>
public sealed partial class MainViewModel
{
    private void StartAssemblyCut()
    {
        if (AssemblyDoc is not { } asm)
        {
            return;
        }

        CancelEditor();
        var before = asm.Save();
        var picks = Selection.Select(s => s.ComponentId).OfType<string>().Distinct().ToList();
        var cut = new AssemblyCut { ThroughAll = true, Components = picks };
        var planes = new[] { "front", "top", "right" };
        var rectangle = false;
        double cx = 0, cy = 0, size = 10, height = 10;
        FeatureEditor? panel = null;

        void Preview()
        {
            asm.RestoreFrom(before);
            var s = new Sketch();
            if (rectangle)
            {
                s.AddCenterRectangle(new Vec2(cx, cy), new Vec2(cx + (size / 2), cy + (height / 2)));
            }
            else
            {
                s.AddCircle(new Vec2(cx, cy), size / 2);
            }

            cut.Sketch = s;
            var added = asm.AddCut(cut);
            added.Id = "pending";
            RebuildAndRefresh();
        }

        panel = FeatureEditor.Command("Assembly Cut", Preview,
            accept: () =>
            {
                if (asm.Features.Find(f => f.Id == "pending") is { } f)
                {
                    _undo.Push("asm:" + before);
                    _redo.Clear();
                    _dirty = true;
                    f.Id = $"cut{Guid.NewGuid():N}"[..11];
                    UndoCommand.NotifyCanExecuteChanged();
                }
            },
            cancel: () => asm.RestoreFrom(before));
        panel.AddField(new ChoiceField("Sketch plane", ["Front", "Top", "Right"], 0, i =>
        {
            cut.PlaneId = planes[i];
            Preview();
        }));
        panel.AddField(new NumberField("Plane offset", "mm", () => cut.PlaneOffset, v =>
        {
            cut.PlaneOffset = v;
            Preview();
        }));
        panel.AddField(new ChoiceField("Profile", ["Circle", "Rectangle"], 0, i =>
        {
            rectangle = i == 1;
            Preview();
        }));
        panel.AddField(new NumberField("Centre X", "mm", () => cx, v =>
        {
            cx = v;
            Preview();
        }));
        panel.AddField(new NumberField("Centre Y", "mm", () => cy, v =>
        {
            cy = v;
            Preview();
        }));
        panel.AddField(new NumberField("Diameter / width", "mm", () => size, v =>
        {
            size = v;
            Preview();
        }, 1e-3));
        panel.AddField(new NumberField("Height (rectangle)", "mm", () => height, v =>
        {
            height = v;
            Preview();
        }, 1e-3));
        panel.AddField(new ChoiceField("Hole style (circles)", ["Plain", "Counterbore", "Countersink"], 0, i =>
        {
            cut.HoleStyle = i switch { 1 => HoleType.Counterbore, 2 => HoleType.Countersink, _ => HoleType.Simple };
            Preview();
        }));
        panel.AddField(new NumberField("Counterbore / countersink diameter", "mm", () => cut.CounterDiameter, v =>
        {
            cut.CounterDiameter = v;
            Preview();
        }, 1e-3));
        panel.AddField(new NumberField("Counterbore depth", "mm", () => cut.CounterboreDepth, v =>
        {
            cut.CounterboreDepth = v;
            Preview();
        }, 1e-3));
        panel.AddField(new BoolField("Through all", true, v =>
        {
            cut.ThroughAll = v;
            Preview();
        }));
        panel.AddField(new NumberField("Depth", "mm", () => cut.Depth, v =>
        {
            cut.Depth = v;
            Preview();
        }, 1e-3));
        panel.AddField(new BoolField("Reverse direction", false, v =>
        {
            cut.Reverse = v;
            Preview();
        }));
        panel.AddField(new SelectionField("Components (empty = all)", [SelectionKind.Face, SelectionKind.Edge, SelectionKind.Feature],
            Selection.Where(s => s.ComponentId is not null).GroupBy(s => s.ComponentId).Select(g => g.First()), items =>
            {
                cut.Components = [.. items.Select(i => i.ComponentId).OfType<string>().Distinct()];
                Preview();
            }));
        panel.AddField(new InfoField("Note", "The cut exists only in this assembly; the part files don't change."));
        panel.ActiveSelectionChanged += (_, _) => LoadActiveField();
        Editor = panel;
        Preview();
    }
}
