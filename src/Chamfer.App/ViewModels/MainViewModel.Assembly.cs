using System.Globalization;
using Avalonia.Platform.Storage;
using Chamfer.App.Editing;
using Chamfer.App.Part;
using Chamfer.Assembly;
using Chamfer.Document;
using Chamfer.Geometry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Assembly mode: components, mates, interference, BOM, explode, and editing parts in place.</summary>
public sealed partial class MainViewModel
{
    private static readonly FilePickerFileType AssemblyFileType = new("Chamfer Assembly") { Patterns = [$"*.{AssemblyDocument.FileExtension}"] };

    private (AssemblyDocument Doc, string? Path)? _returnTo;
    private MateSolveResult? _lastSolve;

    /// <summary>The open assembly, or null when editing a part.</summary>
    public AssemblyDocument? AssemblyDoc { get; private set; }

    public AssemblyScene AssemblyBuilder { get; } = new();

    public bool IsAssemblyMode => AssemblyDoc is not null && Sketch is null && DrawingDoc is null;
    public bool IsPartMode => AssemblyDoc is null && Sketch is null && DrawingDoc is null;
    public string TreeTitle => DrawingDoc is not null ? "DRAWING" : AssemblyDoc is null ? "FEATURES" : "ASSEMBLY";

    public bool CanReturnToAssembly => _returnTo is not null && AssemblyDoc is null && Sketch is null && DrawingDoc is null;

    [ObservableProperty]
    public partial bool Exploded { get; set; }

    partial void OnExplodedChanged(bool value)
    {
        if (value && AssemblyDoc is { } asm && asm.Components.All(c => c.ExplodeOffset == Vec3.Zero))
        {
            AssemblyTools.AutoExplode(asm);
        }

        RaiseScene();
        FitAction?.Invoke();
    }

    /// <summary>Picks in the current scene (part or assembly).</summary>
    public IPickSource Picks => AssemblyDoc is null ? SceneBuilder : AssemblyBuilder;

    public IReadOnlyList<CommandItem> AssemblyCommands => _assemblyCommands ??=
    [
        new("Insert Component", "Insert a saved part", new AsyncRelayCommand(InsertComponent), "⊕"),
        new("Toolbox", "Insert standard screws, bolts, nuts and washers", new RelayCommand(StartToolbox), "⚙"),
        new("Mate", "Mate two faces or edges", new RelayCommand(StartMate), "⫘"),
        new("Linear Pattern", "Copy the selected component along an axis", new RelayCommand(() => StartComponentPattern(circular: false)), "⋯"),
        new("Circular Pattern", "Copy the selected component around an axis", new RelayCommand(() => StartComponentPattern(circular: true)), "⟳"),
        new("Move", "Move or rotate the selected component", new RelayCommand(StartMove), "✥"),
        new("Fix / Float", "Fix the selected component in place, or let it move", new RelayCommand(ToggleFixed), "⚓"),
        new("Assembly Cut", "Cut a hole or pocket through components in the assembly only", new RelayCommand(StartAssemblyCut), "⊘"),
        new("Interference", "Find overlapping components", new RelayCommand(ShowInterference), "⧉"),
        new("Motion Study", "Drive a component with a motor and animate the mechanism", new RelayCommand(StartMotionStudy), "▶"),
        new("Bill of Materials", "List parts and quantities", new RelayCommand(ShowBom), "☰"),
        new("Pack and Go", "Copy the assembly and all its parts to one folder", new RelayCommand(StartPackAndGo), "⇪"),
        new("Design Library", "Insert reusable parts from the library", new RelayCommand(ShowDesignLibrary), "▦"),
        new("Import STEP", "Open a STEP assembly as parts and an assembly", new AsyncRelayCommand(ImportStepAssemblyAsync), "⇩"),
        new("Explode", "Spread the components apart (toggle)", new RelayCommand(() => Exploded = !Exploded), "✳"),
        new("Open Part", "Edit the selected component's part", new RelayCommand(OpenSelectedPart), "↗"),
        new("Sketch in Context", "Build a part from the selected face of another component", new RelayCommand(StartSketchInContext), "⇢"),
        new("External References", "In-context references between parts: lock or break them", new RelayCommand(ShowExternalReferences), "⛓"),
    ];

