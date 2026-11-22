using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Platform.Storage;
using Chamfer.App.Editing;
using Chamfer.App.Editing.Tools;
using Chamfer.App.Part;
using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching.Serialization;
using Chamfer.Viewport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>A button in the command bar.</summary>
public sealed class CommandItem(string label, string tooltip, IRelayCommand command, string glyph = "")
{
    public string Label { get; } = label;
    public string Tooltip { get; } = tooltip;
    public IRelayCommand Command { get; } = command;
    public string Glyph { get; } = glyph;
}

/// <summary>
/// The part editor: document, feature tree, selection, the active command's
/// property panel, sketch mode, files and undo. The 3D view and sketch canvas
/// are driven from here through <see cref="SceneChanged"/> and the request actions.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private static readonly FilePickerFileType PartFileType = new("Chamfer Part") { Patterns = [$"*.{PartDocument.FileExtension}"] };
    private static readonly FilePickerFileType SketchFileType = new("Chamfer Sketch") { Patterns = ["*.chsk"] };
    private static readonly FilePickerFileType StepFileType = new("STEP") { Patterns = ["*.step", "*.stp"] };
    private static readonly FilePickerFileType IgesFileType = new("IGES") { Patterns = ["*.iges", "*.igs"] };
    private static readonly FilePickerFileType StlFileType = new("STL") { Patterns = ["*.stl"] };

    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string? _editorSnapshot;
    private Action<SketchFeature>? _afterSketch;
    private Action<SelectionItem>? _pendingPlanePick;
    private bool _dirty;

    public MainViewModel()
    {
        Document = new PartDocument();
        FeatureCommands =
        [
            new("Extruded Boss", "Extrude a sketch into a solid", new RelayCommand(() => StartSketchFeature(sf => new ExtrudeFeature { SketchId = sf.Id })), "▣"),
            new("Revolved Boss", "Revolve a sketch about a centerline", new RelayCommand(() => StartSketchFeature(sf => new RevolveFeature { SketchId = sf.Id })), "◐"),
            new("Extruded Cut", "Cut by extruding a sketch", new RelayCommand(() => StartSketchFeature(sf => new ExtrudeFeature { SketchId = sf.Id, IsCut = true, EndCondition = EndCondition.ThroughAll })), "▭"),
            new("Revolved Cut", "Cut by revolving a sketch", new RelayCommand(() => StartSketchFeature(sf => new RevolveFeature { SketchId = sf.Id, IsCut = true })), "◑"),
            new("Fillet", "Round edges", new RelayCommand(() => StartFeature(new FilletFeature { Radius = 2 })), "◜"),
            new("Chamfer", "Bevel edges", new RelayCommand(() => StartFeature(new ChamferFeature { Distance = 1 })), "◸"),
            new("Shell", "Hollow the part", new RelayCommand(() => StartFeature(new ShellFeature { Thickness = 1 })), "□"),
            new("Draft", "Taper faces", new RelayCommand(() => StartFeature(new DraftFeature { Angle = 3 })), "⟋"),
            new("Hole Wizard", "Standard holes on a face", new RelayCommand(StartHole), "◎"),
            new("Linear Pattern", "Repeat features in rows", new RelayCommand(() => StartFeature(new LinearPatternFeature { Count = 3, Spacing = 20 })), "⋯"),
            new("Circular Pattern", "Repeat features around an axis", new RelayCommand(() => StartFeature(new CircularPatternFeature { Count = 6 })), "⟳"),
            new("Mirror", "Mirror features about a plane", new RelayCommand(() => StartFeature(new MirrorFeature())), "⇋"),
            new("Plane", "Reference plane", new RelayCommand(() => StartFeature(new ReferencePlaneFeature { Base = SelectedPlane() ?? PlaneRef.Front })), "▱"),
        ];
        RebuildAndRefresh();
    }

    public PartDocument Document { get; private set; }

    public PartScene SceneBuilder { get; } = new();

    public ObservableCollection<FeatureNode> Tree { get; } = [];

    public IReadOnlyList<CommandItem> FeatureCommands { get; }

    public List<SelectionItem> Selection { get; } = [];

    public SelectionItem? Hover { get; private set; }

    public ViewPalette Palette { get; set; } = ViewPalette.Light;

    /// <summary>Set by the window; used for file dialogs.</summary>
    public IStorageProvider? StorageProvider { get; set; }

    /// <summary>Raised when the 3D scene must be rebuilt (model, selection or hover changed).</summary>
    public event EventHandler? SceneChanged;

    /// <summary>Set by the window: fit the model in view.</summary>
    public Action? FitAction { get; set; }

    /// <summary>Set by the window: look normal to a plane.</summary>
    public Action<Plane3>? NormalToAction { get; set; }

    /// <summary>Set by the window: show a standard view.</summary>
    public Action<StandardView>? ViewAction { get; set; }

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "Chamfer";

    [ObservableProperty]
    public partial string HintText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MessageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial FeatureNode? SelectedNode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelMode), nameof(ShowTree))]
    public partial SketchViewModel? Sketch { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTree), nameof(HasEditor))]
    public partial FeatureEditor? Editor { get; set; }

    [ObservableProperty]
    public partial DisplayStyle DisplayStyle { get; set; } = DisplayStyle.ShadedWithEdges;

    [ObservableProperty]
    public partial bool Perspective { get; set; }

    public bool IsModelMode => Sketch is null;
    public bool IsSketchMode => Sketch is not null;
    public bool ShowTree => Editor is null;
    public bool HasEditor => Editor is not null;

    /// <summary>The sketch feature being edited in sketch mode.</summary>
    public SketchFeature? EditingSketch { get; private set; }

    partial void OnSketchChanged(SketchViewModel? value) => OnPropertyChanged(nameof(IsSketchMode));

    partial void OnDisplayStyleChanged(DisplayStyle value) => RaiseScene();

    partial void OnSelectedNodeChanged(FeatureNode? value)
    {
        if (value is null)
        {
            return;
        }

        SelectionItem? item = value.Feature is { } f ? SelectionItem.ForFeature(f) : value.PlaneId is { } p ? SelectionItem.Plane(p, value.Name) : null;
        if (item is not null && !Selection.Any(s => s.SameTarget(item)))
        {
            SetSelection([item]);
        }
    }

    // ------------------------------------------------------------- rebuild

    public void RebuildAndRefresh()
    {
        Document.Rebuild();
        RefreshTree();
        var errors = Document.Features.Where(f => Document.Results.TryGetValue(f.Id, out var r) && r.Status == FeatureStatus.Error).ToList();
        var warnings = Document.Features.Where(f => Document.Results.TryGetValue(f.Id, out var r) && r.Status == FeatureStatus.Warning).ToList();
        MessageText = errors.Count > 0
            ? $"{errors[0].Name}: {Document.Results[errors[0].Id].Message}"
            : warnings.Count > 0 ? $"{warnings[0].Name}: {Document.Results[warnings[0].Id].Message}" : string.Empty;
        if (Editor?.Feature is { } editing && Document.Results.TryGetValue(editing.Id, out var res))
        {
            Editor.Message = res.Status is FeatureStatus.Error or FeatureStatus.Warning ? res.Message : null;
        }

        RefreshStatus();
        RefreshTitle();
        RaiseScene();
    }

    private void RefreshStatus()
    {
        var bodies = Document.Bodies.Count;
        StatusText = Sketch is not null ? Sketch.StatusText
            : bodies == 0 ? "MMGS · Empty part"
            : $"MMGS · {bodies} bod{(bodies == 1 ? "y" : "ies")} · {Document.Material.Name}";
    }

    private void RefreshTitle()
    {
        var name = FilePath is null ? "Part1" : Path.GetFileNameWithoutExtension(FilePath);
        Title = $"{name}{(_dirty ? " •" : string.Empty)} — Chamfer";
    }

    private void RefreshTree()
    {
        var selectedFeature = SelectedNode?.Feature?.Id;
        Tree.Clear();
        Tree.Add(new FeatureNode($"Material <{Document.Material.Name}>", "◆"));
        Tree.Add(new FeatureNode("Front Plane", "▱", planeId: "front"));
        Tree.Add(new FeatureNode("Top Plane", "▱", planeId: "top"));
        Tree.Add(new FeatureNode("Right Plane", "▱", planeId: "right"));
        Tree.Add(new FeatureNode("Origin", "⊹"));

        // Sketches used by exactly one feature are shown under it, as in other feature-based CAD.
        var owner = new Dictionary<string, Feature>();
        foreach (var f in Document.Features.Where(f => f is not SketchFeature and not PatternFeatureBase))
        {
            foreach (var dep in f.Dependencies)
            {
                if (Document.Find(dep) is SketchFeature && !owner.ContainsKey(dep))
                {
                    owner[dep] = f;
                }
            }
        }

        var rollback = Document.RollbackIndex;
        for (var i = 0; i < Document.Features.Count; i++)
        {
            var f = Document.Features[i];
            if (rollback == i)
            {
                Tree.Add(new FeatureNode("── Rollback ──", string.Empty) { IsRollbackBar = true });
            }

            if (f is SketchFeature && owner.ContainsKey(f.Id))
            {
                continue;
            }

            Tree.Add(MakeNode(f, 0));
            foreach (var absorbed in owner.Where(kv => kv.Value == f).Select(kv => Document.Find(kv.Key)!))
            {
                Tree.Add(MakeNode(absorbed, 1));
            }
        }

        if (selectedFeature is not null)
        {
            SelectedNode = Tree.FirstOrDefault(n => n.Feature?.Id == selectedFeature);
        }
    }

    private FeatureNode MakeNode(Feature f, int indent)
    {
        var node = new FeatureNode(f.Name, FeatureNode.GlyphFor(f), f, indent: indent);
        if (Document.Results.TryGetValue(f.Id, out var r))
        {
            node.Status = r.Status;
            node.Message = r.Message;
        }

        return node;
    }

    private void RaiseScene() => SceneChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Builds the 3D scene for the current state.</summary>
    public Scene BuildScene() => SceneBuilder.Build(Document, Selection, Hover, Palette, DisplayStyle, EditingSketch?.Id);

    // ------------------------------------------------------------- undo

    private void Snapshot()
    {
        _undo.Push(Document.Save());
        _redo.Clear();
        _dirty = true;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void Restore(string json)
    {
        Document = PartDocument.Load(json);
        Selection.Clear();
        Hover = null;
        RebuildAndRefresh();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Sketch is not null)
        {
            Sketch.UndoCommand.Execute(null);
            return;
        }

        if (_undo.Count == 0)
        {
            return;
        }

        CancelEditor();
        _redo.Push(Document.Save());
        Restore(_undo.Pop());
        _dirty = true;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => Sketch?.Editor.CanUndo ?? _undo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (Sketch is not null)
        {
            Sketch.RedoCommand.Execute(null);
            return;
        }

        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(Document.Save());
        Restore(_redo.Pop());
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private bool CanRedo() => Sketch?.Editor.CanRedo ?? _redo.Count > 0;

    // ------------------------------------------------------------- selection

    public void SetSelection(IEnumerable<SelectionItem> items)
    {
        Selection.Clear();
        Selection.AddRange(items);
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        Editor?.ActiveSelection?.Sync(Selection);
        if (_measuring)
        {
            UpdateMeasure();
        }

        HintText = Selection.Count switch
        {
            0 => Sketch is null ? "Select faces, edges or planes, then pick a command. Middle-drag or Alt+drag rotates, right-drag pans, scroll zooms." : HintText,
            1 => Selection[0].Label,
            _ => $"{Selection.Count} items selected",
        };
        RaiseScene();
    }

    public SelectionItem? ToSelection(PickTarget? target) => target switch
    {
        FacePick f => SelectionItem.Face(f.Body, f.Index),
        EdgePick e => SelectionItem.Edge(e.Body, e.Index),
        PlanePick p => SelectionItem.Plane(p.PlaneId, p.Label),
        SketchPick s when Document.Find(s.FeatureId) is { } f => SelectionItem.ForFeature(f),
        _ => null,
    };

    /// <summary>The viewport hovered a pick id.</summary>
    public void OnHover(int id)
    {
        var item = ToSelection(SceneBuilder.Target(id));
        if (item?.SameTarget(Hover ?? new SelectionItem(SelectionKind.Feature)) == true || (item is null && Hover is null))
        {
            return;
        }

        Hover = item;
        RaiseScene();
    }

    /// <summary>The viewport was clicked. Ctrl/Cmd/Shift adds to the selection.</summary>
    public void OnPick(int id, bool add, int clickCount)
    {
        var item = ToSelection(SceneBuilder.Target(id));
        if (item is not null && _pendingPlanePick is { } pending && item.AsPlane() is not null)
        {
            _pendingPlanePick = null;
            pending(item);
            return;
        }

        if (clickCount >= 2 && item?.Kind == SelectionKind.Feature && Document.Find(item.FeatureId!) is SketchFeature sf)
        {
            EditSketch(sf);
            return;
        }

        if (item is null)
        {
            if (!add)
            {
                SetSelection([]);
            }

            return;
        }

        // A single-item selection field (a plane, an axis) takes the click as a replacement.
        var single = Editor?.ActiveSelection is { Max: 1 } field && field.Accept(item);
        if (add && !single)
        {
            var existing = Selection.FindIndex(s => s.SameTarget(item));
            if (existing >= 0)
            {
                Selection.RemoveAt(existing);
            }
            else
            {
                Selection.Add(item);
            }

            OnSelectionChanged();
        }
        else if (Editor?.ActiveSelection is { } active && active.Max > 1 && active.Accept(item))
        {
            // While a multi-select field is active, clicks toggle items in it.
            var existing = Selection.FindIndex(s => s.SameTarget(item));
            if (existing >= 0)
            {
                Selection.RemoveAt(existing);
            }
            else
            {
                Selection.Add(item);
            }

            OnSelectionChanged();
        }
        else
        {
            SetSelection([item]);
        }
    }

    private PlaneRef? SelectedPlane() => Selection.Select(s => s.AsPlane()).LastOrDefault(p => p is not null);

    private Plane3? ResolvePlane(PlaneRef r)
    {
        try
        {
            if (r.PlaneId is { } id && Document.State.Planes.TryGetValue(id, out var p))
            {
                return p;
            }

            if (r.Face is { } face)
            {
                foreach (var b in Document.Bodies)
                {
                    if (b.HasFace(face.Name))
                    {
                        return b.Shape.FacePlane(b.ResolveFace(face, out _));
                    }
                }
            }
        }
        catch (KernelException)
        {
        }

        return null;
    }

    // ------------------------------------------------------------- features

    private void StartFeature(Feature feature)
    {
        if (Sketch is not null)
        {
            ExitSketch();
        }

        CancelEditor();
        _editorSnapshot = Document.Save();
        Document.Add(feature);
        Editor = FeatureEditor.Create(Document, feature, isNew: true, Selection, RebuildAndRefresh);
        Editor.ActiveSelectionChanged += (_, _) => LoadActiveField();
        LoadActiveField();
        HintText = Editor.ActiveSelection?.Prompt ?? "Set the parameters, then click OK.";
        RebuildAndRefresh();
    }

    /// <summary>Makes the viewport selection show what the active selection field holds.</summary>
    private void LoadActiveField()
    {
        if (Editor?.ActiveSelection is { } field)
        {
            Selection.Clear();
            Selection.AddRange(field.Items);
            HintText = field.Prompt;
            RaiseScene();
        }
    }

    public void EditFeature(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (feature is SketchFeature sf)
        {
            EditSketch(sf);
            return;
        }

        if (Sketch is not null)
        {
            ExitSketch();
        }

        CancelEditor();
        _editorSnapshot = Document.Save();
        Editor = FeatureEditor.Create(Document, feature, isNew: false, Selection, RebuildAndRefresh);
        Editor.ActiveSelectionChanged += (_, _) => LoadActiveField();
        LoadActiveField();
        RebuildAndRefresh();
    }

    /// <summary>Starts a sketch-based feature: uses the selected or just-finished sketch, or starts a new one.</summary>
    private void StartSketchFeature(Func<SketchFeature, Feature> make)
    {
        if (Sketch is not null && EditingSketch is { } editing)
        {
            ExitSketch();
            StartFeature(make(editing));
            return;
        }

        var sketch = Selection.Where(s => s.Kind == SelectionKind.Feature).Select(s => Document.Find(s.FeatureId!)).OfType<SketchFeature>().LastOrDefault()
            ?? _lastSketch;
        var used = Document.Features.Any(f => f is not SketchFeature && sketch is not null && f.Dependencies.Contains(sketch.Id));
        if (sketch is not null && Document.Features.Contains(sketch) && (!used || Selection.Any(s => s.FeatureId == sketch.Id)))
        {
            StartFeature(make(sketch));
            return;
        }

        StartSketch(after: sf => StartFeature(make(sf)));
    }

    private SketchFeature? _lastSketch;

    private void StartHole()
    {
        if (Selection.LastOrDefault(s => s.Kind == SelectionKind.Face) is not { } face || ResolvePlane(new PlaneRef(Face: face.Ref)) is null)
        {
            MessageText = "Select a planar face for the holes, then click Hole Wizard.";
            return;
        }

        StartSketchOn(new PlaneRef(Face: face.Ref), "point", sf =>
        {
            if (!sf.Sketch.Entities.OfType<Sketching.Entities.SketchPoint>().Any())
            {
                MessageText = "Place at least one point to locate a hole.";
                return;
            }

            StartFeature(new HoleFeature { SketchId = sf.Id, Type = HoleType.Counterbore, Size = "M6", Diameter = 6.6, CounterboreDiameter = 11, CounterboreDepth = 6.5, EndCondition = EndCondition.ThroughAll });
        });
    }

    [RelayCommand]
    private void AcceptEditor()
    {
        if (Editor is null)
        {
            return;
        }

        foreach (var n in Editor.Fields.OfType<NumberField>())
        {
            n.Commit();
        }

        if (Editor.IsFeature && _editorSnapshot is not null)
        {
            _undo.Push(_editorSnapshot);
            _redo.Clear();
            _dirty = true;
            UndoCommand.NotifyCanExecuteChanged();
        }

        _editorSnapshot = null;
        _measuring = false;
        Editor = null;
        SetSelection([]);
        RebuildAndRefresh();
    }

    [RelayCommand]
    private void CancelEditor()
    {
        if (Editor is null)
        {
            return;
        }

        var feature = Editor.Feature;
        Editor = null;
        _measuring = false;
        if (feature is not null && _editorSnapshot is not null)
        {
            Restore(_editorSnapshot);
        }

        _editorSnapshot = null;
        SetSelection([]);
        RebuildAndRefresh();
    }

    // ------------------------------------------------------------- sketch mode

    [RelayCommand]
    private void NewSketch() => StartSketch(after: null);

    private void StartSketch(Action<SketchFeature>? after)
    {
        if (Sketch is not null)
        {
            ExitSketch();
        }

        if (SelectedPlane() is { } plane)
        {
            StartSketchOn(plane, null, after);
            return;
        }

        CancelEditor();
        _pendingPlanePick = item => StartSketchOn(item.AsPlane()!, null, after);
        SceneBuilder.VisiblePlanes.UnionWith(["front", "top", "right"]);
        HintText = "Select a plane or a planar face to sketch on.";
        RaiseScene();
    }

    private void StartSketchOn(PlaneRef plane, string? tool, Action<SketchFeature>? after)
    {
        CancelEditor();
        SceneBuilder.VisiblePlanes.Clear();
        if (ResolvePlane(plane) is null)
        {
            MessageText = "Sketches need a plane or a planar face.";
            return;
        }

        Snapshot();
        var sf = Document.Add(new SketchFeature { Plane = plane });
        RebuildAndRefresh();
        EnterSketch(sf, after);
        if (tool is not null)
        {
            Sketch?.ActivateTool(tool);
        }
    }

    public void EditSketch(SketchFeature sf)
    {
        ArgumentNullException.ThrowIfNull(sf);
        if (Sketch is not null)
        {
            ExitSketch();
        }

        CancelEditor();
        Snapshot();
        EnterSketch(sf, null);
    }

    private void EnterSketch(SketchFeature sf, Action<SketchFeature>? after)
    {
        var editor = new SketchEditor();
        editor.Load(SketchSerializer.Serialize(sf.Sketch), null);
        EditingSketch = sf;
        _afterSketch = after;
        Sketch = new SketchViewModel(editor, ConvertAt);
        Sketch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SketchViewModel.StatusText))
            {
                RefreshStatus();
            }
        };
        editor.Changed += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
        SetSelection([]);
        RefreshStatus();
        HintText = Sketch.HintText;
        if (ResolvePlane(sf.Plane) is { } plane)
        {
            NormalToAction?.Invoke(plane);
        }

        RaiseScene();
    }

    [RelayCommand]
    private void ExitSketch()
    {
        if (Sketch is null || EditingSketch is null)
        {
            return;
        }

        var sf = EditingSketch;
        sf.Sketch = Sketch.Editor.Sketch;
        Sketch = null;
        EditingSketch = null;
        _lastSketch = sf;
        _dirty = true;
        RebuildAndRefresh();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        var after = _afterSketch;
        _afterSketch = null;
        if (after is not null)
        {
            after(sf);
        }
        else
        {
            SetSelection([SelectionItem.ForFeature(sf)]);
        }
    }

    /// <summary>Set by the window: the model edge drawn behind the sketch at a sketch-space point.</summary>
    public Func<Sketching.Vec2, (Body Body, int Edge)?>? PickUnderlayEdge { get; set; }

    private bool ConvertAt(Sketching.Vec2 at)
    {
        if (PickUnderlayEdge?.Invoke(at) is not { } hit || Sketch is null || EditingPlane is not { } plane)
        {
            return false;
        }

        var editor = Sketch.Editor;
        editor.BeginChange();
        var made = EdgeProjection.Project(editor.Sketch, hit.Body, hit.Edge, plane);
        if (made.Count == 0)
        {
            editor.CancelChange();
            return false;
        }

        editor.EndChange(solve: true);
        return true;
    }

    /// <summary>Converts the outline of the face the sketch is on.</summary>
    [RelayCommand]
    private void ConvertFace()
    {
        if (Sketch is null || EditingSketch?.Plane.Face is not { } faceRef || EditingPlane is not { } plane)
        {
            MessageText = "Convert Face works on sketches placed on a model face.";
            return;
        }

        var body = Document.Bodies.FirstOrDefault(b => b.HasFace(faceRef.Name));
        if (body is null)
        {
            return;
        }

        var face = body.ResolveFace(faceRef, out _);
        var editor = Sketch.Editor;
        editor.BeginChange();
        foreach (var edge in body.Shape.EdgesOfFace(face))
        {
            EdgeProjection.Project(editor.Sketch, body, edge, plane);
        }

        editor.EndChange(solve: true);
    }

    /// <summary>Plane of the sketch being edited.</summary>
    public Plane3? EditingPlane => EditingSketch is null ? null : ResolvePlane(EditingSketch.Plane);

    // ------------------------------------------------------------- tree actions

    [RelayCommand]
    private void EditSelected()
    {
        if (SelectedNode?.Feature is { } f)
        {
            EditFeature(f);
        }
    }

    [RelayCommand]
    private void ToggleSuppress()
    {
        if (SelectedNode?.Feature is not { } f)
        {
            return;
        }

        Snapshot();
        f.IsSuppressed = !f.IsSuppressed;
        RebuildAndRefresh();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Sketch is not null)
        {
            Sketch.DeleteCommand.Execute(null);
            return;
        }

        var feature = SelectedNode?.Feature ?? Selection.Where(s => s.Kind == SelectionKind.Feature).Select(s => Document.Find(s.FeatureId!)).LastOrDefault();
        if (feature is null)
        {
            return;
        }

        Snapshot();
        var removed = Document.Delete(feature);
        if (removed.Count > 1)
        {
            MessageText = $"Deleted {feature.Name} and {removed.Count - 1} dependent feature(s).";
        }

        SelectedNode = null;
        SetSelection([]);
        RebuildAndRefresh();
    }

    [RelayCommand]
    private void RollbackHere()
    {
        if (SelectedNode?.Feature is not { } f)
        {
            return;
        }

        var i = Document.Features.IndexOf(f);
        Document.RollbackIndex = i + 1 >= Document.Features.Count ? null : i + 1;
        RebuildAndRefresh();
    }

    [RelayCommand]
    private void RollForward()
    {
        Document.RollbackIndex = null;
        RebuildAndRefresh();
    }

    [RelayCommand]
    private void SetMaterial(Material material)
    {
        Snapshot();
        Document.Material = material;
        RebuildAndRefresh();
        if (_massPanel)
        {
            ShowMassProperties();
        }
    }

    // ------------------------------------------------------------- evaluate

    private bool _measuring;
    private bool _massPanel;

    [RelayCommand]
    private void ShowMassProperties()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Mass Properties");
        var m = Document.MassProperties();
        var inv = CultureInfo.InvariantCulture;
        panel.AddInfo(new InfoField("Material", $"{Document.Material.Name} ({Document.Material.Density.ToString("0", inv)} kg/m³)"));
        panel.AddInfo(new InfoField("Mass", $"{(m.Mass * 1000).ToString("0.###", inv)} g"));
        panel.AddInfo(new InfoField("Volume", $"{m.Volume.ToString("0.###", inv)} mm³"));
        panel.AddInfo(new InfoField("Surface area", $"{m.SurfaceArea.ToString("0.###", inv)} mm²"));
        panel.AddInfo(new InfoField("Centre of mass", m.CenterOfMass.ToString()));
        var box = Document.Bodies.Aggregate(Box3.Empty, (b, body) => b.Union(body.Shape.Bounds));
        if (!box.IsEmpty)
        {
            panel.AddInfo(new InfoField("Bounding box", $"{box.Size.X.ToString("0.###", inv)} × {box.Size.Y.ToString("0.###", inv)} × {box.Size.Z.ToString("0.###", inv)} mm"));
        }

        _massPanel = true;
        Editor = panel;
    }

    [RelayCommand]
    private void Measure()
    {
        CancelEditor();
        _measuring = true;
        _massPanel = false;
        Editor = FeatureEditor.Info("Measure");
        UpdateMeasure();
    }

    private void UpdateMeasure()
    {
        if (Editor is not { IsInfo: true } panel)
        {
            return;
        }

        panel.Fields.Clear();
        var inv = CultureInfo.InvariantCulture;
        var shapes = new List<(string Label, Shape Shape)>();
        foreach (var s in Selection)
        {
            foreach (var body in Document.Bodies)
            {
                if (s.Kind == SelectionKind.Face && body.HasFace(s.Ref!.Name))
                {
                    var i = body.ResolveFace(s.Ref, out _);
                    var f = body.Face(i);
                    panel.AddInfo(new InfoField(s.Label, f.Surface switch
                    {
                        SurfaceKind.Cylinder => $"Area {f.Area.ToString("0.###", inv)} mm², ⌀{(2 * f.Radius).ToString("0.###", inv)} mm",
                        _ => $"Area {f.Area.ToString("0.###", inv)} mm²",
                    }));
                    shapes.Add((s.Label, body.Shape.Sub(TopoKind.Face, i)));
                }
                else if (s.Kind == SelectionKind.Edge && body.HasEdge(s.Ref!.Name))
                {
                    var i = body.ResolveEdge(s.Ref, out _);
                    var e = body.Edge(i);
                    panel.AddInfo(new InfoField(s.Label, e.Curve == CurveKind.Circle
                        ? $"Length {e.Length.ToString("0.###", inv)} mm, ⌀{(2 * e.Radius).ToString("0.###", inv)} mm"
                        : $"Length {e.Length.ToString("0.###", inv)} mm"));
                    shapes.Add((s.Label, body.Shape.Sub(TopoKind.Edge, i)));
                }
            }
        }

        if (shapes.Count == 2)
        {
            try
            {
                var (d, p, q) = shapes[0].Shape.DistanceTo(shapes[1].Shape);
                panel.AddInfo(new InfoField("Distance", $"{d.ToString("0.####", inv)} mm"));
                var delta = q - p;
                panel.AddInfo(new InfoField("dX, dY, dZ", $"{delta.X.ToString("0.###", inv)}, {delta.Y.ToString("0.###", inv)}, {delta.Z.ToString("0.###", inv)}"));
            }
            catch (KernelException ex)
            {
                panel.AddInfo(new InfoField("Distance", ex.Message));
            }
        }

        foreach (var (_, shape) in shapes)
        {
            shape.Dispose();
        }

        if (panel.Fields.Count == 0)
        {
            panel.AddInfo(new InfoField("Measure", "Select one or two faces or edges."));
        }
    }

    // ------------------------------------------------------------- view

    [RelayCommand]
    private void ZoomToFit()
    {
        if (Sketch is not null)
        {
            Sketch.ZoomToFitCommand.Execute(null);
        }
        else
        {
            FitAction?.Invoke();
        }
    }

    [RelayCommand]
    private void ShowView(string view)
    {
        if (view == "normal")
        {
            var plane = Selection.Select(s => s.AsPlane()).OfType<PlaneRef>().Select(ResolvePlane).LastOrDefault(p => p is not null);
            if (plane is { } p)
            {
                NormalToAction?.Invoke(p);
            }
            else
            {
                MessageText = "Select a plane or planar face to look normal to.";
            }

            return;
        }

        ViewAction?.Invoke(Enum.Parse<StandardView>(view, ignoreCase: true));
    }

    [RelayCommand]
    private void SetDisplayStyle(string style) => DisplayStyle = Enum.Parse<DisplayStyle>(style, ignoreCase: true);

    // ------------------------------------------------------------- files

    [RelayCommand]
    private void New()
    {
        if (Sketch is not null)
        {
            Sketch = null;
            EditingSketch = null;
        }

        Editor = null;
        Document = new PartDocument();
        FilePath = null;
        _undo.Clear();
        _redo.Clear();
        _dirty = false;
        _lastSketch = null;
        SetSelection([]);
        RebuildAndRefresh();
        ViewAction?.Invoke(StandardView.Trimetric);
    }

    /// <summary>Opens a part (or a standalone sketch, placed on the Front plane of a new part).</summary>
    public void LoadFile(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.EndsWith(".chsk", StringComparison.OrdinalIgnoreCase))
        {
            New();
            var sf = Document.Add(new SketchFeature { Sketch = SketchSerializer.Deserialize(text) });
            RebuildAndRefresh();
            SetSelection([SelectionItem.ForFeature(sf)]);
            ViewAction?.Invoke(StandardView.Front);
            return;
        }

        var doc = PartDocument.Load(text);
        New();
        Document = doc;
        FilePath = path;
        RebuildAndRefresh();
        FitAction?.Invoke();
    }

    [RelayCommand]
    private async Task Open()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            FileTypeFilter = [PartFileType, SketchFileType],
            AllowMultiple = false,
        });
        if (files is not [var file])
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            LoadFile(file.TryGetLocalPath() ?? file.Name, await reader.ReadToEndAsync());
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException or UnauthorizedAccessException)
        {
            MessageText = $"Couldn't open {file.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Save() => await SaveTo(FilePath is { } path && File.Exists(path) ? path : null);

    [RelayCommand]
    private async Task SaveAs() => await SaveTo(null);

    private async Task SaveTo(string? path)
    {
        if (Sketch is not null)
        {
            ExitSketch();
        }

        try
        {
            if (path is null)
            {
                if (StorageProvider is null)
                {
                    return;
                }

                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save Part",
                    DefaultExtension = PartDocument.FileExtension,
                    SuggestedFileName = FilePath is null ? "Part1" : Path.GetFileNameWithoutExtension(FilePath),
                    FileTypeChoices = [PartFileType],
                });
                path = file?.TryGetLocalPath();
                if (path is null)
                {
                    return;
                }
            }

            await File.WriteAllTextAsync(path, Document.Save());
            FilePath = path;
            _dirty = false;
            RefreshTitle();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText = $"Couldn't save: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import",
            FileTypeFilter = [StepFileType, IgesFileType],
            AllowMultiple = false,
        });
        if (files is not [var file] || file.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            Snapshot();
            Document.Add(ImportFeature.FromFile(path));
            RebuildAndRefresh();
            FitAction?.Invoke();
        }
        catch (Exception ex) when (ex is KernelException or NotSupportedException or IOException)
        {
            MessageText = $"Couldn't import {file.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Export()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export",
            DefaultExtension = "step",
            SuggestedFileName = FilePath is null ? "Part1" : Path.GetFileNameWithoutExtension(FilePath),
            FileTypeChoices = [StepFileType, IgesFileType, StlFileType],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            Document.Export(path);
            MessageText = $"Exported {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is KernelException or NotSupportedException or IOException or InvalidOperationException)
        {
            MessageText = $"Couldn't export: {ex.Message}";
        }
    }

    /// <summary>Escape: cancel the sketch tool, the property panel, a pending pick, or clear the selection.</summary>
    public void Escape()
    {
        if (Sketch is not null)
        {
            Sketch.Escape();
            return;
        }

        if (_pendingPlanePick is not null)
        {
            _pendingPlanePick = null;
            SceneBuilder.VisiblePlanes.Clear();
            HintText = string.Empty;
            RaiseScene();
            return;
        }

        if (Editor is not null)
        {
            CancelEditor();
            return;
        }

        SetSelection([]);
    }
}
