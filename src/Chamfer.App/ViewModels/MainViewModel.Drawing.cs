using Avalonia.Platform.Storage;
using Chamfer.Document;
using Chamfer.Drawing;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Drawing mode: sheets of projected views, dimensions, notes, the title block and export.</summary>
public sealed partial class MainViewModel
{
    private static readonly FilePickerFileType DrawingFileType = new("Chamfer Drawing") { Patterns = [$"*.{DrawingDocument.FileExtension}"] };
    private static readonly FilePickerFileType PdfFileType = new("PDF") { Patterns = ["*.pdf"] };
    private static readonly FilePickerFileType SvgFileType = new("SVG") { Patterns = ["*.svg"] };
    private static readonly FilePickerFileType DxfFileType = new("DXF") { Patterns = ["*.dxf"] };

    private DrawingRenderer? _drawingRenderer;
    private (PartDocument Part, Chamfer.Assembly.AssemblyDocument? Assembly, string? Path)? _drawingSource;

    [ObservableProperty]
    public partial int SheetIndex { get; set; }

    partial void OnSheetIndexChanged(int value) => OnPropertyChanged(nameof(CurrentSheet));

    public DrawingDocument? DrawingDoc { get; private set; }

    public bool IsDrawingMode => DrawingDoc is not null;

    public Sheet? CurrentSheet => DrawingDoc is { } d && d.Sheets.Count > 0 ? d.Sheets[Math.Clamp(SheetIndex, 0, d.Sheets.Count - 1)] : null;

    /// <summary>Selected views and dimensions on the sheet.</summary>
    public HashSet<string> DrawingSelection { get; } = [];

    [ObservableProperty]
    public partial SheetGraphics? SheetGraphics { get; set; }

    public IReadOnlyList<CommandItem> DrawingCommands => _drawingCommands ??=
    [
        new("Standard 3 View", "Front, top and right views plus an isometric", new RelayCommand(AddStandardViews), "⊞"),
        new("Isometric View", "Add an isometric view", new RelayCommand(() => AddView(ViewKind.Isometric)), "◇"),
        new("Section View", "Cut through the selected view", new RelayCommand(StartSection), "⊘"),
        new("Detail View", "Magnify part of the selected view", new RelayCommand(StartDetail), "⊙"),
        new("Auto Dimension", "Overall sizes and hole diameters in the selected view", new RelayCommand(AutoDimension), "↔"),
        new("Note", "Add a text note", new RelayCommand(StartNote), "✎"),
        new("Geometric Tolerance", "Feature control frame with a leader (GD&T)", new RelayCommand(StartGeometricTolerance), "⌖"),
        new("Datum Feature", "Datum feature symbol", new RelayCommand(StartDatum), "Ⓐ"),
        new("Surface Finish", "Surface texture symbol with roughness", new RelayCommand(StartSurfaceFinish), "√"),
        new("BOM Table", "Bill of materials table (assembly drawings)", new RelayCommand(AddBomTable), "☰"),
        new("Hole Table", "Table of the holes in the selected view", new RelayCommand(StartHoleTable), "⊕"),
        new("Add Revision", "Add a row to the revision table", new RelayCommand(StartRevision), "Ⓡ"),
        new("Auto Balloon", "Number the parts in the selected view (assembly drawings)", new RelayCommand(AutoBalloon), "①"),
        new("Add Sheet", "Add another sheet", new RelayCommand(AddSheet), "⊞"),
        new("Next Sheet", "Show the next sheet", new RelayCommand(NextSheet), "→"),
        new("Hidden Lines", "Show or hide hidden lines in the selected view", new RelayCommand(ToggleHiddenLines), "┅"),
        new("Sheet", "Sheet size, scale and title block", new RelayCommand(ShowSheetProperties), "▭"),
        new("Delete", "Delete the selected views or dimensions", new RelayCommand(DeleteDrawingSelection), "✕"),
        new("Export PDF", "Save the sheet as PDF", new AsyncRelayCommand(() => ExportDrawing("pdf")), "⇩"),
        new("Export SVG", "Save the sheet as SVG", new AsyncRelayCommand(() => ExportDrawing("svg")), "⇩"),
        new("Export DXF", "Save the sheet as DXF", new AsyncRelayCommand(() => ExportDrawing("dxf")), "⇩"),
    ];

