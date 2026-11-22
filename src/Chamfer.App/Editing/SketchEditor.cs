using Chamfer.App.Editing.Tools;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Serialization;
using Chamfer.Sketching.Solving;

namespace Chamfer.App.Editing;

/// <summary>
/// Editing session for one sketch: the active tool, selection, hover, snapping, undo/redo and the
/// commands the UI exposes. Independent of Avalonia so it can be tested headless.
/// </summary>
public sealed class SketchEditor
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly List<SketchEntity> _selection = [];
    private readonly HashSet<SketchConstraint> _selectedConstraints = [];
    private string? _pendingSnapshot;
    private SketchTool _tool;

    public SketchEditor()
    {
        Sketch = new Sketch();
        Analysis = Sketch.Analyze();
        _tool = new SelectTool();
        _tool.Attach(this);
    }

    public Sketch Sketch { get; private set; }

    /// <summary>Degrees-of-freedom analysis of the current geometry. Refreshed after every change.</summary>
    public SketchAnalysis Analysis { get; private set; }

    /// <summary>Size of one screen pixel in sketch units. Set by the view; used for pick and snap tolerances.</summary>
    public double PixelSize { get; set; } = 0.25;

    public double PickTolerance => 7 * PixelSize;

    public SketchEntity? Hover { get; set; }
    public DimensionConstraint? HoverDimension { get; set; }

    /// <summary>Selected entities in the order they were picked (order matters for some relations).</summary>
    public IReadOnlyList<SketchEntity> Selection => _selection;

    public IReadOnlyCollection<SketchConstraint> SelectedConstraints => _selectedConstraints;

    /// <summary>Show every relation glyph, not only those on selected or hovered entities.</summary>
    public bool ShowAllRelations { get; set; }

    /// <summary>Last informational or error message, for the status bar.</summary>
    public string? Message { get; private set; }

    public string? FilePath { get; set; }
    public bool IsDirty { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public SketchTool Tool
    {
        get => _tool;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            // Reset, not Cancel: switching tools drops the old tool's half-finished work but keeps
            // the selection (Cancel means Esc, which clears it).
            _tool.Reset();
            _tool = value;
            _tool.Attach(this);
            Message = null;
            ToolChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    /// <summary>The sketch's structure, geometry or analysis changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Only the picture changed (hover, preview, drag in progress).</summary>
    public event EventHandler? Invalidated;

    public event EventHandler? SelectionChanged;
    public event EventHandler? ToolChanged;

    /// <summary>The UI should prompt for a new value for this dimension.</summary>
    public event EventHandler<DimensionConstraint>? DimensionEditRequested;

    public void Invalidate() => Invalidated?.Invoke(this, EventArgs.Empty);

    public void SetMessage(string? message)
    {
        Message = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ Documents

    public void New()
    {
        Replace(new Sketch(), path: null);
        _undo.Clear();
        _redo.Clear();
        IsDirty = false;
        RaiseChanged();
    }

    public void Load(string json, string? path)
    {
        var sketch = SketchSerializer.Deserialize(json);
        Replace(sketch, path);
        _undo.Clear();
        _redo.Clear();
        IsDirty = false;
        RaiseChanged();
    }

    public string Save()
    {
        IsDirty = false;
        RaiseChanged();
        return SketchSerializer.Serialize(Sketch);
    }

    private void Replace(Sketch sketch, string? path)
    {
        Sketch = sketch;
        FilePath = path ?? FilePath;
        _selection.Clear();
        _selectedConstraints.Clear();
        Hover = null;
        HoverDimension = null;
        _tool.Reset();
        Analysis = Sketch.Analyze();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ Transactions

    /// <summary>Starts an undoable change. Pair with <see cref="EndChange"/> or <see cref="CancelChange"/>.</summary>
    public void BeginChange() => _pendingSnapshot ??= SketchSerializer.Serialize(Sketch);

    /// <summary>Finishes an undoable change: optionally solves, re-analyzes and records the undo step.</summary>
    public void EndChange(bool solve = true)
    {
        if (solve)
        {
            Sketch.Solve();
        }

        if (_pendingSnapshot is not null && _pendingSnapshot != SketchSerializer.Serialize(Sketch))
        {
            _undo.Push(_pendingSnapshot);
            _redo.Clear();
            IsDirty = true;
        }

        _pendingSnapshot = null;
        PruneSelection();
        RaiseChanged();
    }

    /// <summary>Abandons the change in progress and restores the sketch as it was.</summary>
    public void CancelChange()
    {
        if (_pendingSnapshot is { } snapshot)
        {
            _pendingSnapshot = null;
            Replace(SketchSerializer.Deserialize(snapshot), FilePath);
            RaiseChanged();
        }
    }

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    private void Step(Stack<string> from, Stack<string> to)
    {
        if (from.Count == 0)
        {
            return;
        }

        _tool.Cancel();
        to.Push(SketchSerializer.Serialize(Sketch));
        Replace(SketchSerializer.Deserialize(from.Pop()), FilePath);
        IsDirty = true;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Analysis = Sketch.Analyze();
        Changed?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    // ------------------------------------------------------------------ Selection

    public bool IsSelected(SketchEntity e) => _selection.Contains(e);

    public bool IsSelected(SketchConstraint c) => _selectedConstraints.Contains(c);

    public void ClearSelection()
    {
        if (_selection.Count == 0 && _selectedConstraints.Count == 0)
        {
            return;
        }

        _selection.Clear();
        _selectedConstraints.Clear();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void Select(SketchEntity entity, bool toggle = false)
    {
        // Entities in a block select together.
        IReadOnlyList<SketchEntity> group = SketchBlocks.BlockOf(Sketch, entity) is { } block && entity is not SketchPoint
            ? SketchBlocks.Entities(Sketch, block)
            : [entity];
        if (toggle && _selection.Contains(entity))
        {
            foreach (var e in group.Append(entity))
            {
                _selection.Remove(e);
            }
        }
        else
        {
            foreach (var e in group.Where(e => !_selection.Contains(e)))
            {
                _selection.Add(e);
            }
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void Select(SketchConstraint constraint, bool toggle = false)
    {
        if (toggle && _selectedConstraints.Remove(constraint))
        {
            // Deselected.
        }
        else
        {
            _selectedConstraints.Add(constraint);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void SelectMany(IEnumerable<SketchEntity> entities, bool add)
    {
        if (!add)
        {
            _selection.Clear();
            _selectedConstraints.Clear();
        }

        foreach (var e in entities)
        {
            if (!_selection.Contains(e))
            {
                _selection.Add(e);
            }
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>Drops selected items that no longer exist (after delete or undo).</summary>
    private void PruneSelection()
    {
        var alive = Sketch.AllEntities.Append(Sketch.Origin).ToHashSet();
        var before = _selection.Count + _selectedConstraints.Count;
        _selection.RemoveAll(e => !alive.Contains(e));
        _selectedConstraints.RemoveWhere(c => !Sketch.Constraints.Contains(c));
        if (Hover is not null && !alive.Contains(Hover))
        {
            Hover = null;
        }

        if (HoverDimension is not null && !Sketch.Constraints.Contains(HoverDimension))
        {
            HoverDimension = null;
        }

        if (before != _selection.Count + _selectedConstraints.Count)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ Hit testing & snapping

    /// <summary>The entity under <paramref name="p"/>. Points win over curves so end points stay easy to grab.</summary>
    public SketchEntity? HitEntity(Vec2 p)
    {
        var tol = PickTolerance;
        SketchEntity? best = null;
        var bestDistance = double.MaxValue;
        foreach (var point in Sketch.AllPoints.Append(Sketch.Origin))
        {
            var d = point.DistanceTo(p);
            if (d < tol && d < bestDistance)
            {
                best = point;
                bestDistance = d;
            }
        }

        if (best is not null)
        {
            return best;
        }

        foreach (var e in Sketch.Entities)
        {
            if (e is SketchPoint)
            {
                continue;
            }

            var d = e.DistanceTo(p);
            if (d < tol && d < bestDistance)
            {
                best = e;
                bestDistance = d;
            }
        }

        return best;
    }

    public DimensionConstraint? HitDimension(Vec2 p)
    {
        var tol = PickTolerance * 2.5;
        return Sketch.Constraints.OfType<DimensionConstraint>()
            .Select(d => (d, dist: Vec2.Distance(DimensionGeometry.Layout(d).Label, p)))
            .Where(x => x.dist < tol)
            .OrderBy(x => x.dist)
            .Select(x => x.d)
            .FirstOrDefault();
    }

    /// <summary>Finds what the cursor should snap to: points first, then midpoints, then curves.</summary>
    public Snap FindSnap(Vec2 p, IReadOnlySet<SketchEntity>? ignore = null)
    {
        var tol = PickTolerance;
        bool Usable(SketchEntity e) => ignore is null || (!ignore.Contains(e) && !ignore.Contains(e.Root));

        var point = Sketch.AllPoints.Append(Sketch.Origin)
            .Where(Usable)
            .Select(pt => (pt, d: pt.DistanceTo(p)))
            .Where(x => x.d < tol)
            .OrderBy(x => x.d)
            .FirstOrDefault();
        if (point.pt is not null)
        {
            return new Snap(SnapKind.Point, point.pt.Position, point.pt);
        }

        var mid = Sketch.Entities.OfType<SketchLine>()
            .Where(Usable)
            .Select(l => (l, d: Vec2.Distance(l.Midpoint, p)))
            .Where(x => x.d < tol)
            .OrderBy(x => x.d)
            .FirstOrDefault();
        if (mid.l is not null)
        {
            return new Snap(SnapKind.Midpoint, mid.l.Midpoint, mid.l);
        }

        var curve = Sketch.Entities
            .Where(e => e is not SketchPoint && Usable(e))
            .Select(e => (e, d: e.DistanceTo(p)))
            .Where(x => x.d < tol)
            .OrderBy(x => x.d)
            .FirstOrDefault();
        if (curve.e is not null)
        {
            var kind = curve.e is SketchLine ? SnapKind.OnLine : SnapKind.OnCircle;
            return new Snap(kind, curve.e.ClosestPoint(p), curve.e);
        }

        // Inference lines: line up with an existing point across or down (nearest of each).
        var points = Sketch.AllPoints.Append(Sketch.Origin).Where(Usable).Select(pt => pt.Position).ToList();
        Vec2? Nearest(Func<Vec2, double> along) => points
            .Where(q => Math.Abs(along(q) - along(p)) < tol)
            .OrderBy(q => Vec2.Distance(q, p))
            .Select(q => (Vec2?)q)
            .FirstOrDefault();
        var alignX = Nearest(q => q.X);
        var alignY = Nearest(q => q.Y);
        if (alignX is not null || alignY is not null)
        {
            return Snap.None(new Vec2(alignX?.X ?? p.X, alignY?.Y ?? p.Y)) with { AlignX = alignX, AlignY = alignY };
        }

        return Snap.None(p);
    }

    // ------------------------------------------------------------------ Relations

    /// <summary>
    /// Adds relations that came from inference (snaps, horizontal/vertical) and silently drops any that
    /// turn out to be redundant, so automatic relations never over-define the sketch.
    /// </summary>
    public void AddAutoRelations(IEnumerable<SketchConstraint> relations)
    {
        var added = new List<SketchConstraint>();
        foreach (var r in relations)
        {
            added.Add(Sketch.AddConstraint(r));
        }

        if (added.Count == 0)
        {
            return;
        }

        var analysis = Sketch.Analyze();
        foreach (var r in added.Where(analysis.IsRedundant))
        {
            Sketch.RemoveConstraint(r);
        }
    }

    /// <summary>Relates newly created points to whatever the user's clicks snapped to.</summary>
    public void AddSnapRelations(IEnumerable<SketchPoint> newPoints, IEnumerable<Snap> clicks)
    {
        var points = newPoints.ToList();
        var relations = new List<SketchConstraint>();
        foreach (var snap in clicks.Where(s => s.Kind != SnapKind.None))
        {
            var match = points.FirstOrDefault(p => Vec2.Distance(p.Position, snap.Position) < 1e-9 * Math.Max(1, snap.Position.Length));
            if (match is not null && !ReferenceEquals(match, snap.Target) && snap.RelationFor(match) is { } relation)
            {
                relations.Add(relation);
            }
        }

        AddAutoRelations(relations);
    }

    public IEnumerable<ConstraintKind> AvailableRelations() =>
        RelationRules.Offered.Where(k => RelationRules.Build(k, _selection) is not null);

    /// <summary>Applies a relation to the selection. Rejects relations that conflict or add nothing.</summary>
    public bool ApplyRelation(ConstraintKind kind)
    {
        if (RelationRules.Build(kind, _selection) is not { Count: > 0 } constraints)
        {
            SetMessage($"{RelationRules.Label(kind)} doesn't apply to the current selection.");
            return false;
        }

        BeginChange();
        foreach (var c in constraints)
        {
            Sketch.AddConstraint(c);
        }

        void Reject(string message)
        {
            foreach (var c in constraints)
            {
                Sketch.RemoveConstraint(c);
            }

            _pendingSnapshot = null;
            RaiseChanged();
            SetMessage(message);
        }

        var analysis = Sketch.Analyze();
        if (constraints.Any(analysis.IsRedundant))
        {
            Reject($"{RelationRules.Label(kind)} is already implied by existing relations, so it would over-define the sketch.");
            return false;
        }

        // Solve() leaves the geometry untouched when it fails.
        if (!Sketch.Solve().Converged)
        {
            Reject($"{RelationRules.Label(kind)} conflicts with existing relations or dimensions.");
            return false;
        }

        EndChange(solve: false);
        SetMessage($"Added {RelationRules.Label(kind)}.");
        return true;
    }

    // ------------------------------------------------------------------ Dimensions

    /// <summary>
    /// Adds a dimension. If it would over-define the sketch it is added as a driven (reference) dimension.
    /// Returns true when the new dimension is driving.
    /// </summary>
    public bool AddDimension(DimensionConstraint dimension)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        BeginChange();
        Sketch.AddConstraint(dimension);
        var driving = !Sketch.Analyze().IsRedundant(dimension);
        if (!driving)
        {
            dimension.IsDriving = false;
            SetMessage("That dimension would over-define the sketch, so it was added as a driven (reference) dimension.");
        }

        EndChange(solve: false);
        if (driving)
        {
            RequestDimensionEdit(dimension);
        }

        return driving;
    }

    public void RequestDimensionEdit(DimensionConstraint dimension)
    {
        if (dimension.IsDriving)
        {
            DimensionEditRequested?.Invoke(this, dimension);
        }
    }

    /// <summary>Sets a driving dimension's value and re-solves. Reverts and returns false if that's impossible.</summary>
    public bool SetDimensionValue(DimensionConstraint dimension, double value)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        if (!double.IsFinite(value) || (!dimension.IsAngular && value < 0))
        {
            SetMessage("Enter a positive number.");
            return false;
        }

        if (dimension.IsAngular)
        {
            value = value * Math.PI / 180;
        }

        BeginChange();
        var previous = dimension.Value;
        dimension.Value = value;
        if (!Sketch.Solve().Converged)
        {
            dimension.Value = previous;
            _pendingSnapshot = null;
            RaiseChanged();
            SetMessage("The sketch can't be solved with that value.");
            return false;
        }

        EndChange(solve: false);
        SetMessage(null);
        return true;
    }

    // ------------------------------------------------------------------ Other commands

    public void DeleteSelection()
    {
        if (_selection.Count == 0 && _selectedConstraints.Count == 0)
        {
            return;
        }

        BeginChange();
        foreach (var c in _selectedConstraints)
        {
            Sketch.RemoveConstraint(c);
        }

        // Owned points (end points, centers) go with their entity; deleting one alone does nothing.
        foreach (var e in _selection.Where(e => e.Owner is null))
        {
            Sketch.Remove(e);
        }

        _selection.Clear();
        _selectedConstraints.Clear();
        EndChange(solve: true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleConstruction()
    {
        var targets = _selection.Select(e => e.Root).Where(e => e is not SketchPoint).Distinct().ToList();
        if (targets.Count == 0)
        {
            return;
        }

        BeginChange();
        var makeConstruction = !targets.All(e => e.IsConstruction);
        foreach (var e in targets)
        {
            e.IsConstruction = makeConstruction;
        }

        EndChange(solve: false);
    }

    public void ToggleDriving(DimensionConstraint dimension)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        BeginChange();
        dimension.IsDriving = !dimension.IsDriving;
        if (dimension.IsDriving && Sketch.Analyze().IsRedundant(dimension))
        {
            dimension.IsDriving = false;
            _pendingSnapshot = null;
            SetMessage("Making that dimension driving would over-define the sketch.");
            return;
        }

        EndChange(solve: true);
    }
}
