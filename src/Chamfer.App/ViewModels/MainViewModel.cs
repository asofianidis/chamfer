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
public sealed class CommandItem(string label, string tooltip, IRelayCommand command, string glyph = "", string tab = "Features")
{
    public string Tab { get; } = tab;

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
    private static readonly FilePickerFileType ThreeMfFileType = new("3MF") { Patterns = ["*.3mf"] };
    private static readonly FilePickerFileType GlbFileType = new("glTF binary") { Patterns = ["*.glb"] };
    private static readonly FilePickerFileType HtmlFileType = new("3D web page (HTML viewer)") { Patterns = ["*.html"] };
    private static readonly FilePickerFileType ObjFileType = new("OBJ") { Patterns = ["*.obj"] };
    private static readonly FilePickerFileType CsvFileType = new("Design table (CSV)") { Patterns = ["*.csv"] };

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
            new("Swept Boss", "Sweep a profile sketch along a path sketch", new RelayCommand(() => StartFeature(NewSweep(cut: false))), "⤳"),
            new("Lofted Boss", "Loft through profile sketches in order", new RelayCommand(() => StartFeature(NewLoft(cut: false))), "⏢"),
            new("Swept Cut", "Cut by sweeping a profile along a path", new RelayCommand(() => StartFeature(NewSweep(cut: true))), "⤳"),
            new("Lofted Cut", "Cut by lofting through profiles", new RelayCommand(() => StartFeature(NewLoft(cut: true))), "⏢"),
            new("Rib", "Thicken an open sketch into the part", new RelayCommand(() => StartSketchFeature(sf => new RibFeature { SketchId = sf.Id, Thickness = 2 })), "⊿"),
            new("Fillet", "Round edges", new RelayCommand(() => StartFeature(new FilletFeature { Radius = 2 })), "◜"),
            new("Chamfer", "Bevel edges", new RelayCommand(() => StartFeature(new ChamferFeature { Distance = 1 })), "◸"),
            new("Shell", "Hollow the part", new RelayCommand(() => StartFeature(new ShellFeature { Thickness = 1 })), "□"),
            new("Draft", "Taper faces", new RelayCommand(() => StartFeature(new DraftFeature { Angle = 3 })), "⟋"),
            new("Hole Wizard", "Standard holes on a face", new RelayCommand(StartHole), "◎"),
            new("Linear Pattern", "Repeat features in rows", new RelayCommand(() => StartFeature(new LinearPatternFeature { Count = 3, Spacing = 20 })), "⋯"),
            new("Circular Pattern", "Repeat features around an axis", new RelayCommand(() => StartFeature(new CircularPatternFeature { Count = 6 })), "⟳"),
            new("Sketch Pattern", "Copy features to the points of a sketch", new RelayCommand(() => StartFeature(new SketchDrivenPatternFeature())), "⁘"),
            new("Table Pattern", "Copy features to a list of X, Y offsets", new RelayCommand(() => StartFeature(new TablePatternFeature())), "▦"),
            new("Curve Pattern", "Repeat features along a sketched curve", new RelayCommand(() => StartFeature(new CurveDrivenPatternFeature())), "∿"),
            new("Fill Pattern", "Fill a face with copies on a grid", new RelayCommand(() => StartFeature(new FillPatternFeature())), "⣿"),
            new("Mirror", "Mirror features about a plane", new RelayCommand(() => StartFeature(new MirrorFeature())), "⇋"),
            new("Dome", "Raise a spherical dome on a flat face", new RelayCommand(() => StartFeature(new DomeFeature { Face = Selection.FirstOrDefault(s => s.Kind == SelectionKind.Face)?.Ref })), "◠"),
            new("Mate Reference", "Faces this part mates by automatically when inserted", new RelayCommand(StartMateReference), "⫘"),
            new("Plane", "Reference plane", new RelayCommand(() => StartFeature(new ReferencePlaneFeature { Base = SelectedPlane() ?? PlaneRef.Front })), "▱"),
            new("3D Sketch", "Polyline or spline through points in space, for sweep paths", new RelayCommand(() => StartFeature(new Sketch3DFeature
            {
                Points = [new(0, 0, 0), new(0, 0, 50), new(50, 0, 50)],
            })), "⟋"),
            new("Helix", "Helix on a sketch circle, for springs and sweeps", new RelayCommand(() => StartSketchFeature(sf => new HelixFeature { SketchId = sf.Id })), "➰"),
            new("Thread", "Model a metric thread on a cylindrical face", new RelayCommand(StartThread), "⩫"),

