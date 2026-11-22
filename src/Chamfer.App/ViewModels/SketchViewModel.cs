using System.Collections.ObjectModel;
using Chamfer.App.Editing;
using Chamfer.App.Editing.Tools;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Operations;
using Chamfer.Sketching.Solving;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>A toolbar button that activates a sketch tool.</summary>
public sealed partial class ToolItem(string id, string label, string shortcut, string tooltip, Func<SketchTool> create) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Shortcut { get; } = shortcut;
    public string Tooltip { get; } = string.IsNullOrEmpty(shortcut) ? tooltip : $"{tooltip} ({shortcut})";
    public Func<SketchTool> Create { get; } = create;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public required IRelayCommand Activate { get; init; }
}

/// <summary>An "Add Relation" button, enabled when the relation applies to the selection.</summary>
public sealed partial class RelationItem(ConstraintKind kind) : ObservableObject
{
    public ConstraintKind Kind { get; } = kind;
    public string Label { get; } = RelationRules.Label(kind);
    public string Glyph { get; } = RelationRules.Glyph(kind);

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public required IRelayCommand Apply { get; init; }
}

/// <summary>A row in the relations and dimensions list.</summary>
public sealed class ConstraintRow(SketchConstraint constraint, bool redundant)
{
    public SketchConstraint Constraint { get; } = constraint;
    public string Glyph { get; } = constraint is DimensionConstraint ? "<->" : RelationRules.Glyph(constraint.Kind);

    public string Text { get; } = constraint is DimensionConstraint d
        ? $"{RelationRules.Label(constraint.Kind)} {DimensionGeometry.FormatValue(d)}"
        : RelationRules.Label(constraint.Kind);

    public string Targets { get; } = string.Join(", ", constraint.References.Select(Describe));
    public bool IsRedundant { get; } = redundant;

    public static string Describe(SketchEntity e) => e switch
    {
        { Id: Sketch.OriginId } => "Origin",
        SketchPoint { Owner: { } owner } p => $"{owner.Kind} {owner.Id} {PointRole(owner, p)}",
        _ => $"{e.Kind} {e.Id}",
    };

    private static string PointRole(SketchEntity owner, SketchPoint p) => owner switch
    {
        SketchLine l when ReferenceEquals(l.Start, p) => "start",
        SketchLine => "end",
        SketchArc a when ReferenceEquals(a.Center, p) => "center",
        SketchArc a when ReferenceEquals(a.Start, p) => "start",
        SketchArc => "end",
        _ => "center",
    };
}

/// <summary>Sketch-mode state: tools, relations, the constraint list and status for one sketch being edited.</summary>
public sealed partial class SketchViewModel : ViewModelBase
{
    public SketchViewModel()
        : this(new SketchEditor())
    {
    }

    public SketchViewModel(SketchEditor editor, Func<Vec2, bool>? convertAt = null)
    {
        Editor = editor;
        Tools =
        [
            MakeTool("select", "Select", "Esc", "Select, drag geometry, double-click a dimension to edit it", () => new SelectTool()),
            MakeTool("line", "Line", "L", "Draw connected lines", () => new LineTool(construction: false)),
            MakeTool("centerline", "Centerline", "", "Draw construction lines", () => new LineTool(construction: true)),
            MakeTool("rect-corner", "Corner Rect", "R", "Rectangle from two corners", () => new CornerRectangleTool()),
            MakeTool("rect-center", "Center Rect", "", "Rectangle from center and corner", () => new CenterRectangleTool()),
            MakeTool("rect-3pt", "3-Pt Rect", "", "Angled rectangle from three points", () => new ThreePointRectangleTool()),
            MakeTool("circle", "Circle", "C", "Circle from center and radius", () => new CircleTool()),
            MakeTool("ellipse", "Ellipse", "E", "Ellipse from center, major axis end and minor size", () => new EllipseTool()),
            MakeTool("spline", "Spline", "S", "Smooth curve through points: click points, double-click to finish", () => new SplineTool()),
            MakeTool("arc-3pt", "3-Pt Arc", "A", "Arc through start, end and a third point", () => new ThreePointArcTool()),
            MakeTool("arc-center", "Center Arc", "", "Arc from center, start and end", () => new CenterArcTool()),
            MakeTool("polygon", "Polygon", "G", "Regular polygon", () => new PolygonTool(() => PolygonSides)),
            MakeTool("slot", "Slot", "T", "Straight slot", () => new SlotTool()),
            MakeTool("point", "Point", "P", "Sketch point", () => new PointTool()),
            MakeTool("dimension", "Smart Dimension", "D", "Dimension lengths, distances, angles and radii", () => new DimensionTool()),
            MakeTool("trim", "Trim", "", "Trim curves back to their intersections", () => new TrimTool()),
            MakeTool("extend", "Extend", "", "Extend a line or arc to the next curve", () => new ExtendTool()),
            MakeTool("sketch-fillet", "Fillet", "", "Round a corner with the radius in Size", () => new CornerTool(chamfer: false, () => OperationValue)),
            MakeTool("sketch-chamfer", "Chamfer", "", "Bevel a corner by the distance in Size", () => new CornerTool(chamfer: true, () => OperationValue)),
        ];
        if (convertAt is not null)
        {
            Tools.Add(MakeTool("convert", "Convert Entities", "", "Project model edges into the sketch", () => new ConvertTool(convertAt)));
        }

        Relations = [.. RelationRules.Offered.Select(k => new RelationItem(k) { Apply = new RelayCommand(() => Editor.ApplyRelation(k)) })];

        Editor.Changed += (_, _) => Refresh();
        Editor.SelectionChanged += (_, _) => RefreshSelection();
        Editor.ToolChanged += (_, _) => RefreshTool();
        Refresh();
        RefreshTool();
    }

