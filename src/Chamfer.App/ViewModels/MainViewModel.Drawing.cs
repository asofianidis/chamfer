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
    private (PartDocument Part, string? Path)? _drawingSource;

    public DrawingDocument? DrawingDoc { get; private set; }

    public bool IsDrawingMode => DrawingDoc is not null;

    public Sheet? CurrentSheet => DrawingDoc?.Sheets.FirstOrDefault();

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
        if (AssemblyDoc is not null || Document.Bodies.Count == 0)
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
        _drawingSource = (Document, FilePath);
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

        Tree.Add(new FeatureNode($"{sheet.Name} ({sheet.Size}, {Scales.Format(sheet.Scale)})", "▭"));
        foreach (var v in sheet.Views)
        {
            Tree.Add(new FeatureNode(v.Name, "⊞", indent: 1, tag: v));
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
        restored.SetModel(current.Model);
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
        RefreshDrawing();
    }

    private void LoadDrawing(string path, string text)
    {
        New();
        var drawing = DrawingDocument.Load(text, Path.GetDirectoryName(Path.GetFullPath(path)));
        try
        {
            _ = drawing.Model;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            MessageText = $"The drawing's part ({drawing.ModelPath}) couldn't be loaded: {ex.Message}";
            return;
        }

        OpenDrawing(drawing, path);
    }
}