    private IReadOnlyList<CommandItem>? _drawingCommands;

    /// <summary>Starts a drawing of the current part, with standard views.</summary>
    [RelayCommand]
    private void NewDrawing()
    {
        if (IsDrawingMode)
        {
            return;
        }

        if (Sketch is not null)
        {
            ExitSketch();
        }

        CancelEditor();
        if (AssemblyDoc is { } asm)
        {
            if (asm.Components.Count == 0)
            {
                MessageText = "Insert components first; the drawing shows the current assembly.";
                return;
            }

            var asmDrawing = new DrawingDocument { ModelPath = FilePath ?? "Assembly1.chasm" };
            asmDrawing.SetAssembly(asm);
            asmDrawing.Properties["Title"] = FilePath is null ? "Assembly1" : Path.GetFileNameWithoutExtension(FilePath);
            asmDrawing.AddStandardViews(asmDrawing.Sheets[0]);
            OpenDrawing(asmDrawing, null);
            return;
        }

        if (Document.Bodies.Count == 0)
        {
            MessageText = "Open or build a part first; the drawing shows the current part.";
            return;
        }

        var drawing = new DrawingDocument { ModelPath = FilePath ?? "Part1.chprt" };
        drawing.SetModel(Document);
        drawing.Properties["Title"] = FilePath is null ? "Part1" : Path.GetFileNameWithoutExtension(FilePath);
        drawing.AddStandardViews(drawing.Sheets[0]);
        OpenDrawing(drawing, null);
    }

    private void OpenDrawing(DrawingDocument drawing, string? path)
    {
        _drawingSource = (Document, AssemblyDoc, FilePath);
        AssemblyDoc = null;
        SheetIndex = 0;
        DrawingDoc = drawing;
        _drawingRenderer = new DrawingRenderer(drawing);
        FilePath = path;
        DrawingSelection.Clear();
        _undo.Clear();
        _redo.Clear();
        RaiseDrawingMode();
        RefreshDrawing();
    }

    [RelayCommand]
    private void CloseDrawing()
    {
        if (DrawingDoc is null)
        {
            return;
        }

        CancelEditor();
        DrawingDoc = null;
        _drawingRenderer = null;
        SheetGraphics = null;
        if (_drawingSource is { } src)
        {
            Document = src.Part;
            AssemblyDoc = src.Assembly;
            FilePath = src.Path;
        }

        _drawingSource = null;
        _undo.Clear();
        _redo.Clear();
        RaiseDrawingMode();
        RebuildAndRefresh();
    }

    private void RaiseDrawingMode()
    {
        OnPropertyChanged(nameof(IsDrawingMode));
        OnPropertyChanged(nameof(CurrentSheet));
        RaiseModeChanged();
    }

    public void RefreshDrawing()
    {
        if (DrawingDoc is not { } drawing || _drawingRenderer is null || CurrentSheet is not { } sheet)
        {
            return;
        }

        try
        {
            SheetGraphics = _drawingRenderer.Render(sheet);
            MessageText = string.Empty;
        }
        catch (Exception ex) when (ex is KernelException or InvalidOperationException or IOException)
        {
            MessageText = $"Couldn't update the drawing: {ex.Message}";
        }

        var selected = DrawingSelection.Count switch
        {
            0 => "Click a view to select it; drag to move it.",
            _ => string.Join(", ", DrawingSelection.Select(id => sheet.Views.Find(v => v.Id == id)?.Name ?? (sheet.Dimensions.Any(d => d.Id == id) ? "Dimension" : id))),
        };
        HintText = selected;
        StatusText = $"{sheet.Name} · {sheet.Size} · Scale {Scales.Format(sheet.Scale)}";
        var name = FilePath is null ? "Drawing1" : Path.GetFileNameWithoutExtension(FilePath);
        Title = $"{name}{(_dirty ? " •" : string.Empty)} — Chamfer";
        RefreshDrawingTree();
    }