            new("Extruded Surface", "Extrude sketch curves into a surface", new RelayCommand(() => StartSketchFeature(sf => new SurfaceExtrudeFeature { SketchId = sf.Id })), "▤", "Surfaces"),
            new("Revolved Surface", "Revolve sketch curves about a centerline", new RelayCommand(() => StartSketchFeature(sf => new SurfaceRevolveFeature { SketchId = sf.Id })), "◐", "Surfaces"),
            new("Planar Surface", "Flat surface from closed sketch regions", new RelayCommand(() => StartSketchFeature(sf => new PlanarSurfaceFeature { SketchId = sf.Id })), "▭", "Surfaces"),
            new("Filled Surface", "Smooth patch inside a closed sketch boundary", new RelayCommand(() => StartSketchFeature(sf => new FilledSurfaceFeature { SketchId = sf.Id })), "◍", "Surfaces"),
            new("Swept Surface", "Sweep a profile sketch's curves along a path", new RelayCommand(() => StartFeature(NewSweep(cut: false, surface: true))), "⤳", "Surfaces"),
            new("Offset Surface", "Copy faces offset along their normals", new RelayCommand(() => StartFeature(new OffsetSurfaceFeature { Distance = 2 })), "⧈", "Surfaces"),
            new("Knit", "Sew surfaces together (closed ones become solids)", new RelayCommand(() => StartFeature(new KnitFeature { Bodies = SelectedBodyNames() })), "⨝", "Surfaces"),
            new("Cut With Surface", "Remove the material on one side of a surface or plane", new RelayCommand(() => StartFeature(new CutWithSurfaceFeature
            {
                Surface = Selection.FirstOrDefault(s => s.Kind == SelectionKind.Face && s.Ref is { } r && Document.Bodies.Any(b => b.IsSurface && b.HasFace(r.Name)))?.Ref,
                Plane = SelectedPlane() ?? PlaneRef.Right,
            })), "⫻", "Surfaces"),
            new("Thicken", "Give a surface thickness", new RelayCommand(() => StartFeature(new ThickenFeature { Body = SelectedBodyNames().FirstOrDefault() ?? string.Empty, Thickness = 1 })), "▥", "Surfaces"),

            new("Base Flange", "Sheet metal from a sketch: closed makes a plate, open makes a bent sheet", new RelayCommand(() => StartSketchFeature(sf => new BaseFlangeFeature { SketchId = sf.Id })), "▬", "Sheet Metal"),
            new("Edge Flange", "Bend a flange up from straight edges", new RelayCommand(() => StartFeature(new EdgeFlangeFeature { Edges = [.. Selection.Where(s => s.Kind == SelectionKind.Edge).Select(s => s.Ref!)] })), "⌐", "Sheet Metal"),
            new("Hem", "Fold edges back on themselves (closed or open)", new RelayCommand(() => StartFeature(new HemFeature { Edges = [.. Selection.Where(s => s.Kind == SelectionKind.Edge).Select(s => s.Ref!)] })), "⊃", "Sheet Metal"),
            new("Sketched Bend", "Fold the sheet along a sketched line", new RelayCommand(() => StartSketchFeature(sf => new SketchedBendFeature { SketchId = sf.Id })), "⌒", "Sheet Metal"),
            new("Dimple", "Forming tool: round dimples at sketch points", new RelayCommand(() => StartSketchFeature(sf => new DimpleFeature { SketchId = sf.Id })), "◉", "Sheet Metal"),
            new("Tab", "Add flat sheet from a sketch on the sheet face", new RelayCommand(() => StartSketchFeature(sf => new TabFeature { SketchId = sf.Id })), "⊓", "Sheet Metal"),
            new("Flatten", "Show the flat pattern (toggle)", new RelayCommand(ToggleFlat), "▭", "Sheet Metal"),
            new("Export Flat DXF", "Save the flat pattern for cutting", new AsyncRelayCommand(ExportFlatDxf), "⇩", "Sheet Metal"),

            new("Structural Member", "Profiles along sketch lines, mitred at corners", new RelayCommand(() => StartSketchFeature(sf => new StructuralMemberFeature { SketchId = sf.Id })), "⌗", "Weldments"),
            new("Cut List", "Members grouped by profile and length", new RelayCommand(ShowCutList), "☰", "Weldments"),
            new("Pipe Route", "Pipe, tube or cable along 3D points with bends", new RelayCommand(() => StartFeature(new RouteFeature
            {
                Points = [new(0, 0, 0), new(0, 0, 100), new(100, 0, 100)],
            })), "⌇", "Weldments"),

            new("Parting Lines", "Find where faces turn from the pull direction (pull along Z)", new RelayCommand(ShowPartingLines), "≡", "Mold Tools"),
            new("Tooling Split", "Core and cavity blocks from a flat parting plane", new RelayCommand(() => StartFeature(new ToolingSplitFeature())), "⊟", "Mold Tools"),
            new("Draft Check", "Colour faces by draft against the pull direction", new RelayCommand(ToggleDraftAnalysis), "◩", "Mold Tools"),