    private IReadOnlyList<CommandItem>? _assemblyCommands;

    private void RaiseModeChanged()
    {
        OnPropertyChanged(nameof(IsAssemblyMode));
        OnPropertyChanged(nameof(IsPartMode));
        OnPropertyChanged(nameof(CanReturnToAssembly));
        OnPropertyChanged(nameof(IsModelMode));
        OnPropertyChanged(nameof(TreeTitle));
        OnPropertyChanged(nameof(IsDrawingMode));
    }

    [RelayCommand]
    private void NewAssembly()
    {
        New();
        AssemblyDoc = new AssemblyDocument();
        _returnTo = null;
        RaiseModeChanged();
        RebuildAndRefresh();
    }

    /// <summary>Inserts a part file (or a part already in memory, registered under its path) into the assembly.</summary>
    public Component InsertPart(string path, PartDocument? part = null)
    {
        var asm = AssemblyDoc ?? throw new InvalidOperationException("No assembly is open.");
        SnapshotAssembly();
        if (part is not null)
        {
            asm.RegisterPart(path, part);
        }

        // Place new components beside what's already there.
        var box = asm.Components.Where(c => !c.Suppressed).SelectMany(c => asm.PlacedBodies(c)).Aggregate(Box3.Empty, (b, p) => b.Union(p.Placed.Bounds));
        var at = box.IsEmpty ? Vec3.Zero : new Vec3(box.Max.X + (box.Size.X * 0.25) + 10, box.Min.Y, box.Min.Z);
        var c = asm.AddComponent(path, Transform3.Translation(at));
        var auto = asm.ApplyMateReferences(c);
        RebuildAndRefresh();
        if (auto.Count > 0)
        {
            MessageText = $"Mated {c.Name} by its mate references ({string.Join(", ", auto.Select(m => m.Type))}).";
        }

        FitAction?.Invoke();
        return c;
    }

