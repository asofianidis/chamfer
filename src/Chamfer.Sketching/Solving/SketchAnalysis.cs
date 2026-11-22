using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.Sketching.Solving;

public enum SketchStatus
{
    /// <summary>Some geometry can still move.</summary>
    UnderDefined,

    /// <summary>Every entity is completely determined.</summary>
    FullyDefined,

    /// <summary>There are redundant or conflicting constraints, or the sketch could not be solved.</summary>
    OverDefined,
}

/// <summary>
/// Degrees-of-freedom analysis of a solved sketch, from the rank of the constraint Jacobian.
/// </summary>
public sealed class SketchAnalysis
{
    private readonly HashSet<Param> _freeParams;
    private readonly HashSet<SketchConstraint> _redundant;

    internal SketchAnalysis(int dof, HashSet<Param> freeParams, HashSet<SketchConstraint> redundant, bool solveFailed)
    {
        DegreesOfFreedom = dof;
        _freeParams = freeParams;
        _redundant = redundant;
        SolveFailed = solveFailed;
    }

    /// <summary>Number of independent ways the sketch can still move.</summary>
    public int DegreesOfFreedom { get; }

    /// <summary>Constraints that add no new information (redundant) or contradict earlier ones.</summary>
    public IReadOnlyCollection<SketchConstraint> RedundantConstraints => _redundant;

    public bool SolveFailed { get; }

    public SketchStatus Status =>
        SolveFailed || _redundant.Count > 0 ? SketchStatus.OverDefined
        : DegreesOfFreedom == 0 ? SketchStatus.FullyDefined
        : SketchStatus.UnderDefined;

    public bool IsRedundant(SketchConstraint constraint) => _redundant.Contains(constraint);

    public bool IsFree(Param p) => _freeParams.Contains(p);

    /// <summary>True when none of the entity's parameters can move.</summary>
    public bool IsFullyDefined(SketchEntity entity) => !entity.Params.Any(_freeParams.Contains);

    /// <summary>
    /// Builds the analysis by orthogonalizing the Jacobian rows (modified Gram–Schmidt) in order:
    /// built-in entity equations first, then constraints in the order they were added.
    /// A row that adds nothing to the span of earlier rows marks its constraint as redundant.
    /// A parameter is free if its unit direction is not fully inside the row space.
    /// </summary>
    internal static SketchAnalysis Compute(
        IReadOnlyList<Param> parameters,
        IReadOnlyList<IEquationSource> sources,
        bool solveFailed)
    {
        var cols = parameters.Count;
        var rows = sources.Sum(s => s.EquationCount);
        for (var i = 0; i < cols; i++)
        {
            parameters[i].Index = i;
        }

        var basis = new List<double[]>();
        var redundant = new HashSet<SketchConstraint>();
        try
        {
            var system = new DenseSystem(rows, cols);
            system.Evaluate(sources);
            var row = 0;
            foreach (var source in sources)
            {
                for (var e = 0; e < source.EquationCount; e++, row++)
                {
                    var v = new double[cols];
                    Array.Copy(system.Jacobian, row * cols, v, 0, cols);
                    if (!Orthogonalize(v, basis) && source is SketchConstraint c)
                    {
                        redundant.Add(c);
                    }
                }
            }
        }
        finally
        {
            foreach (var p in parameters)
            {
                p.Index = -1;
            }
        }

        var free = new HashSet<Param>();
        for (var i = 0; i < cols; i++)
        {
            var inSpan = 0.0;
            foreach (var q in basis)
            {
                inSpan += q[i] * q[i];
            }

            if (1 - inSpan > 1e-8)
            {
                free.Add(parameters[i]);
            }
        }

        return new SketchAnalysis(cols - basis.Count, free, redundant, solveFailed);
    }

    /// <summary>
    /// Removes the components of <paramref name="v"/> along the basis. If anything independent is left,
    /// normalizes it, appends it to the basis and returns true.
    /// </summary>
    private static bool Orthogonalize(double[] v, List<double[]> basis)
    {
        var norm = Norm(v);
        if (norm < 1e-12)
        {
            return false;
        }

        Scale(v, 1 / norm);

        // Two passes of modified Gram–Schmidt keep the basis orthogonal to working precision.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var q in basis)
            {
                var dot = 0.0;
                for (var i = 0; i < v.Length; i++)
                {
                    dot += v[i] * q[i];
                }

                for (var i = 0; i < v.Length; i++)
                {
                    v[i] -= dot * q[i];
                }
            }
        }

        var rest = Norm(v);
        if (rest < 1e-7)
        {
            return false;
        }

        Scale(v, 1 / rest);
        basis.Add(v);
        return true;
    }

    private static double Norm(double[] v)
    {
        var sum = 0.0;
        foreach (var x in v)
        {
            sum += x * x;
        }

        return Math.Sqrt(sum);
    }

    private static void Scale(double[] v, double s)
    {
        for (var i = 0; i < v.Length; i++)
        {
            v[i] *= s;
        }
    }
}
