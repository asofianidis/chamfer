using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching;

/// <summary>
/// A 2D parametric sketch: geometry, the relations and dimensions between it, and the solver that
/// keeps them consistent. Coordinates are in millimetres on the sketch plane.
/// </summary>
public sealed class Sketch
{
    private readonly List<SketchEntity> _entities = [];
    private readonly List<SketchConstraint> _constraints = [];
    private readonly Dictionary<int, SketchEntity> _byId = [];
    private int _nextEntityId = 1;
    private int _nextConstraintId = 1;

    /// <summary>How much more reluctant dragged parameters are to move than the rest when projecting.</summary>
    private const double DragWeight = 1e4;

    /// <summary>The id of <see cref="Origin"/>.</summary>
    public const int OriginId = -1;

    public Sketch()
    {
        Origin = new SketchPoint(Vec2.Zero) { Id = OriginId };
        _byId[OriginId] = Origin;
    }

    /// <summary>
    /// The sketch origin at (0, 0). Constraints may refer to it, but it never moves: its coordinates are
    /// constants rather than solver unknowns. It is not part of <see cref="Entities"/> and cannot be removed.
    /// </summary>
    public SketchPoint Origin { get; }

    /// <summary>Top-level entities (lines, circles, arcs and standalone points), in creation order.</summary>
    public IReadOnlyList<SketchEntity> Entities => _entities;

    public IReadOnlyList<SketchConstraint> Constraints => _constraints;

    /// <summary>Named groups of entities (sketch blocks).</summary>
    public List<SketchBlock> Blocks { get; } = [];

    /// <summary>True if the most recent solve or drag could not satisfy every constraint.</summary>
    public bool LastSolveFailed { get; private set; }

    /// <summary>Top-level entities followed by the points they own.</summary>
    public IEnumerable<SketchEntity> AllEntities =>
        _entities.SelectMany(e => e.Points.Count == 0 ? [e] : e.Points.Cast<SketchEntity>().Prepend(e));

    /// <summary>All points: standalone points and the points owned by other entities.</summary>
    public IEnumerable<SketchPoint> AllPoints => AllEntities.OfType<SketchPoint>();

    public SketchEntity? FindEntity(int id) => _byId.GetValueOrDefault(id);

    public SketchConstraint? FindConstraint(int id) => _constraints.Find(c => c.Id == id);

    // ---------------------------------------------------------------- Entities