            new("Combine", "Add, subtract or intersect bodies", new RelayCommand(StartCombine), "⊕", "Bodies"),
            new("Split", "Cut bodies in two with a plane", new RelayCommand(() => StartFeature(new SplitBodyFeature { Plane = SelectedPlane() ?? PlaneRef.Right })), "⫽", "Bodies"),
            new("Move/Copy Body", "Move, rotate or copy bodies", new RelayCommand(() => StartFeature(new MoveCopyBodyFeature { Bodies = SelectedBodyNames() })), "✥", "Bodies"),
            new("Delete Face", "Remove faces (holes, fillets, bosses) and heal the gap", new RelayCommand(() => StartFeature(new DeleteFaceFeature { Faces = [.. Selection.Where(s => s.Kind == SelectionKind.Face).Select(s => s.Ref!)] })), "⌫", "Surfaces"),
            new("Recognize Holes", "Turn an imported body's round holes into editable features", new RelayCommand(RecognizeHoles), "⊚", "Bodies"),
            new("Import Diagnostics", "Check bodies for gaps, open edges and bad faces", new RelayCommand(ShowDiagnostics), "✚", "Bodies"),
            new("Heal", "Sew gaps and repair faces (for imported bodies)", new RelayCommand(() => StartFeature(new HealFeature { Bodies = SelectedBodyNames() })), "✚", "Bodies"),
            new("Scale", "Scale bodies uniformly", new RelayCommand(() => StartFeature(new ScaleFeature { Bodies = SelectedBodyNames() })), "⤢", "Bodies"),
            new("Delete/Keep Body", "Remove bodies, or keep only some", new RelayCommand(() => StartFeature(new DeleteBodyFeature { Bodies = SelectedBodyNames() })), "✕", "Bodies"),
            new("Appearance", "Colour bodies", new RelayCommand(() => StartFeature(new AppearanceFeature { Bodies = SelectedBodyNames() })), "◆", "Bodies"),
        ];
        FeatureCommands = [.. FeatureCommands, .. SimulationCommands];
        VisibleFeatureCommands = [.. FeatureCommands.Where(c => c.Tab == "Features")];
        RebuildAndRefresh();
    }

    public PartDocument Document { get; private set; }

    public PartScene SceneBuilder { get; } = new();

    public ObservableCollection<FeatureNode> Tree { get; } = [];

    public ObservableCollection<string> ConfigurationNames { get; } = [];

    [ObservableProperty]
    public partial string? ActiveConfigurationName { get; set; }

    private bool _syncingConfigurations;

    partial void OnActiveConfigurationNameChanged(string? value)
    {
        if (_syncingConfigurations || value is null || value == Document.ActiveConfiguration)
        {
            return;
        }

        CancelEditor();
        Document.ActiveConfiguration = value;
        RebuildAndRefresh();
    }

    private void RefreshConfigurations()
    {
        _syncingConfigurations = true;
        var names = Document.Configurations.Select(c => c.Name).ToList();
        if (!names.SequenceEqual(ConfigurationNames))
        {
            ConfigurationNames.Clear();
            foreach (var n in names)
            {
                ConfigurationNames.Add(n);
            }
        }

        ActiveConfigurationName = Document.Active.Name;
        _syncingConfigurations = false;
    }

    [ObservableProperty]
    public partial IReadOnlyList<CommandItem> FeatureCommands { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> CommandTabs { get; set; } = ["Features", "Surfaces", "Sheet Metal", "Weldments", "Mold Tools", "Bodies", "Simulation"];

    private void RecognizeHoles()
    {
        var body = Selection.Where(s => s.Ref is not null).Select(s => Document.Bodies.FirstOrDefault(b => b.HasFace(s.Ref!.Name))).FirstOrDefault(b => b is not null)
            ?? Document.Bodies.FirstOrDefault(b => !b.IsSurface);
        if (body is null)
        {
            MessageText = "There's no solid to recognise holes in.";
            return;
        }

        Snapshot();
        var count = FeatureRecognition.RecognizeHoles(Document, body);
        RebuildAndRefresh();
        MessageText = count == 0 ? "No round through holes found." : $"Recognised {count} hole(s) as editable Hole features.";
    }

    /// <summary>Selects the parting-line edges of every solid for a pull along +Z (the Front plane's normal).</summary>
    private void ShowPartingLines()
    {
        var picks = new List<SelectionItem>();
        foreach (var body in Document.Bodies.Where(b => !b.IsSurface))
        {
            picks.AddRange(PartingLines.Find(body, Vec3.UnitZ).Select(e => SelectionItem.Edge(body, e)));
        }

        SetSelection(picks);
        MessageText = picks.Count == 0 ? "No parting line found for a pull along Z." : $"Parting line: {picks.Count} edge(s) for a pull along Z (selected).";
    }

    private void ShowCutList()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Cut List");
        var items = CutList.Build(Document);
        if (items.Count == 0)
        {
            panel.AddInfo(new InfoField("Cut list", "No structural members yet."));
        }

        foreach (var item in items)
        {
            panel.AddInfo(new InfoField($"{item.Item}. {item.Description}",
                string.Create(CultureInfo.InvariantCulture, $"Length {item.Length:0.#} mm · Qty {item.Quantity} · {item.MassEach:0.###} kg each")));
        }

        Editor = panel;
    }

    /// <summary>Showing the sheet metal flat pattern instead of the folded part.</summary>
    [ObservableProperty]
    public partial bool ShowFlat { get; set; }

    partial void OnShowFlatChanged(bool value)
    {
        RaiseScene();
        FitAction?.Invoke();
    }

    private void ToggleFlat()
    {
        if (Document.State.SheetMetal is null)
        {
            MessageText = "This part has no sheet metal. Start with a Base Flange.";
            return;
        }

        ShowFlat = !ShowFlat;
    }

    private (RebuildState State, Chamfer.Kernel.Occt.Shape Flat)? _flatCache;

    private Chamfer.Kernel.Occt.Shape? FlatShape()
    {
        if (Document.State.SheetMetal is null)
        {
            return null;
        }

        if (_flatCache is { } c && ReferenceEquals(c.State, Document.State))
        {
            return c.Flat;
        }

        try
        {
            var flat = FlatPattern.Solid(Document);
            _flatCache = (Document.State, flat);
            return flat;
        }
        catch (Exception ex) when (ex is Chamfer.Kernel.Occt.KernelException or InvalidOperationException)
        {
            MessageText = $"The flat pattern couldn't be built: {ex.Message}";
            return null;
        }
    }

    private async Task ExportFlatDxf()
    {
        if (StorageProvider is null || Document.State.SheetMetal is null)
        {
            MessageText = "This part has no sheet metal to flatten.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Flat Pattern",
            DefaultExtension = "dxf",
            SuggestedFileName = (FilePath is null ? "Part1" : Path.GetFileNameWithoutExtension(FilePath)) + "-flat",
            FileTypeChoices = [new FilePickerFileType("DXF") { Patterns = ["*.dxf"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, Chamfer.Drawing.Exporters.Dxf(Chamfer.Drawing.FlatPatternExport.Graphics(Document)));
            MessageText = $"Exported {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Chamfer.Kernel.Occt.KernelException)
        {
            MessageText = $"Couldn't export the flat pattern: {ex.Message}";
        }
    }

    [ObservableProperty]
    public partial string CommandTab { get; set; } = "Features";

    [ObservableProperty]
    public partial IReadOnlyList<CommandItem> VisibleFeatureCommands { get; set; }

    partial void OnCommandTabChanged(string value) => VisibleFeatureCommands = [.. FeatureCommands.Where(c => c.Tab == value)];

    [RelayCommand]
    private void SelectTab(string tab) => CommandTab = tab;

    /// <summary>Names of the bodies owning the selected faces and edges.</summary>
    private List<string> SelectedBodyNames() =>
        [.. Selection.Where(s => s.Ref is not null).Select(s => Document.Bodies.FirstOrDefault(b => b.HasFace(s.Ref!.Name) || b.HasEdge(s.Ref!.Name))?.Name).OfType<string>().Distinct()];

    /// <summary>ISO coarse pitch for a nominal diameter.</summary>
    private static double CoarsePitch(double diameter) =>
        new (double D, double P)[] { (3, 0.5), (4, 0.7), (5, 0.8), (6, 1), (8, 1.25), (10, 1.5), (12, 1.75), (16, 2), (20, 2.5), (24, 3), (30, 3.5) }
            .MinBy(x => Math.Abs(x.D - diameter)).P;

    private void StartThread()
    {
        var face = Selection.LastOrDefault(s => s.Kind == SelectionKind.Face);
        var pitch = 1.0;
        if (face?.Ref is { } r && Document.Bodies.FirstOrDefault(b => b.HasFace(r.Name)) is { } body)
        {
            var info = body.Face(body.ResolveFace(r, out _));
            if (info.Surface == Chamfer.Kernel.Occt.SurfaceKind.Cylinder)
            {
                pitch = CoarsePitch(2 * info.Radius);
            }
        }

        StartFeature(new ThreadFeature { Face = face?.Ref, Pitch = pitch });
    }

    private void StartCombine()
    {
        var bodies = SelectedBodyNames();
        StartFeature(new CombineFeature
        {
            MainBody = bodies.Count > 0 ? bodies[0] : Document.Bodies.Count > 0 ? Document.Bodies[0].Name : string.Empty,
            ToolBodies = [.. bodies.Skip(1)],
        });
    }

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

    public bool IsModelMode => Sketch is null && DrawingDoc is null;
    public bool IsSketchMode => Sketch is not null;
    public bool ShowTree => Editor is null;
    public bool HasEditor => Editor is not null;

    /// <summary>The sketch feature being edited in sketch mode.</summary>
    public SketchFeature? EditingSketch { get; private set; }

    partial void OnSketchChanged(SketchViewModel? value)
    {
        OnPropertyChanged(nameof(IsSketchMode));
        RaiseModeChanged();
    }

    partial void OnDisplayStyleChanged(DisplayStyle value) => RaiseScene();

    partial void OnSelectedNodeChanged(FeatureNode? value)
    {
        if (value is null)
        {
            return;
        }

        SelectionItem? item = value.Tag is Chamfer.Assembly.Component c ? ComponentItem(c)
            : value.Feature is { } f ? SelectionItem.ForFeature(f)
            : value.PlaneId is { } p ? SelectionItem.Plane(p, value.Name) : null;
        if (item is not null && !Selection.Any(s => s.SameTarget(item)))
        {
            SetSelection([item]);
        }
    }

    // ------------------------------------------------------------- rebuild

    public void RebuildAndRefresh()
    {
        if (DrawingDoc is not null)
        {
            RefreshDrawing();
            return;
        }

        if (AssemblyDoc is not null)
        {
            RefreshAssembly();
            return;
        }

        Document.Rebuild();
        RefreshConfigurations();
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
        DisplayUnits.Length = Document.Units;
        var system = Units.SystemName(Document.Units);
        var bodies = Document.Bodies.Count;
        StatusText = Sketch is not null ? Sketch.StatusText
            : bodies == 0 ? $"{system} · Empty part"
            : $"{system} · {bodies} bod{(bodies == 1 ? "y" : "ies")} · {Document.Material.Name}";
    }

    /// <summary>Document units: lengths show and are typed in this unit.</summary>
    [RelayCommand]
    private void ShowUnits()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Units");
        var units = Enum.GetValues<LengthUnit>();
        panel.AddInfo(new ChoiceField("Unit system", ["MMGS (millimetre)", "CGS (centimetre)", "MKS (metre)", "IPS (inch)"], Array.IndexOf(units, Document.Units), i =>
        {
            Snapshot();
            Document.Units = units[i];
            RebuildAndRefresh();
            ShowUnits();
        }));
        panel.AddInfo(new InfoField("Typing values", "Numbers are in the document unit; add a unit to use another (25mm, 1in, 2.5cm)."));
        Editor = panel;
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
    public Scene BuildScene()
    {
        if (AssemblyDoc is { } asm)
        {
            return AssemblyBuilder.Build(asm, Selection, Hover, Palette, DisplayStyle, Exploded);
        }

        if (ResultScene() is { } results)
        {
            return results;
        }

        if (ShowFlat && FlatShape() is { } flat && Document.State.SheetMetal is { } sm)
        {
            var scene = new Scene { Style = DisplayStyle, BackgroundTop = Palette.Top, BackgroundBottom = Palette.Bottom };
            scene.Meshes.Add(new MeshItem { Mesh = flat.Tessellate(0.02, 0.25), Color = Palette.Body, EdgeColor = Palette.Edge });
            var lift = sm.FlatExtrusion.Normalized() * -0.01;
            foreach (var (a, b) in sm.BendLines)
            {
                scene.Lines.Add(new LineItem
                {
                    Points = [sm.FlatPlane.ToWorld(a.X, a.Y) + lift, sm.FlatPlane.ToWorld(b.X, b.Y) + lift],
                    Color = Palette.Sketch,
                    Dashed = true,
                    OnTop = true,
                });
            }

            return scene;
        }

        var model = ApplyAnalysis(SceneBuilder.Build(Document, Selection, Hover, Palette, DisplayStyle, EditingSketch?.Id));
        AddHandle(model);
        return model;
    }

    // ------------------------------------------------------------- undo

    private string CurrentState() => DrawingDoc is { } drw ? "drw:" + drw.Save() : AssemblyDoc is { } asm ? "asm:" + asm.Save() : Document.Save();

    private void Snapshot()
    {
        _undo.Push(CurrentState());
        _redo.Clear();
        _dirty = true;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void Restore(string json)
    {
        if (json.StartsWith("drw:", StringComparison.Ordinal))
        {
            RestoreDrawing(json[4..]);
            return;
        }

        if (json.StartsWith("asm:", StringComparison.Ordinal))
        {
            AssemblyDoc?.RestoreFrom(json[4..]);
            Selection.Clear();
            Hover = null;
            RebuildAndRefresh();
            return;
        }

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
        _redo.Push(CurrentState());
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

        _undo.Push(CurrentState());
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
        ComponentPick { IsEdge: false } c => new SelectionItem(SelectionKind.Face, c.Body.FaceRef(c.Index), Label: $"{c.Component.Name}: face <{c.Body.Face(c.Index).Surface.ToString().ToLowerInvariant()}>", ComponentId: c.Component.Id),
        ComponentPick c => new SelectionItem(SelectionKind.Edge, c.Body.EdgeRef(c.Index), Label: $"{c.Component.Name}: edge <{c.Body.Edge(c.Index).Curve.ToString().ToLowerInvariant()}>", ComponentId: c.Component.Id),
        _ => null,
    };

    /// <summary>The viewport hovered a pick id.</summary>
    public void OnHover(int id)
    {
        var item = ToSelection(Picks.Target(id));
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
        var item = ToSelection(Picks.Target(id));
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
        RollBackFor(feature);
        Editor = FeatureEditor.Create(Document, feature, isNew: true, Selection, RebuildAndRefresh);
        Editor.ActiveSelectionChanged += (_, _) => LoadActiveField();
        LoadActiveField();
        HintText = Editor.ActiveSelection?.Prompt ?? "Set the parameters, then click OK.";
        RebuildAndRefresh();
    }

    private int? _rollbackBeforeEdit;
    private bool _rolledBackForEdit;

    /// <summary>For features that consume bodies, shows the model as it was before them while editing.</summary>
    private void RollBackFor(Feature feature)
    {
        _rolledBackForEdit = FeatureEditor.EditRolledBack(feature);
        if (_rolledBackForEdit)
        {
            _rollbackBeforeEdit = Document.RollbackIndex;
            Document.RollbackIndex = Document.Features.IndexOf(feature);
        }
    }

    private void RestoreRollback()
    {
        if (_rolledBackForEdit)
        {
            Document.RollbackIndex = _rollbackBeforeEdit;
            _rolledBackForEdit = false;
        }
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
        RollBackFor(feature);
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

    private List<SketchFeature> SelectedSketches() =>
        [.. Selection.Where(s => s.Kind == SelectionKind.Feature).Select(s => Document.Find(s.FeatureId!)).OfType<SketchFeature>()];

    private SweepFeature NewSweep(bool cut, bool surface = false)
    {
        var sketches = SelectedSketches();
        return new SweepFeature
        {
            IsCut = cut,
            IsSurface = surface,
            ProfileSketchId = sketches.Count > 0 ? sketches[0].Id : string.Empty,
            PathSketchId = sketches.Count > 1 ? sketches[1].Id : string.Empty,
        };
    }

    private LoftFeature NewLoft(bool cut) => new() { IsCut = cut, SectionSketchIds = [.. SelectedSketches().Select(s => s.Id)] };

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

        Editor.OnAccept?.Invoke();
        RestoreRollback();
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
        var onCancel = Editor.OnCancel;
        RestoreRollback();
        Editor = null;
        _measuring = false;
        onCancel?.Invoke();
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

    /// <summary>Adds the 2D geometry of a DXF file to the sketch being edited.</summary>
    [RelayCommand]
    private async Task ImportDxf()
    {
        if (Sketch is null || StorageProvider is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import DXF",
            FileTypeFilter = [new FilePickerFileType("DXF") { Patterns = ["*.dxf"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        ImportDxfFile(path);
    }

    /// <summary>Imports a DXF file into the sketch being edited (used by the command and tests).</summary>
    public void ImportDxfFile(string path)
    {
        if (Sketch is null)
        {
            return;
        }

        var editor = Sketch.Editor;
        editor.BeginChange();
        try
        {
            var r = Chamfer.Sketching.Serialization.DxfImport.Import(editor.Sketch, path);
            MessageText = r.Total == 0
                ? "No lines, arcs, circles or polylines found in the DXF file."
                : $"Imported {r.Lines} lines, {r.Arcs} arcs, {r.Circles} circles" + (r.Skipped > 0 ? $" ({r.Skipped} other entities skipped)." : ".");
        }
        catch (IOException ex)
        {
            MessageText = $"Could not read the DXF file: {ex.Message}";
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
        if (AssemblyDoc is not null && SelectedNode?.Tag is { } tag)
        {
            SnapshotAssembly();
            if (tag is Chamfer.Assembly.Component comp)
            {
                comp.Suppressed = !comp.Suppressed;
            }
            else if (tag is Chamfer.Assembly.Mate mate)
            {
                mate.Suppressed = !mate.Suppressed;
            }
            else if (tag is Chamfer.Assembly.AssemblyCut cut)
            {
                cut.Suppressed = !cut.Suppressed;
            }

            RebuildAndRefresh();
            return;
        }

        if (SelectedNode?.Feature is not { } f)
        {
            return;
        }

        Snapshot();
        Document.SetSuppressed(f, !Document.IsSuppressed(f));
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

        if (AssemblyDoc is { } asm && SelectedNode?.Tag is { } tag)
        {
            SnapshotAssembly();
            if (tag is Chamfer.Assembly.Component comp)
            {
                asm.RemoveComponent(comp);
            }
            else if (tag is Chamfer.Assembly.Mate mate)
            {
                asm.Mates.Remove(mate);
            }
            else if (tag is Chamfer.Assembly.AssemblyCut cut)
            {
                asm.Features.Remove(cut);
            }

            SelectedNode = null;
            SetSelection([]);
            RebuildAndRefresh();
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
    private void MoveUp() => MoveSelected(-1);

    [RelayCommand]
    private void MoveDown() => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (SelectedNode?.Feature is not { } f)
        {
            return;
        }

        var snapshot = Document.Save();
        if (!Document.Move(f, delta))
        {
            MessageText = $"{f.Name} can't move past a feature it depends on or that depends on it.";
            return;
        }

        _undo.Push(snapshot);
        _redo.Clear();
        _dirty = true;
        UndoCommand.NotifyCanExecuteChanged();
        RebuildAndRefresh();
        SelectedNode = Tree.FirstOrDefault(n => n.Feature == f);
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
        panel.AddInfo(new InfoField("Moments of inertia (kg·mm², about centre of mass)",
            $"Ixx {m.Ixx.ToString("0.###", inv)}\nIyy {m.Iyy.ToString("0.###", inv)}\nIzz {m.Izz.ToString("0.###", inv)}\n" +
            $"Ixy {m.Ixy.ToString("0.###", inv)}  Ixz {m.Ixz.ToString("0.###", inv)}  Iyz {m.Iyz.ToString("0.###", inv)}"));
        var box = Document.Bodies.Aggregate(Box3.Empty, (b, body) => b.Union(body.Shape.Bounds));
        if (!box.IsEmpty)
        {
            panel.AddInfo(new InfoField("Bounding box", $"{box.Size.X.ToString("0.###", inv)} × {box.Size.Y.ToString("0.###", inv)} × {box.Size.Z.ToString("0.###", inv)} mm"));
        }

        _massPanel = true;
        Editor = panel;
    }


    /// <summary>Global variables: list them and add, change or delete with "Name = expression".</summary>
    [RelayCommand]
    private void ShowEquations()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Equations");
        var inv = CultureInfo.InvariantCulture;
        panel.AddInfo(new InfoField("How to use",
            "Type Name = expression and press Enter to add or change a variable (Name = to delete it). " +
            "Use a variable anywhere a number is asked for by typing =Name, or an expression such as =Width/2, including sketch dimensions."));
        panel.AddInfo(new TextField("Name = expression", string.Empty, SetEquation));
        foreach (var v in Document.Variables)
        {
            var value = Document.VariableValues.TryGetValue(v.Name, out var x) ? x.ToString("0.####", inv) : null;
            var error = Document.VariableErrors.GetValueOrDefault(v.Name);
            panel.AddInfo(new InfoField(v.Name, value is null ? $"= {v.Expression}  ⚠ {error}" : $"= {v.Expression}  →  {value}"));
        }

        Editor = panel;
    }

    private void SetEquation(string text)
    {
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        var name = (eq < 0 ? text : text[..eq]).Trim().Trim('"');
        var expr = eq < 0 ? string.Empty : text[(eq + 1)..].Trim();
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '@' or '.') || char.IsDigit(name[0]))
        {
            MessageText = "Variable names start with a letter and use letters, digits and underscores.";
            return;
        }

        Snapshot();
        if (expr.Length == 0)
        {
            Document.Variables.RemoveAll(v => v.Name == name);
        }
        else
        {
            Document.SetVariable(name, expr);
        }

        RebuildAndRefresh();
        ShowEquations();
    }

    /// <summary>Configurations: add, switch, override variables, delete, and design tables.</summary>
    [RelayCommand]
    private void ShowConfigurations()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Configurations");
        panel.AddInfo(new InfoField("Active", $"{Document.Active.Name} (switch with the list above the feature tree)"));
        panel.AddInfo(new TextField("Add configuration", string.Empty, name =>
        {
            if (Document.Configurations.Exists(c => c.Name == name))
            {
                MessageText = $"There is already a configuration named {name}.";
                return;
            }

            Snapshot();
            Document.Configurations.Add(Document.Active.Copy(name));
            Document.ActiveConfiguration = name;
            RebuildAndRefresh();
            ShowConfigurations();
        }));
        panel.AddInfo(new TextField("Override here (Name = value)", string.Empty, text =>
        {
            var eq = text.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                MessageText = "Type a variable name, =, and its value for this configuration.";
                return;
            }

            Snapshot();
            var name = text[..eq].Trim().Trim('"');
            var expr = text[(eq + 1)..].Trim();
            if (expr.Length == 0)
            {
                Document.Active.Variables.Remove(name);
            }
            else
            {
                Document.Active.Variables[name] = expr;
            }

            RebuildAndRefresh();
            ShowConfigurations();
        }));
        foreach (var (name, expr) in Document.Active.Variables)
        {
            panel.AddInfo(new InfoField($"{name} (overridden)", $"= {expr}"));
        }

        var suppressed = Document.Active.Suppression.Where(kv => kv.Value).Select(kv => Document.Find(kv.Key)?.Name).OfType<string>().ToList();
        if (suppressed.Count > 0)
        {
            panel.AddInfo(new InfoField("Suppressed here", string.Join(", ", suppressed)));
        }

        if (Document.Configurations.Count > 1)
        {
            panel.AddInfo(new ButtonField("Delete this configuration", new RelayCommand(() =>
            {
                Snapshot();
                Document.Configurations.Remove(Document.Active);
                Document.ActiveConfiguration = Document.Configurations[0].Name;
                RebuildAndRefresh();
                ShowConfigurations();
            })));
        }

        panel.AddInfo(new ButtonField("Export design table", ExportDesignTableCommand));
        panel.AddInfo(new ButtonField("Import design table", ImportDesignTableCommand));
        Editor = panel;
    }

    [RelayCommand]
    private async Task ExportDesignTable()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Design Table",
            DefaultExtension = "csv",
            SuggestedFileName = "DesignTable",
            FileTypeChoices = [CsvFileType],
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await File.WriteAllTextAsync(path, DesignTable.Export(Document));
        }
    }

    [RelayCommand]
    private async Task ImportDesignTable()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import Design Table", FileTypeFilter = [CsvFileType] });
        if (files is not [var file] || file.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            Snapshot();
            DesignTable.Import(Document, await File.ReadAllTextAsync(path));
            RebuildAndRefresh();
            ShowConfigurations();
        }
        catch (FormatException ex)
        {
            MessageText = $"Couldn't read the design table: {ex.Message}";
        }
    }

    /// <summary>
    /// Links (or unlinks, when <paramref name="expression"/> is null) a dimension of the sketch being
    /// edited to an equation, and returns the value it evaluates to.
    /// </summary>
    public double? LinkDimension(int constraintId, string? expression)
    {
        if (EditingSketch is not { } sf)
        {
            return null;
        }

        if (expression is null)
        {
            sf.DimensionLinks?.Remove(constraintId);
            return null;
        }

        if (!Expressions.TryEvaluate(expression, n => Document.VariableValues.TryGetValue(n, out var v) ? v : null, out var value))
        {
            return null;
        }

        (sf.DimensionLinks ??= [])[constraintId] = expression;
        return value;
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
        AssemblyDoc = null;
        DrawingDoc = null;
        _drawingRenderer = null;
        _drawingSource = null;
        SheetGraphics = null;
        _returnTo = null;
        Exploded = false;
        FilePath = null;
        _undo.Clear();
        _redo.Clear();
        _dirty = false;
        DiscardRecovery();
        _lastSketch = null;
        SetSelection([]);
        RebuildAndRefresh();
        RaiseModeChanged();
        ViewAction?.Invoke(StandardView.Trimetric);
    }

    /// <summary>Opens a part (or a standalone sketch, placed on the Front plane of a new part).</summary>
    public void LoadFile(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.EndsWith("." + Chamfer.Assembly.AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            LoadAssembly(path, text);
            return;
        }

        if (path.EndsWith("." + Chamfer.Drawing.DrawingDocument.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            LoadDrawing(path, text);
            return;
        }

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
            FileTypeFilter = [PartFileType, AssemblyFileType, DrawingFileType, SketchFileType],
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

        if (DrawingDoc is not null)
        {
            try
            {
                await SaveDrawing(path is not null && path.EndsWith("." + Chamfer.Drawing.DrawingDocument.FileExtension, StringComparison.OrdinalIgnoreCase) ? path : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageText = $"Couldn't save: {ex.Message}";
            }

            return;
        }

        if (AssemblyDoc is not null)
        {
            try
            {
                await SaveAssembly(path is not null && path.EndsWith("." + Chamfer.Assembly.AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase) ? path : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageText = $"Couldn't save: {ex.Message}";
            }

            return;
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
            DiscardRecovery();
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
            FileTypeChoices = [StepFileType, IgesFileType, StlFileType, ThreeMfFileType, ObjFileType, GlbFileType, HtmlFileType],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            if (AssemblyDoc is { } asm)
            {
                Chamfer.Assembly.AssemblyTools.Export(asm, path);
                MessageText = $"Exported {Path.GetFileName(path)}.";
                return;
            }

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

        if (DrawingDoc is not null && Editor is null)
        {
            _pendingSheetClick = null;
            DrawingSelection.Clear();
            RefreshDrawing();
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