    private async Task InsertComponent()
    {
        if (StorageProvider is null || AssemblyDoc is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Insert Component", FileTypeFilter = [PartFileType, AssemblyFileType] });
        if (files is not [var file] || file.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            InsertPart(path);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or NotSupportedException)
        {
            MessageText = $"Couldn't insert {file.Name}: {ex.Message}";
        }
    }

    private void SnapshotAssembly()
    {
        if (AssemblyDoc is null)
        {
            return;
        }

        _undo.Push("asm:" + AssemblyDoc.Save());
        _redo.Clear();
        _dirty = true;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private Component? SelectedComponent() =>
        (SelectedNode?.Tag as Component) ??
        Selection.Select(s => s.ComponentId).OfType<string>().Select(id => AssemblyDoc?.Find(id)).LastOrDefault(c => c is not null);

    private static SelectionItem ComponentItem(Component c) => new(SelectionKind.Feature, FeatureId: c.Id, Label: c.Name, ComponentId: c.Id);

    private static MateRef? ToMateRef(SelectionItem s) => s switch
    {
        { ComponentId: { } id, Kind: SelectionKind.Face, Ref: { } r } => new MateRef(id, Face: r),
        { ComponentId: { } id, Kind: SelectionKind.Edge, Ref: { } r } => new MateRef(id, Edge: r),
        _ => null,
    };

    private void StartToolbox()
    {
        if (AssemblyDoc is null)
        {
            return;
        }

        CancelEditor();
        var kinds = Enum.GetValues<FastenerKind>();
        var kindNames = new[] { "Socket head cap screw (ISO 4762)", "Hex bolt (ISO 4017)", "Hex nut (ISO 4032)", "Flat washer (ISO 7089)" };
        var spec = new FastenerSpec(FastenerKind.SocketHeadCapScrew, "M6", 20);
        var panel = FeatureEditor.Command("Toolbox", () => { },
            accept: () => InsertPart(spec.Path),
            cancel: () => { });
        var preview = new InfoField("Part", spec.Description);
        void Update(FastenerSpec next)
        {
            spec = next;
            preview.Text = spec.Description;
        }

        panel.AddField(new ChoiceField("Type", kindNames, 0, i => Update(spec with { Kind = kinds[i] })));
        panel.AddField(new ChoiceField("Size", [.. Toolbox.SizeNames], Toolbox.SizeNames.ToList().IndexOf("M6"), i => Update(spec with { Size = Toolbox.SizeNames[i] })));
        var lengths = Toolbox.StandardLengths.Select(l => l.ToString("0", CultureInfo.InvariantCulture)).ToList();
        panel.AddField(new ChoiceField("Length (screws, bolts)", lengths, lengths.IndexOf("20"), i => Update(spec with { Length = Toolbox.StandardLengths[i] })));
        panel.AddField(preview);
        Editor = panel;
    }

    // ------------------------------------------------------------- mates

    private void StartMate()
    {
        if (AssemblyDoc is not { } asm)
        {
            return;
        }

        CancelEditor();
        var before = asm.Save();
        var picks = Selection.Where(s => ToMateRef(s) is not null).ToList();
        var mate = new Mate { Id = "pending", Type = MateType.Coincident };
        var types = Enum.GetValues<MateType>().Where(t => t != MateType.Drive).ToArray();
        FeatureEditor? panel = null;

        void Preview()
        {
            asm.RestoreFrom(before);
            asm.Mates.RemoveAll(m => m.Id == "pending");
            if (panel?.Fields.OfType<SelectionField>().ToList() is not [var fa, var fb, var fc, var fd] || fa.Items.Count == 0 || fb.Items.Count == 0)
            {
                RebuildAndRefresh();
                return;
            }

            var a = ToMateRef(fa.Items[0]);
            var b = ToMateRef(fb.Items[0]);
            if (a is null || b is null || (a.ComponentId == b.ComponentId && mate.Type != MateType.Width))
            {
                panel.Message = "Pick faces or edges on two different components.";
                RebuildAndRefresh();
                return;
            }

            var c = fc.Items.Count > 0 ? ToMateRef(fc.Items[0]) : null;
            var d = fd.Items.Count > 0 ? ToMateRef(fd.Items[0]) : null;
            var missing = mate.Type switch
            {
                MateType.Symmetric when c is null => "Pick the symmetry plane as the third entity.",
                MateType.Width when c is null || d is null => "Pick the two slot faces as the third and fourth entities.",
                MateType.Slot when c is null => "Pick the pin and one slot wall, then the other slot wall as the third entity.",
                MateType.Hinge when (c is null) != (d is null) => "For a hinge, pick both touching faces (third and fourth), or neither.",
                _ => null,
            };
            if (missing is not null)
            {
                panel.Message = missing;
                RebuildAndRefresh();
                return;
            }

            var added = asm.AddMate(mate.Type, a, b, mate.Value,
                mate.Type is MateType.Gear or MateType.RackPinion or MateType.Screw or MateType.UniversalJoint or MateType.LinearCoupler ? !mate.Aligned : mate.Aligned);
            added.C = c;
            added.D = d;
            added.Value2 = Math.Max(mate.Value, mate.Value2);
            added.Id = "pending";
            RebuildAndRefresh();
            panel.Message = _lastSolve is { Converged: false } ? "These mates conflict or can't be satisfied." : null;
        }

        panel = FeatureEditor.Command("Mate", Preview,
            accept: () =>
            {
                if (asm.Mates.Find(m => m.Id == "pending") is { } m)
                {
                    _undo.Push("asm:" + before);
                    _redo.Clear();
                    _dirty = true;
                    m.Id = $"m{Guid.NewGuid():N}"[..9];
                    m.Name = $"{m.Type}{asm.Mates.Count(x => x.Type == m.Type)}";
                    UndoCommand.NotifyCanExecuteChanged();
                }
            },
            cancel: () => asm.RestoreFrom(before));
        panel.AddField(new ChoiceField("Mate type", [.. types.Select(t => t.ToString())], 0, i =>
        {
            mate.Type = types[i];
            Preview();
        }));
        panel.AddField(new NumberField("Distance / angle / ratio", string.Empty, () => mate.Value, v =>
        {
            mate.Value = v;
            Preview();
        }));
        panel.AddField(new NumberField("Maximum (limit mates)", string.Empty, () => mate.Value2, v =>
        {
            mate.Value2 = v;
            Preview();
        }));
        panel.AddField(new BoolField("Flip alignment", false, v =>
        {
            mate.Aligned = !mate.Aligned;
            Preview();
        }));
        var kinds = new[] { SelectionKind.Face, SelectionKind.Edge };
        var first = panel.AddField(new SelectionField("First entity", kinds, picks.Take(1), _ => Preview(), max: 1));
        var second = panel.AddField(new SelectionField("Second entity", kinds, picks.Skip(1).Take(1), _ => Preview(), max: 1));
        panel.AddField(new SelectionField("Third: symmetry plane, slot wall or hinge face", kinds, picks.Skip(2).Take(1), _ => Preview(), max: 1));
        panel.AddField(new SelectionField("Fourth: second slot face or hinge face", kinds, picks.Skip(3).Take(1), _ => Preview(), max: 1));

        // Pre-pick the alignment and the closest sensible type for the current selection.
        if (picks.Count >= 2 && ToMateRef(picks[0]) is { } ra && ToMateRef(picks[1]) is { } rb)
        {
            try
            {
                var ea = MateSolver.Resolve(asm, ra);
                var eb = MateSolver.Resolve(asm, rb);
                mate.Aligned = Vec3.Dot(ea.Direction, eb.Direction) > 0;
                mate.Type = MateSolver.Applicable(ea.Kind, eb.Kind)[0];
                ((ChoiceField)panel.Fields[0]).Index = Array.IndexOf(types, mate.Type);
            }
            catch (InvalidOperationException)
            {
            }
        }

        panel.ActiveSelectionChanged += (_, _) => LoadActiveField();
        panel.Activate(picks.Count >= 1 ? second : first);
        Editor = panel;
        Preview();
    }

    // ------------------------------------------------------------- move

    private void StartMove()
    {
        if (AssemblyDoc is not { } asm || SelectedComponent() is not { } c)
        {
            MessageText = "Select a component (in the tree or the view) to move.";
            return;
        }

        CancelEditor();
        var before = asm.Save();
        var original = c.Transform;
        var lastClear = original;
        var stopAtCollisions = false;
        FeatureEditor? panel = null;
        double dx = 0, dy = 0, dz = 0, rx = 0, ry = 0, rz = 0;
        void Apply()
        {
            var center = AssemblyTools.Center(asm, c);
            var r = Transform3.Rotation(center, Vec3.UnitX, rx * Math.PI / 180) *
                    Transform3.Rotation(center, Vec3.UnitY, ry * Math.PI / 180) *
                    Transform3.Rotation(center, Vec3.UnitZ, rz * Math.PI / 180);
            c.Transform = Transform3.Translation(new Vec3(dx, dy, dz)) * r * original;
            if (panel is not null)
            {
                panel.Message = null;
            }

            if (stopAtCollisions && AssemblyTools.CollisionsWith(asm, c) is { Count: > 0 } hits)
            {
                // Refuse the move: back to the last position that was clear.
                c.Transform = lastClear;
                if (panel is not null)
                {
                    panel.Message = $"Stopped: {c.Name} would collide with {string.Join(", ", hits.Select(h => h.B.Name))}.";
                }
            }
            else
            {
                lastClear = c.Transform;
            }

            RebuildAndRefresh();
        }

        panel = FeatureEditor.Command($"Move {c.Name}", Apply,
            accept: () =>
            {
                _undo.Push("asm:" + before);
                _redo.Clear();
                _dirty = true;
                UndoCommand.NotifyCanExecuteChanged();
            },
            cancel: () => asm.RestoreFrom(before));
        panel.AddField(new NumberField("Move X", "mm", () => dx, v => { dx = v; Apply(); }));
        panel.AddField(new NumberField("Move Y", "mm", () => dy, v => { dy = v; Apply(); }));
        panel.AddField(new NumberField("Move Z", "mm", () => dz, v => { dz = v; Apply(); }));
        panel.AddField(new NumberField("Rotate X", "°", () => rx, v => { rx = v; Apply(); }));
        panel.AddField(new NumberField("Rotate Y", "°", () => ry, v => { ry = v; Apply(); }));
        panel.AddField(new NumberField("Rotate Z", "°", () => rz, v => { rz = v; Apply(); }));
        panel.AddField(new BoolField("Stop at collisions", false, v => { stopAtCollisions = v; Apply(); }));
        if (c.Fixed)
        {
            panel.Message = "This component is fixed; float it first to move it.";
        }

        Editor = panel;
    }

    private void StartComponentPattern(bool circular)
    {
        if (AssemblyDoc is not { } asm || SelectedComponent() is not { } seed)
        {
            MessageText = "Select a component to pattern.";
            return;
        }

        CancelEditor();
        var before = asm.Save();
        var axes = new[] { Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ };
        var axis = circular ? 2 : 0;
        var spacing = 50.0;
        var count = circular ? 6 : 3;

        void Preview()
        {
            asm.RestoreFrom(before);
            var s = asm.Find(seed.Id)!;
            if (circular)
            {
                asm.CircularPattern(s, Vec3.Zero, axes[axis], Math.Max(2, count));
            }
            else
            {
                asm.LinearPattern(s, axes[axis], spacing, Math.Max(2, count));
            }

            RebuildAndRefresh();
        }

        var panel = FeatureEditor.Command(circular ? "Circular Component Pattern" : "Linear Component Pattern", Preview,
            accept: () =>
            {
                _undo.Push("asm:" + before);
                _redo.Clear();
                _dirty = true;
                UndoCommand.NotifyCanExecuteChanged();
            },
            cancel: () =>
            {
                asm.RestoreFrom(before);
                RebuildAndRefresh();
            });
        panel.AddField(new ChoiceField(circular ? "Axis (through origin)" : "Direction", ["X", "Y", "Z"], axis, i =>
        {
            axis = i;
            Preview();
        }));
        if (!circular)
        {
            panel.AddField(new NumberField("Spacing", "mm", () => spacing, v =>
            {
                spacing = v;
                Preview();
            }));
        }

        panel.AddField(new NumberField("Instances", string.Empty, () => count, v =>
        {
            count = (int)Math.Round(v);
            Preview();
        }, 2, wholeNumber: true));
        panel.AddField(new InfoField("Copies", "Copies are locked to the seed and move with it."));
        Editor = panel;
        Preview();
    }

    private void ToggleFixed()
    {
        if (AssemblyDoc is null || SelectedComponent() is not { } c)
        {
            MessageText = "Select a component to fix or float.";
            return;
        }

        SnapshotAssembly();
        c.Fixed = !c.Fixed;
        RebuildAndRefresh();
    }

    // ------------------------------------------------------------- evaluate

    private void ShowInterference()
    {
        if (AssemblyDoc is not { } asm)
        {
            return;
        }

        CancelEditor();
        var panel = FeatureEditor.Info("Interference Detection");
        var found = AssemblyTools.FindInterferences(asm);
        if (found.Count == 0)
        {
            panel.AddInfo(new InfoField("Result", "No interferences."));
        }

        foreach (var i in found)
        {
            panel.AddInfo(new InfoField($"{i.A.Name} ↔ {i.B.Name}", $"{i.Volume.ToString("0.###", CultureInfo.InvariantCulture)} mm³ overlap"));
        }

        Editor = panel;
    }

    private void ShowBom()
    {
        if (AssemblyDoc is not { } asm)
        {
            return;
        }

        CancelEditor();
        var panel = FeatureEditor.Info("Bill of Materials");
        double total = 0;
        foreach (var line in AssemblyTools.BillOfMaterials(asm))
        {
            total += line.MassEach * line.Quantity;
            var config = line.Configuration is null ? string.Empty : $" [{line.Configuration}]";
            panel.AddInfo(new InfoField($"{line.Item}. {line.PartName}{config}",
                (line.Description.Length > 0 ? line.Description + "\n" : string.Empty) +
                $"Qty {line.Quantity} · {line.Material} · {(line.MassEach * 1000).ToString("0.##", CultureInfo.InvariantCulture)} g each"));
        }

        panel.AddInfo(new InfoField("Total mass", $"{(total * 1000).ToString("0.##", CultureInfo.InvariantCulture)} g"));
        Editor = panel;
    }

    // ------------------------------------------------------------- parts in context

    private void OpenSelectedPart()
    {
        if (AssemblyDoc is not { } asm || SelectedComponent() is not { } c)
        {
            MessageText = "Select a component whose part you want to edit.";
            return;
        }

        CancelEditor();
        if (asm.SubAssemblyOf(c) is { } sub)
        {
            // Edit the subassembly in place; returning rebuilds it in the parent.
            _returnTo = (asm, FilePath);
            _returnToSub = c.Path;
            AssemblyDoc = sub;
            FilePath = Path.IsPathRooted(c.Path) ? c.Path : asm.Resolve(c.Path);
            _undo.Clear();
            _redo.Clear();
            SetSelection([]);
            RaiseModeChanged();
            RebuildAndRefresh();
            FitAction?.Invoke();
            return;
        }

        var part = asm.PartOf(c);
        _returnTo = (asm, FilePath);
        AssemblyDoc = null;
        Document = part;
        FilePath = Path.IsPathRooted(c.Path) ? c.Path : asm.Resolve(c.Path);
        _undo.Clear();
        _redo.Clear();
        SetSelection([]);
        RaiseModeChanged();
        RebuildAndRefresh();
        FitAction?.Invoke();
    }

    [RelayCommand]
    private void ReturnToAssembly()
    {
        if (_returnTo is not { } back)
        {
            return;
        }

        if (Sketch is not null)
        {
            ExitSketch();
        }

        CancelEditor();
        if (_returnToSub is { } subPath)
        {
            back.Doc.Refresh(subPath);
            _returnToSub = null;
        }

        AssemblyDoc = back.Doc;
        FilePath = back.Path;
        _returnTo = null;
        Document = new PartDocument();
        _undo.Clear();
        _redo.Clear();
        SetSelection([]);
        RaiseModeChanged();
        RebuildAndRefresh();
    }

    /// <summary>The subassembly path being edited in place, when returning goes to its parent.</summary>
    private string? _returnToSub;

    // ------------------------------------------------------------- refresh

    private void RefreshAssembly()
    {
        var asm = AssemblyDoc!;
        DisplayUnits.Length = LengthUnit.Millimeter;
        _lastSolve = asm.Solve();
        UpdateInContext(asm);
        var selectedTag = SelectedNode?.Tag;
        Tree.Clear();
        foreach (var c in asm.Components)
        {
            var label = $"{(c.Fixed ? "(f) " : string.Empty)}{c.Name}{(c.Configuration is { } cfg ? $" [{cfg}]" : string.Empty)}";
            var sub = c.Suppressed ? null : SafeSubAssembly(asm, c);
            Tree.Add(new FeatureNode(label, sub is null ? "⬚" : "◫", tag: c) { Status = c.Suppressed ? FeatureStatus.Suppressed : FeatureStatus.Ok });
            foreach (var child in sub?.Components ?? [])
            {
                Tree.Add(new FeatureNode(child.Name, SubAssemblies.IsAssemblyPath(child.Path) ? "◫" : "⬚", indent: 1));
            }
        }

        if (asm.Mates.Count > 0)
        {
            Tree.Add(new FeatureNode("Mates", "⫘"));
            foreach (var m in asm.Mates)
            {
                var failed = _lastSolve.FailedMates.Contains(m);
                Tree.Add(new FeatureNode($"{m.Name} ({asm.Find(m.A.ComponentId)?.Name}, {asm.Find(m.B.ComponentId)?.Name})", "⟂", indent: 1, tag: m)
                {
                    Status = m.Suppressed ? FeatureStatus.Suppressed : failed ? FeatureStatus.Error : FeatureStatus.Ok,
                    Message = failed ? "This mate can't be satisfied with the others." : null,
                });
            }
        }

        if (asm.Features.Count > 0)
        {
            Tree.Add(new FeatureNode("Assembly Features", "⊘"));
            foreach (var f in asm.Features)
            {
                Tree.Add(new FeatureNode(f.Name, "⊘", indent: 1, tag: f) { Status = f.Suppressed ? FeatureStatus.Suppressed : FeatureStatus.Ok });
            }
        }

        if (selectedTag is not null)
        {
            SelectedNode = Tree.FirstOrDefault(n => n.Tag == selectedTag);
        }

        MessageText = _lastSolve.FailedMates.Count > 0 ? $"Mate errors: {string.Join(", ", _lastSolve.FailedMates.Select(m => m.Name))}" : string.Empty;
        StatusText = asm.Components.Count == 0 ? "Empty assembly"
            : _lastSolve.FailedMates.Count > 0 ? "Over defined"
            : _lastSolve.DegreesOfFreedom == 0 ? "Fully defined"
            : $"Under defined ({_lastSolve.DegreesOfFreedom} DOF)";
        var name = FilePath is null ? "Assembly1" : Path.GetFileNameWithoutExtension(FilePath);
        Title = $"{name}{(_dirty ? " •" : string.Empty)} — Chamfer";
        RaiseScene();
    }

    private async Task SaveAssembly(string? path)
    {
        var asm = AssemblyDoc!;
        if (path is null)
        {
            if (StorageProvider is null)
            {
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Assembly",
                DefaultExtension = AssemblyDocument.FileExtension,
                SuggestedFileName = FilePath is null ? "Assembly1" : Path.GetFileNameWithoutExtension(FilePath),
                FileTypeChoices = [AssemblyFileType],
            });
            path = file?.TryGetLocalPath();
            if (path is null)
            {
                return;
            }
        }

        // Store part paths relative to the assembly so the folder can move.
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var c in asm.Components.Where(c => Path.IsPathRooted(c.Path)))
        {
            c.Path = Path.GetRelativePath(dir, c.Path);
        }

        asm.Directory = dir;
        await File.WriteAllTextAsync(path, asm.Save());
        FilePath = path;
        _dirty = false;
        DiscardRecovery();
        RefreshAssembly();
    }

    /// <summary>A component's subassembly, or null (also when it can't be loaded; the error shows elsewhere).</summary>
    private static AssemblyDocument? SafeSubAssembly(AssemblyDocument asm, Component c)
    {
        try
        {
            return asm.SubAssemblyOf(c);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private void LoadAssembly(string path, string text)
    {
        New();
        AssemblyDoc = AssemblyDocument.Load(text, Path.GetDirectoryName(Path.GetFullPath(path)));
        FilePath = path;
        RaiseModeChanged();
        RebuildAndRefresh();
        FitAction?.Invoke();
    }
}