    public T Add<T>(T entity)
        where T : SketchEntity
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Owner is not null)
        {
            throw new ArgumentException("Owned points are added together with their owner.", nameof(entity));
        }

        if (_byId.ContainsValue(entity))
        {
            throw new ArgumentException("Entity is already in the sketch.", nameof(entity));
        }

        Register(entity, entity.Id > 0 ? entity.Id : null);
        foreach (var p in entity.Points)
        {
            Register(p, p.Id > 0 ? p.Id : null);
        }

        _entities.Add(entity);
        return entity;
    }

    public SketchPoint AddPoint(Vec2 position) => Add(new SketchPoint(position));

    public SketchLine AddLine(Vec2 start, Vec2 end, bool construction = false) =>
        Add(new SketchLine(start, end) { IsConstruction = construction });

    public SketchCircle AddCircle(Vec2 center, double radius, bool construction = false) =>
        Add(new SketchCircle(center, radius) { IsConstruction = construction });

    public SketchSpline AddSpline(IEnumerable<Vec2> through, bool construction = false) =>
        Add(new SketchSpline(through) { IsConstruction = construction });

    public SketchEllipse AddEllipse(Vec2 center, Vec2 majorEnd, double minorRadius, bool construction = false) =>
        Add(new SketchEllipse(center, majorEnd, minorRadius) { IsConstruction = construction });

    public SketchArc AddArc(Vec2 center, Vec2 start, Vec2 end, bool construction = false) =>
        Add(new SketchArc(center, start, end) { IsConstruction = construction });

    /// <summary>
    /// Removes a top-level entity (or the owner of a point), its points, and every constraint that refers to them.
    /// </summary>
    public bool Remove(SketchEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (ReferenceEquals(entity, Origin))
        {
            return false;
        }

        var root = entity.Root;
        if (!_entities.Remove(root))
        {
            return false;
        }

        var gone = new HashSet<SketchEntity>(root.Points) { root };
        _constraints.RemoveAll(c => c.References.Any(r => gone.Contains(r)));
        foreach (var b in Blocks)
        {
            b.EntityIds.Remove(root.Id);
        }

        Blocks.RemoveAll(b => b.EntityIds.Count == 0);
        foreach (var e in gone)
        {
            _byId.Remove(e.Id);
        }

        return true;
    }

    // ---------------------------------------------------------------- Constraints

    public T AddConstraint<T>(T constraint)
        where T : SketchConstraint
    {
        ArgumentNullException.ThrowIfNull(constraint);
        foreach (var r in constraint.References)
        {
            if (!_byId.TryGetValue(r.Id, out var known) || !ReferenceEquals(known, r))
            {
                throw new ArgumentException($"{r} is not part of this sketch.", nameof(constraint));
            }
        }

        constraint.Id = constraint.Id > 0 ? constraint.Id : _nextConstraintId;
        _nextConstraintId = Math.Max(_nextConstraintId, constraint.Id + 1);
        _constraints.Add(constraint);
        return constraint;
    }

    public bool RemoveConstraint(SketchConstraint constraint) => _constraints.Remove(constraint);

    /// <summary>
    /// Adds a constraint and solves. If the sketch cannot be solved with it, the constraint is removed,
    /// the geometry is restored and false is returned.
    /// </summary>
    public bool TryAddConstraint(SketchConstraint constraint)
    {
        var snapshot = TakeSnapshot();
        AddConstraint(constraint);
        if (Solve().Converged)
        {
            return true;
        }

        RemoveConstraint(constraint);
        RestoreSnapshot(snapshot);
        LastSolveFailed = false;
        return false;
    }

    // ---------------------------------------------------------------- Solving

    /// <summary>Moves geometry to satisfy all constraints. On failure, the geometry is left unchanged.</summary>
    public SolveResult Solve()
    {
        var snapshot = TakeSnapshot();
        var result = LevenbergMarquardt.Solve(AllParams(), EquationSources());
        if (!result.Converged)
        {
            RestoreSnapshot(snapshot);
        }

        LastSolveFailed = !result.Converged;
        return result;
    }

    /// <summary>Drags a point toward <paramref name="target"/>, keeping every constraint satisfied.</summary>
    public SolveResult Drag(SketchPoint point, Vec2 target)
    {
        ArgumentNullException.ThrowIfNull(point);
        return Drag([(point.XParam, target.X), (point.YParam, target.Y)]);
    }

    /// <summary>
    /// Moves the given parameters toward target values while keeping every constraint satisfied.
    /// First tries to hit the targets exactly. If that is impossible (for example, a point constrained
    /// to a circle), the dragged parameters start at the targets and the sketch is projected back onto
    /// its constraints, with the dragged parameters weighted so other geometry gives way first.
    /// If no valid configuration is found, nothing moves.
    /// </summary>
    public SolveResult Drag(IReadOnlyList<(Param Param, double Target)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var snapshot = TakeSnapshot();
        var all = AllParams();
        var sources = EquationSources();

        // 1. Hard targets: hold dragged parameters at the target and solve for the rest.
        var locked = targets.Select(t => t.Param).ToHashSet();
        foreach (var (p, target) in targets)
        {
            p.Value = target;
        }

        var result = LevenbergMarquardt.Solve([.. all.Where(p => !locked.Contains(p))], sources);
        if (result.Converged)
        {
            LastSolveFailed = false;
            return result;
        }

        // 2. Weighted projection from the targets back onto the constraints.
        RestoreSnapshot(snapshot);
        foreach (var (p, target) in targets)
        {
            p.Value = target;
        }

        var weights = locked.ToDictionary(p => p, _ => DragWeight);
        result = LevenbergMarquardt.Solve(all, sources, weights: weights);
        if (result.Converged)
        {
            LastSolveFailed = false;
            return result;
        }

        RestoreSnapshot(snapshot);
        return result;
    }

    /// <summary>Computes degrees of freedom and finds redundant constraints for the current geometry.</summary>
    public SketchAnalysis Analyze() => SketchAnalysis.Compute(AllParams(), EquationSources(), LastSolveFailed);

    /// <summary>Every parameter of the sketch, each listed once.</summary>
    public IReadOnlyList<Param> AllParams() => [.. _entities.SelectMany(e => e.Params).Distinct()];

    internal List<IEquationSource> EquationSources()
    {
        var sources = new List<IEquationSource>();
        sources.AddRange(_entities.OfType<IEquationSource>());
        sources.AddRange(_constraints);
        return sources;
    }

    public Dictionary<Param, double> TakeSnapshot() => AllParams().ToDictionary(p => p, p => p.Value);

    public static void RestoreSnapshot(Dictionary<Param, double> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var (p, v) in snapshot)
        {
            p.Value = v;
        }
    }

    private void Register(SketchEntity entity, int? id)
    {
        if (id is { } existing && _byId.ContainsKey(existing))
        {
            throw new ArgumentException($"Duplicate entity id {existing}.", nameof(id));
        }

        entity.Id = id ?? _nextEntityId;
        _nextEntityId = Math.Max(_nextEntityId, entity.Id + 1);
        _byId[entity.Id] = entity;
    }

}
