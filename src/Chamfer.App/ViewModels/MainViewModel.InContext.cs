using Chamfer.App.Part;
using Chamfer.Assembly;
using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>In-context (top-down) design: building parts from other components' faces in the assembly.</summary>
public sealed partial class MainViewModel
{
    private void StartSketchInContext()
    {
        if (AssemblyDoc is not { } asm ||
            Selection.FirstOrDefault(s => s is { Kind: SelectionKind.Face, ComponentId: not null, Ref: not null }) is not { } pick ||
            asm.Find(pick.ComponentId!) is not { } source)
        {
            MessageText = "Select a planar face on the component to build from.";
            return;
        }

        CancelEditor();
        var targets = asm.Components.Where(c => !c.Suppressed && c.Path != source.Path && !Toolbox.IsToolboxPath(c.Path)).ToList();
        var targetIndex = 0; // 0 is a new part
        var depth = 2.0;
        var panel = FeatureEditor.Command("Sketch in Context", () => { },
            accept: () => CreateInContext(asm, source, pick.Ref!, targetIndex == 0 ? null : targets[targetIndex - 1], depth),
            cancel: () => { });
        panel.AddInfo(new InfoField("From", $"{source.Name}, {pick.Label}"));
        panel.AddField(new ChoiceField("Part to build", ["New part", .. targets.Select(t => t.Name)], 0, i => targetIndex = i));
        panel.AddField(new NumberField("Then extrude by (0: sketch only)", "mm", () => depth, v => depth = v, min: 0));
        panel.AddInfo(new InfoField("Updates", "The sketch follows the face as the source changes or moves; lock or break it under External References."));
        Editor = panel;
    }

    private void CreateInContext(AssemblyDocument asm, Component source, TopoRef face, Component? target, double depth)
    {
        SnapshotAssembly();
        if (target is null)
        {
            // A new part placed at the assembly origin, so its coordinates are the assembly's.
            var n = 1;
            while (asm.Components.Any(c => Path.GetFileNameWithoutExtension(c.Path) == $"Part{n}") || File.Exists(asm.Resolve($"Part{n}.{PartDocument.FileExtension}")))
            {
                n++;
            }

            var path = $"Part{n}.{PartDocument.FileExtension}";
            asm.RegisterPart(path, new PartDocument());
            target = asm.AddComponent(path);
        }

        try
        {
            var (_, sketch) = InContext.SketchOnFace(asm, target, source, face);
            var part = asm.PartOf(target);
            if (depth > 0)
            {
                part.Add(new ExtrudeFeature { SketchId = sketch.Id, Depth = depth });
                part.Rebuild();
            }

            SaveComponentPart(asm, target);
            MessageText = $"{target.Name} now has an in-context sketch of {source.Name}'s face{(depth > 0 ? " and an extrusion" : string.Empty)}. Open the part to add more.";
        }
        catch (InvalidOperationException ex)
        {
            MessageText = ex.Message;
        }

        RebuildAndRefresh();
    }

    /// <summary>Brings in-context geometry up to date and saves the parts that changed.</summary>
    private void UpdateInContext(AssemblyDocument asm)
    {
        var changed = InContext.Update(asm);
        foreach (var c in changed)
        {
            SaveComponentPart(asm, c);
        }

        if (changed.Count > 0)
        {
            MessageText = $"Updated in-context geometry in {string.Join(", ", changed.Select(c => c.Name))}.";
        }
    }

    /// <summary>Writes a component's part to its file, when the assembly lives on disk.</summary>
    private void SaveComponentPart(AssemblyDocument asm, Component c)
    {
        if (asm.Directory is null && !Path.IsPathRooted(c.Path))
        {
            return; // an unsaved assembly: the part stays in memory until the assembly is saved
        }

        try
        {
            File.WriteAllText(asm.Resolve(c.Path), asm.PartOf(c).Save());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText = $"Couldn't save {c.Name}: {ex.Message}";
        }
    }

    private void ShowExternalReferences()
    {
        if (AssemblyDoc is not { } asm)
        {
            return;
        }

        CancelEditor();
        var panel = FeatureEditor.Info("External References");
        var refs = InContext.References(asm).ToList();
        if (refs.Count == 0)
        {
            panel.AddInfo(new InfoField("References", "No part in this assembly references another component."));
        }

        foreach (var (c, f) in refs)
        {
            var r = f.Reference!;
            var source = asm.Find(r.SourceComponentId);
            var state = r.Locked ? "locked" : source is null ? "out of context (source removed)" : "in context";
            panel.AddInfo(new InfoField($"{c.Name}: {f.Name}", $"{r.SourceComponentName}, {r.SourceFace.Name} ({state})"));
            panel.AddInfo(new BoolField($"Lock {f.Name}", r.Locked, v =>
            {
                InContext.SetLocked(f, v);
                SaveComponentPart(asm, c);
                RebuildAndRefresh();
            }));
            panel.AddInfo(new ButtonField($"Break {f.Name}", new RelayCommand(() =>
            {
                InContext.Break(f);
                SaveComponentPart(asm, c);
                ShowExternalReferences();
            })));
        }

        Editor = panel;
    }
}