    private void RefreshDrawingTree()
    {
        Tree.Clear();
        if (CurrentSheet is not { } sheet)
        {
            return;
        }

        foreach (var s in DrawingDoc!.Sheets)
        {
            Tree.Add(new FeatureNode($"{s.Name} ({s.Size}, {Scales.Format(s.Scale)}){(s == sheet ? "  ◀" : string.Empty)}", "▭", tag: s));
            if (s != sheet)
            {
                continue;
            }

            foreach (var v in s.Views)
            {
                Tree.Add(new FeatureNode(v.Name, "⊞", indent: 1, tag: v));
            }
        }
    }

    private void SnapshotDrawing()
    {
        if (DrawingDoc is null)
        {
            return;
        }

        _undo.Push("drw:" + DrawingDoc.Save());
        _redo.Clear();
        _dirty = true;
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void RestoreDrawing(string json)
    {
        if (DrawingDoc is not { } current)
        {
            return;
        }

        var restored = DrawingDocument.Load(json, current.Directory);
        if (current.IsAssembly)
        {
            restored.SetAssembly(current.Assembly);
        }
        else
        {
            restored.SetModel(current.Model);
        }
        DrawingDoc = restored;
        _drawingRenderer = new DrawingRenderer(restored);
        DrawingSelection.Clear();
        RefreshDrawing();
    }

    /// <summary>The sheet was clicked: select a view or dimension (Ctrl/Shift adds).</summary>
    public void OnSheetClick(Point2 at, string? owner, bool add)
    {
        if (_pendingSheetClick is { } pending)
        {
            _pendingSheetClick = null;
            pending(at);
            return;
        }

        if (!add)
        {
            DrawingSelection.Clear();
        }

        if (owner is not null && !DrawingSelection.Remove(owner))
        {
            DrawingSelection.Add(owner);
        }

        RefreshDrawing();
    }

    private Action<Point2>? _pendingSheetClick;

    private bool _dragSnapshotTaken;

    /// <summary>A view is being dragged on the sheet.</summary>
    public void OnViewDragged(string viewId, Point2 delta, bool finished)
    {
        if (CurrentSheet?.Views.Find(v => v.Id == viewId) is not { } view)
        {
            return;
        }

        if (finished)
        {
            _dragSnapshotTaken = false;
            return;
        }

        if (!_dragSnapshotTaken)
        {
            SnapshotDrawing();
            _dragSnapshotTaken = true;
        }

        view.X += delta.X;
        view.Y += delta.Y;
        DrawingSelection.Clear();
        DrawingSelection.Add(viewId);
        RefreshDrawing();
    }

    /// <summary>Moves a new view to the free spot on the sheet nearest its parent (or the sheet centre).</summary>
    private void PlaceInFreeSpace(Sheet sheet, DrawingView view, DrawingView? near)
    {
        if (_drawingRenderer is null)
        {
            return;
        }

        var (w, h) = sheet.Dimensions2D;
        var others = sheet.Views.Where(v => v != view).Select(v =>
        {
            var (min, max) = _drawingRenderer.Geometry(sheet, v).SheetBounds;
            return (Min: min - new Point2(12, 18), Max: max + new Point2(12, 12));
        }).ToList();

        // Keep clear of the title block too.
        others.Add((new Point2(w - 200, 0), new Point2(w, 52)));
        var (vmin, vmax) = _drawingRenderer.Geometry(sheet, view).SheetBounds;
        var half = (vmax - vmin) * 0.5;
        var target = near is null ? new Point2(w / 2, h / 2) : new Point2(near.X, near.Y);
        Point2? best = null;
        var bestD = double.PositiveInfinity;
        for (var x = 15 + half.X; x <= w - 15 - half.X; x += 5)
        {
            for (var y = 15 + half.Y + 15; y <= h - 15 - half.Y; y += 5)
            {
                var lo = new Point2(x, y) - half;
                var hi = new Point2(x, y) + half;
                if (others.Any(o => lo.X < o.Max.X && o.Min.X < hi.X && lo.Y < o.Max.Y && o.Min.Y < hi.Y))
                {
                    continue;
                }

                var dist = Point2.Distance(new Point2(x, y), target);
                if (dist < bestD)
                {
                    bestD = dist;
                    best = new Point2(x, y);
                }
            }
        }

        if (best is { } p)
        {
            view.X = p.X;
            view.Y = p.Y;
        }
    }

    private DrawingView? SelectedView() =>
        CurrentSheet?.Views.LastOrDefault(v => DrawingSelection.Contains(v.Id)) ?? CurrentSheet?.Views.FirstOrDefault(v => v.Kind == ViewKind.Front);

    // ------------------------------------------------------------- commands

    private void AddStandardViews()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet)
        {
            return;
        }