    public SketchEditor Editor { get; }

    public ObservableCollection<ToolItem> Tools { get; }
    public ObservableCollection<RelationItem> Relations { get; }
    public ObservableCollection<ConstraintRow> ConstraintRows { get; } = [];

    /// <summary>Set by the window; zooms the canvas to fit the sketch.</summary>
    public Action? ZoomToFitAction { get; set; }

    [ObservableProperty]
    public partial int PolygonSides { get; set; } = 6;

    /// <summary>Radius, distance or spacing used by fillet, chamfer, offset and linear pattern.</summary>
    [ObservableProperty]
    public partial double OperationValue { get; set; } = 5;

    /// <summary>Instance count for sketch patterns.</summary>
    [ObservableProperty]
    public partial int PatternCount { get; set; } = 4;

    /// <summary>Offset to the other side (inward for closed shapes).</summary>
    [ObservableProperty]
    public partial bool OffsetReverse { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUnderDefined { get; set; } = true;

    [ObservableProperty]
    public partial bool IsOverDefined { get; set; }

    [ObservableProperty]
    public partial string MessageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HintText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CursorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectionText { get; set; } = "Nothing selected";

    [ObservableProperty]
    public partial bool ShowAllRelations { get; set; }

    [ObservableProperty]
    public partial ConstraintRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial bool HasSelectedDimension { get; set; }

    partial void OnShowAllRelationsChanged(bool value)
    {
        Editor.ShowAllRelations = value;
        Editor.Invalidate();
    }

    partial void OnSelectedRowChanged(ConstraintRow? value)
    {
        if (value is not null && !Editor.IsSelected(value.Constraint))
        {
            Editor.ClearSelection();
            Editor.Select(value.Constraint);
        }
    }

    private ToolItem MakeTool(string id, string label, string shortcut, string tooltip, Func<SketchTool> create)
    {
        ToolItem? item = null;
        item = new ToolItem(id, label, shortcut, tooltip, create) { Activate = new RelayCommand(() => ActivateTool(item!)) };
        return item;
    }

    private void ActivateTool(ToolItem item) => Editor.Tool = item.Create();

    public void ActivateTool(string id)
    {
        if (Tools.FirstOrDefault(t => t.Id == id) is { } item)
        {
            ActivateTool(item);
        }
    }

    public void SetCursor(Vec2 world) => CursorText = $"X {world.X:0.00}   Y {world.Y:0.00} mm";

    // ------------------------------------------------------------------ Refresh

    private void Refresh()
    {
        var analysis = Editor.Analysis;
        StatusText = analysis.Status switch
        {
            SketchStatus.FullyDefined => "Fully Defined",
            SketchStatus.OverDefined => analysis.SolveFailed ? "Over Defined (can't solve)" : $"Over Defined ({analysis.RedundantConstraints.Count} redundant)",
            _ => $"Under Defined ({analysis.DegreesOfFreedom} DOF)",
        };
        IsUnderDefined = analysis.Status == SketchStatus.UnderDefined;
        IsOverDefined = analysis.Status == SketchStatus.OverDefined;
        MessageText = Editor.Message ?? string.Empty;

        ConstraintRows.Clear();
        foreach (var c in Editor.Sketch.Constraints)
        {
            ConstraintRows.Add(new ConstraintRow(c, analysis.IsRedundant(c)));
        }

        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RefreshSelection();
        RefreshTool();
    }

    private void RefreshSelection()
    {
        var available = Editor.AvailableRelations().ToHashSet();
        foreach (var r in Relations)
        {
            r.IsEnabled = available.Contains(r.Kind);
        }

        var entities = Editor.Selection;
        var constraints = Editor.SelectedConstraints;
        SelectionText = (entities.Count, constraints.Count) switch
        {
            (0, 0) => "Nothing selected",
            (1, 0) => Details(entities[0]),
            (0, 1) => constraints.First() is DimensionConstraint d
                ? $"{RelationRules.Label(d.Kind)} {DimensionGeometry.FormatValue(d)} ({(d.IsDriving ? "driving" : "driven")})"
                : RelationRules.Label(constraints.First().Kind),
            _ => $"{entities.Count} entities, {constraints.Count} relations/dimensions",
        };
        HasSelectedDimension = constraints.Count == 1 && constraints.First() is DimensionConstraint;

        if (SelectedRow is not null && !Editor.IsSelected(SelectedRow.Constraint))
        {
            SelectedRow = null;
        }

        DeleteCommand.NotifyCanExecuteChanged();
        ToggleConstructionCommand.NotifyCanExecuteChanged();
    }

    private string Details(SketchEntity e)
    {
        var state = Editor.Analysis.IsFullyDefined(e) ? "fully defined" : "under defined";
        return e switch
        {
            SketchLine l => $"Line {l.Id}: length {l.Length:0.###}, angle {l.Direction.Angle * 180 / Math.PI:0.##}°{(l.IsConstruction ? ", construction" : string.Empty)} — {state}",
            SketchCircle c => $"Circle {c.Id}: ⌀{2 * c.Radius:0.###} at {c.Center.Position} — {state}",
            SketchArc a => $"Arc {a.Id}: R{a.Radius:0.###}, sweep {a.Sweep * 180 / Math.PI:0.##}° — {state}",
            SketchPoint p => $"{ConstraintRow.Describe(p)} at {p.Position} — {state}",
            _ => e.ToString(),
        };
    }

    private void RefreshTool()
    {
        foreach (var t in Tools)
        {
            t.IsActive = t.Id == Editor.Tool.Id;
        }

        HintText = Editor.Tool.Hint;
    }

    public void RefreshHint() => HintText = Editor.Tool.Hint;

    // ------------------------------------------------------------------ Commands

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Editor.Undo();

    private bool CanUndo() => Editor.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Editor.Redo();

    private bool CanRedo() => Editor.CanRedo;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete() => Editor.DeleteSelection();

    private bool HasSelection() => Editor.Selection.Count > 0 || Editor.SelectedConstraints.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleConstruction() => Editor.ToggleConstruction();

    /// <summary>Runs an edit on the selection as one undo step, reporting argument errors.</summary>
    private void Modify(Func<IReadOnlyList<SketchEntity>, string?> edit)
    {
        var selection = Editor.Selection.Select(e => e is SketchPoint { Owner: { } o } ? o : e).Distinct().ToList();
        Editor.BeginChange();
        try
        {
            if (edit(selection) is { } problem)
            {
                Editor.CancelChange();
                Editor.SetMessage(problem);
                return;
            }

            Editor.ClearSelection();
            Editor.EndChange(solve: true);
        }
        catch (ArgumentException ex)
        {
            Editor.CancelChange();
            Editor.SetMessage(ex.Message);
        }
    }

    [RelayCommand]
    private void Offset() => Modify(sel =>
    {
        if (!sel.Any(Curves.IsCurve))
        {
            return "Select lines, arcs or circles to offset.";
        }

        SketchOperations.Offset(Editor.Sketch, sel, OperationValue, OffsetReverse);
        return null;
    });

    [RelayCommand]
    private void Mirror() => Modify(sel =>
    {
        var axis = sel.OfType<SketchLine>().LastOrDefault(l => l.IsConstruction) ?? sel.OfType<SketchLine>().LastOrDefault();
        if (axis is null || sel.Count < 2)
        {
            return "Select the entities to mirror and a line (ideally a centerline) to mirror about.";
        }

        SketchOperations.Mirror(Editor.Sketch, sel.Where(e => e != axis), axis);
        return null;
    });

    [RelayCommand]
    private void LinearPattern() => Modify(sel =>
    {
        if (sel.Count == 0)
        {
            return "Select entities to pattern along X.";
        }

        SketchOperations.LinearPattern(Editor.Sketch, sel, new Vec2(OperationValue, 0), PatternCount);
        return null;
    });

    [RelayCommand]
    private void CircularPattern() => Modify(sel =>
    {
        if (sel.Count == 0)
        {
            return "Select entities to pattern around the origin.";
        }

        SketchOperations.CircularPattern(Editor.Sketch, sel, Vec2.Zero, PatternCount);
        return null;
    });

    [RelayCommand]
    private void ToggleDriving()
    {
        if (Editor.SelectedConstraints.FirstOrDefault() is DimensionConstraint d)
        {
            Editor.ToggleDriving(d);
        }
    }

    [RelayCommand]
    private void ZoomToFit() => ZoomToFitAction?.Invoke();

    /// <summary>Escape: cancel what the tool is doing, or go back to Select.</summary>
    public void Escape()
    {
        if (!Editor.Tool.Cancel() && Editor.Tool is not SelectTool)
        {
            ActivateTool("select");
        }

        Editor.Invalidate();
        RefreshHint();
    }
}