        SnapshotDrawing();
        sheet.Views.Clear();
        sheet.Dimensions.Clear();
        d.AddStandardViews(sheet);
        RefreshDrawing();
    }

    private void AddView(ViewKind kind)
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet)
        {
            return;
        }

        SnapshotDrawing();
        var (w, h) = sheet.Dimensions2D;
        var view = d.Add(sheet, kind, w * 0.75, h * 0.7);
        PlaceInFreeSpace(sheet, view, null);
        DrawingSelection.Clear();
        DrawingSelection.Add(view.Id);
        RefreshDrawing();
    }

    private void StartSection()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet || SelectedView() is not { } parent || _drawingRenderer is null)
        {
            MessageText = "Select a view to cut.";
            return;
        }

        CancelEditor();
        var pg = _drawingRenderer.Geometry(sheet, parent);
        var (min, max) = (pg.Lines.SelectMany(l => l.Points).DefaultIfEmpty().Min(p => p.X), pg.Lines.SelectMany(l => l.Points).DefaultIfEmpty().Max(p => p.X));
        var (ymin, ymax) = (pg.Lines.SelectMany(l => l.Points).DefaultIfEmpty().Min(p => p.Y), pg.Lines.SelectMany(l => l.Points).DefaultIfEmpty().Max(p => p.Y));
        var vertical = false;
        var at = (ymin + ymax) / 2;
        var before = d.Save();
        DrawingView? section = null;
        void Apply()
        {
            if (section is not null)
            {
                sheet.Views.Remove(section);
            }

            var (w, _) = sheet.Dimensions2D;
            var (a, b) = vertical
                ? (new Point2(at, ymin - 5), new Point2(at, ymax + 5))
                : (new Point2(min - 5, at), new Point2(max + 5, at));
            section = d.AddSection(sheet, parent, a, b, Math.Min(w - 60, parent.X + ((max - min) * sheet.Scale) + 60), parent.Y);
            PlaceInFreeSpace(sheet, section, parent);
            RefreshDrawing();
        }

        var panel = FeatureEditor.Command("Section View", Apply,
            accept: () =>
            {
                _undo.Push("drw:" + before);
                _dirty = true;
                UndoCommand.NotifyCanExecuteChanged();
            },
            cancel: () => RestoreDrawing(before));
        panel.AddField(new BoolField("Vertical cut", false, v =>
        {
            vertical = v;
            at = v ? (min + max) / 2 : (ymin + ymax) / 2;
            Apply();
        }));
        panel.AddField(new NumberField("Cut position", "mm", () => at, v =>
        {
            at = v;
            Apply();
        }));
        panel.AddField(new InfoField("Tip", "Drag the section view to place it. The position is measured in model coordinates across the parent view."));
        Editor = panel;
        Apply();
    }

    private void StartDetail()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet || SelectedView() is not { } parent || _drawingRenderer is null)
        {
            MessageText = "Select a view to take a detail from.";
            return;
        }

        CancelEditor();
        var pg = _drawingRenderer.Geometry(sheet, parent);
        double cx = pg.Center.X, cy = pg.Center.Y, radius = 10, magnification = 2;
        var before = d.Save();
        DrawingView? detail = null;
        void Apply()
        {
            if (detail is not null)
            {
                sheet.Views.Remove(detail);
            }

            var (w, h) = sheet.Dimensions2D;
            detail = d.AddDetail(sheet, parent, new Point2(cx, cy), radius, magnification, w * 0.72, h * 0.35);
            PlaceInFreeSpace(sheet, detail, parent);
            RefreshDrawing();
        }

        var panel = FeatureEditor.Command("Detail View", Apply,
            accept: () =>
            {
                _undo.Push("drw:" + before);
                _dirty = true;
                UndoCommand.NotifyCanExecuteChanged();
            },
            cancel: () => RestoreDrawing(before));
        panel.AddField(new NumberField("Centre X", "mm", () => cx, v => { cx = v; Apply(); }));
        panel.AddField(new NumberField("Centre Y", "mm", () => cy, v => { cy = v; Apply(); }));
        panel.AddField(new NumberField("Radius", "mm", () => radius, v => { radius = v; Apply(); }, 0.1));
        panel.AddField(new NumberField("Magnification", "×", () => magnification, v => { magnification = v; Apply(); }, 0.1));
        Editor = panel;
        Apply();
    }

    /// <summary>Overall width and height plus hole diameters in the selected view, tied to model edges.</summary>
    private void AutoDimension()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet || SelectedView() is not { } view || _drawingRenderer is null)
        {
            MessageText = "Select a view to dimension.";
            return;
        }

        if (view.Kind is ViewKind.Isometric or ViewKind.Section or ViewKind.Detail)
        {
            MessageText = "Auto Dimension works on orthographic views (front, top, right...).";
            return;
        }

        SnapshotDrawing();
        var vg = _drawingRenderer.Geometry(sheet, view);
        var model = d.Model;

        // Extreme vertices along the view's X and Y, anchored to edges so they follow the model.
        var ends = new List<(DimensionAnchor Anchor, Point2 Uv)>();
        foreach (var body in model.Bodies)
        {
            for (var i = 1; i <= body.EdgeCount; i++)
            {
                var e = body.Edge(i);
                if (e.Curve != CurveKind.Line)
                {
                    continue;
                }

                ends.Add((new DimensionAnchor(body.EdgeRef(i), DimensionAnchor.Start, e.Start), vg.ToView(e.Start)));
                ends.Add((new DimensionAnchor(body.EdgeRef(i), DimensionAnchor.End, e.End), vg.ToView(e.End)));
            }
        }

        if (ends.Count >= 2)
        {
            var left = ends.MinBy(x => (x.Uv.X, x.Uv.Y));
            var right = ends.MaxBy(x => (x.Uv.X, -x.Uv.Y));
            var bottom = ends.MinBy(x => (x.Uv.Y, x.Uv.X));
            var top = ends.MaxBy(x => (x.Uv.Y, -x.Uv.X));
            d.AddDimension(sheet, view, DimensionKind.Horizontal, left.Anchor, right.Anchor, -12);
            d.AddDimension(sheet, view, DimensionKind.Vertical, bottom.Anchor, top.Anchor, 12);
        }

        // Diameters of circular edges seen face-on, one per distinct size.
        var sizes = new HashSet<double>();
        foreach (var body in model.Bodies)
        {
            for (var i = 1; i <= body.EdgeCount; i++)
            {
                var e = body.Edge(i);
                var faces = body.Shape.FacesOfEdge(i).Select(body.Face).ToList();
                var axis = faces.FirstOrDefault(f => f.Surface == SurfaceKind.Cylinder)?.AxisDirection;
                if (e.Curve == CurveKind.Circle && e.IsClosed && axis is { } ax &&
                    Math.Abs(Math.Abs(Vec3.Dot(ax.Normalized(), vg.Toward)) - 1) < 1e-6 && sizes.Add(Math.Round(e.Radius, 4)))
                {
                    d.AddDimension(sheet, view, DimensionKind.Diameter, new DimensionAnchor(body.EdgeRef(i), DimensionAnchor.Center, e.CenterOrDirection), offset: 8);
                }
            }
        }

        RefreshDrawing();
    }

    private void StartNote()
    {
        if (CurrentSheet is not { } sheet)
        {
            return;
        }

        CancelEditor();
        var panel = FeatureEditor.Info("Note");
        panel.AddInfo(new TextField("Text", string.Empty, text =>
        {
            HintText = "Click on the sheet to place the note.";
            _pendingSheetClick = at =>
            {
                SnapshotDrawing();
                sheet.Notes.Add(new Note { X = at.X, Y = at.Y, Text = text.Replace("\\n", "\n", StringComparison.Ordinal) });
                Editor = null;
                RefreshDrawing();
            };
        }));
        panel.AddInfo(new InfoField("How", "Type the note and press Enter, then click on the sheet. Use \\n for a new line."));
        Editor = panel;
    }

    private void StartGeometricTolerance()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet)
        {
            return;
        }

        CancelEditor();
        var t = new GeometricTolerance { Id = d.NextId("gdt") };
        var kinds = Enum.GetValues<GeometricCharacteristic>();
        var panel = FeatureEditor.Info("Geometric Tolerance");
        panel.AddInfo(new ChoiceField("Characteristic", [.. kinds.Select(k => k.ToString())], Array.IndexOf(kinds, t.Characteristic), i => t.Characteristic = kinds[i]));
        panel.AddInfo(new TextField("Tolerance", t.Tolerance, v => t.Tolerance = v.Trim()));
        panel.AddInfo(new BoolField("Diameter zone (Ø)", t.Diameter, v => t.Diameter = v));
        panel.AddInfo(new ChoiceField("Material condition", ["None", "MMC (M)", "LMC (L)"], 0, i => t.Condition = (MaterialCondition)i));
        panel.AddInfo(new TextField("Datums", string.Empty, v => t.Datums = [.. v.Split([' ', ',', '|'], StringSplitOptions.RemoveEmptyEntries)]));
        panel.AddInfo(new InfoField("Place", "Click the feature for the leader arrow, then click where the frame goes."));
        PlaceTwoClicks((tip, at) =>
        {
            t.LeaderX = tip.X;
            t.LeaderY = tip.Y;
            t.X = at.X;
            t.Y = at.Y;
            sheet.Tolerances.Add(t);
        });
        Editor = panel;
    }

    private void StartDatum()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet)
        {
            return;
        }

        CancelEditor();
        var used = sheet.Datums.Select(x => x.Label).ToHashSet();
        var next = Enumerable.Range('A', 26).Select(c => ((char)c).ToString()).FirstOrDefault(l => !used.Contains(l)) ?? "A";
        var datum = new DatumFeature { Id = d.NextId("datum"), Label = next };
        var panel = FeatureEditor.Info("Datum Feature");
        panel.AddInfo(new TextField("Label", datum.Label, v => datum.Label = v.Trim().ToUpperInvariant()));
        panel.AddInfo(new InfoField("Place", "Click the feature (edge or face line), then click where the letter box goes."));
        PlaceTwoClicks((anchor, at) =>
        {
            datum.AnchorX = anchor.X;
            datum.AnchorY = anchor.Y;
            datum.X = at.X;
            datum.Y = at.Y;
            sheet.Datums.Add(datum);
        });
        Editor = panel;
    }

    private void StartSurfaceFinish()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet)
        {
            return;
        }

        CancelEditor();
        var finish = new SurfaceFinish { Id = d.NextId("sf") };
        var panel = FeatureEditor.Info("Surface Finish");
        panel.AddInfo(new TextField("Roughness", finish.Roughness, v => finish.Roughness = v.Trim()));
        panel.AddInfo(new BoolField("Machining required", finish.MachiningRequired, v => finish.MachiningRequired = v));
        panel.AddInfo(new InfoField("Place", "Click on the surface line to place the symbol."));
        HintText = "Click on the surface for the surface finish symbol.";
        _pendingSheetClick = at =>
        {
            SnapshotDrawing();
            finish.X = at.X;
            finish.Y = at.Y;
            sheet.Finishes.Add(finish);
            Editor = null;
            HintText = string.Empty;
            RefreshDrawing();
        };
        Editor = panel;
    }

    /// <summary>Waits for two sheet clicks (a target, then a position) and then adds the annotation.</summary>
    private void PlaceTwoClicks(Action<Point2, Point2> place)
    {
        HintText = "Click the feature to point at.";
        _pendingSheetClick = target =>
        {
            HintText = "Click where the annotation goes.";
            _pendingSheetClick = at =>
            {
                SnapshotDrawing();
                place(target, at);
                Editor = null;
                HintText = string.Empty;
                RefreshDrawing();
            };
        };
    }

    private void StartHoleTable()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet || SelectedView() is not { } view)
        {
            MessageText = "Select a view for the hole table.";
            return;
        }

        CancelEditor();
        HintText = "Click where the hole table's top-left corner goes.";
        _pendingSheetClick = at =>
        {
            SnapshotDrawing();
            sheet.HoleTables.Add(new HoleTable { Id = d.NextId("holetable"), ViewId = view.Id, X = at.X, Y = at.Y });
            HintText = string.Empty;
            RefreshDrawing();
        };
    }

    private void StartRevision()
    {
        if (CurrentSheet is not { } sheet)
        {
            return;
        }

        CancelEditor();
        var panel = FeatureEditor.Info("Add Revision");
        var approved = string.Empty;
        panel.AddInfo(new TextField("Approved by", string.Empty, v => approved = v));
        panel.AddInfo(new TextField("Description", string.Empty, text =>
        {
            SnapshotDrawing();
            var (w, h) = sheet.Dimensions2D;
            sheet.Revisions ??= new RevisionTable { X = w - 10 - 130, Y = h - 16 };
            sheet.Revisions.Rows.Add(new Revision
            {
                Rev = sheet.Revisions.NextRev(),
                Description = text,
                Date = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Approved = approved,
            });
            DrawingDoc!.Properties["Revision"] = sheet.Revisions.Rows[^1].Rev;
            Editor = null;
            RefreshDrawing();
        }));
        panel.AddInfo(new InfoField("How", "Type what changed and press Enter. The title block's revision follows the latest row."));
        Editor = panel;
    }

    private void AddBomTable()
    {
        if (DrawingDoc is not { IsAssembly: true } || CurrentSheet is not { } sheet)
        {
            MessageText = "BOM tables are for assembly drawings.";
            return;
        }

        SnapshotDrawing();
        var (_, h) = sheet.Dimensions2D;
        sheet.Bom = sheet.Bom is null ? new BomTable { X = 15, Y = h - 15 } : null;
        RefreshDrawing();
    }

    private void AutoBalloon()
    {
        if (DrawingDoc is not { IsAssembly: true } d || CurrentSheet is not { } sheet || _drawingRenderer is null)
        {
            MessageText = "Balloons are for assembly drawings.";
            return;
        }

        var view = sheet.Views.LastOrDefault(v => DrawingSelection.Contains(v.Id)) ?? sheet.Views.FirstOrDefault(v => v.Kind == ViewKind.Isometric) ?? sheet.Views.FirstOrDefault();
        if (view is null)
        {
            return;
        }

        SnapshotDrawing();
        sheet.Balloons.RemoveAll(b => b.ViewId == view.Id);
        d.AutoBalloon(sheet, view, _drawingRenderer);
        RefreshDrawing();
    }

    private void AddSheet()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet)
        {
            return;
        }

        SnapshotDrawing();
        d.Sheets.Add(new Sheet { Name = $"Sheet{d.Sheets.Count + 1}", Size = sheet.Size, Landscape = sheet.Landscape, Scale = sheet.Scale });
        SheetIndex = d.Sheets.Count - 1;
        DrawingSelection.Clear();
        RaiseDrawingMode();
        RefreshDrawing();
    }

    private void NextSheet()
    {
        if (DrawingDoc is not { } d || d.Sheets.Count < 2)
        {
            return;
        }

        SheetIndex = (SheetIndex + 1) % d.Sheets.Count;
        DrawingSelection.Clear();
        OnPropertyChanged(nameof(CurrentSheet));
        RefreshDrawing();
    }

    private void ToggleHiddenLines()
    {
        if (SelectedView() is not { } view)
        {
            return;
        }

        SnapshotDrawing();
        view.ShowHidden = !view.ShowHidden;
        RefreshDrawing();
    }

    private void DeleteDrawingSelection()
    {
        if (CurrentSheet is not { } sheet || DrawingSelection.Count == 0)
        {
            return;
        }

        SnapshotDrawing();
        var views = sheet.Views.Where(v => DrawingSelection.Contains(v.Id)).Select(v => v.Id).ToHashSet();

        // Deleting a view also deletes views taken from it and its dimensions.
        foreach (var v in sheet.Views.Where(v => v.ParentId is not null && views.Contains(v.ParentId)).ToList())
        {
            views.Add(v.Id);
        }

        sheet.Views.RemoveAll(v => views.Contains(v.Id));
        sheet.Dimensions.RemoveAll(dim => DrawingSelection.Contains(dim.Id) || views.Contains(dim.ViewId));
        sheet.Tolerances.RemoveAll(t => DrawingSelection.Contains(t.Id));
        sheet.Datums.RemoveAll(d => DrawingSelection.Contains(d.Id));
        sheet.Finishes.RemoveAll(f => DrawingSelection.Contains(f.Id));
        sheet.HoleTables.RemoveAll(t => DrawingSelection.Contains(t.Id));
        if (DrawingSelection.Contains("revisions"))
        {
            sheet.Revisions = null;
        }
        DrawingSelection.Clear();
        RefreshDrawing();
    }

    private void ShowSheetProperties()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet)
        {
            return;
        }

        CancelEditor();
        var panel = FeatureEditor.Info("Sheet");
        var sizes = Enum.GetValues<SheetSize>();
        panel.AddInfo(new ChoiceField("Size", [.. sizes.Select(s => s.ToString())], Array.IndexOf(sizes, sheet.Size), i =>
        {
            SnapshotDrawing();
            sheet.Size = sizes[i];
            RefreshDrawing();
        }));
        panel.AddInfo(new BoolField("Landscape", sheet.Landscape, v =>
        {
            SnapshotDrawing();
            sheet.Landscape = v;
            RefreshDrawing();
        }));
        panel.AddInfo(new NumberField("Scale (sheet : model)", string.Empty, () => sheet.Scale, v =>
        {
            SnapshotDrawing();
            sheet.Scale = v;
            RefreshDrawing();
        }, 0.001));
        panel.AddInfo(new BoolField("First-angle projection (ISO)", d.FirstAngle, v =>
        {
            SnapshotDrawing();
            d.FirstAngle = v;
            RefreshDrawing();
        }));
        foreach (var key in new[] { "Title", "Number", "Revision", "Author", "Date", "Company", "Material" })
        {
            panel.AddInfo(new TextField(key, d.Properties.GetValueOrDefault(key) ?? string.Empty, v =>
            {
                SnapshotDrawing();
                d.Properties[key] = v;
                RefreshDrawing();
            }));
        }

        Editor = panel;
    }

    private async Task ExportDrawing(string format)
    {
        if (SheetGraphics is not { } g || StorageProvider is null || DrawingDoc is null || _drawingRenderer is null)
        {
            return;
        }

        var type = format switch { "pdf" => PdfFileType, "svg" => SvgFileType, _ => DxfFileType };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {format.ToUpperInvariant()}",
            DefaultExtension = format,
            SuggestedFileName = FilePath is null ? "Drawing1" : Path.GetFileNameWithoutExtension(FilePath),
            FileTypeChoices = [type],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            switch (format)
            {
                case "pdf":
                    await File.WriteAllBytesAsync(path, Exporters.Pdf([.. DrawingDoc.Sheets.Select(_drawingRenderer.Render)]));
                    break;
                case "svg":
                    await File.WriteAllTextAsync(path, Exporters.Svg(g));
                    break;
                default:
                    await File.WriteAllTextAsync(path, Exporters.Dxf(g));
                    break;
            }

            MessageText = $"Exported {Path.GetFileName(path)}.";
        }
        catch (IOException ex)
        {
            MessageText = $"Couldn't export: {ex.Message}";
        }
    }

    private async Task SaveDrawing(string? path)
    {
        var d = DrawingDoc!;
        if (path is null)
        {
            if (StorageProvider is null)
            {
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Drawing",
                DefaultExtension = DrawingDocument.FileExtension,
                SuggestedFileName = FilePath is null ? "Drawing1" : Path.GetFileNameWithoutExtension(FilePath),
                FileTypeChoices = [DrawingFileType],
            });
            path = file?.TryGetLocalPath();
            if (path is null)
            {
                return;
            }
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (Path.IsPathRooted(d.ModelPath))
        {
            d.ModelPath = Path.GetRelativePath(dir, d.ModelPath);
        }

        d.Directory = dir;
        await File.WriteAllTextAsync(path, d.Save());
        FilePath = path;
        _dirty = false;
        DiscardRecovery();
        RefreshDrawing();
    }

    private void LoadDrawing(string path, string text)
    {
        New();
        var drawing = DrawingDocument.Load(text, Path.GetDirectoryName(Path.GetFullPath(path)));
        try
        {
            _ = drawing.Bodies;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            MessageText = $"The drawing's part ({drawing.ModelPath}) couldn't be loaded: {ex.Message}";
            return;
        }

        OpenDrawing(drawing, path);
    }
}
