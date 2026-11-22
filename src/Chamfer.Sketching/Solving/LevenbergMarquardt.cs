namespace Chamfer.Sketching.Solving;

public readonly record struct SolveResult(bool Converged, int Iterations, double MaxResidual)
{
    public static readonly SolveResult Trivial = new(true, 0, 0);
}

/// <summary>
/// Damped Gauss–Newton (Levenberg–Marquardt) solver using the minimum-norm step
/// dx = -Jᵀ (J Jᵀ + λI)⁻¹ r.
/// </summary>
/// <remarks>
/// Sketches are usually under-constrained, so J has more columns than rows and the plain
/// normal equations are singular. The minimum-norm step moves the parameters as little as
/// possible while satisfying the constraints, so unconstrained geometry stays where the user
/// put it. The same formula equals the classic LM step (JᵀJ + λI)⁻¹ Jᵀ r, so it also handles
/// over-determined, least-squares systems.
///
/// Optional per-parameter weights turn this into a weighted minimum-norm step (minimize Σ wᵢ dxᵢ²),
/// so heavily weighted parameters move only when nothing else can satisfy the constraints.
/// </remarks>
internal static class LevenbergMarquardt
{
    public const double DefaultTolerance = 1e-9;

    public static SolveResult Solve(
        IReadOnlyList<Param> variables,
        IReadOnlyList<IEquationSource> sources,
        double tolerance = DefaultTolerance,
        int maxIterations = 200,
        IReadOnlyDictionary<Param, double>? weights = null)
    {
        var rows = sources.Sum(s => s.EquationCount);
        var cols = variables.Count;
        for (var i = 0; i < cols; i++)
        {
            variables[i].Index = i;
        }

        try
        {
            if (rows == 0)
            {
                return SolveResult.Trivial;
            }

            var system = new DenseSystem(rows, cols);
            var trial = new DenseSystem(rows, cols) { WithJacobian = false };
            system.Evaluate(sources);
            var error = system.SquaredError();
            if (system.MaxAbsResidual() < tolerance)
            {
                return new SolveResult(true, 0, system.MaxAbsResidual());
            }

            if (cols == 0)
            {
                return new SolveResult(false, 0, system.MaxAbsResidual());
            }

            // Weighted min-norm: dx = -W⁻¹Jᵀ (J W⁻¹ Jᵀ + λI)⁻¹ r.
            var invWeight = new double[cols];
            for (var c = 0; c < cols; c++)
            {
                invWeight[c] = weights is not null && weights.TryGetValue(variables[c], out var w) && w > 0 ? 1 / w : 1;
            }

            var a = new double[rows * rows];
            var y = new double[rows];
            var dx = new double[cols];
            var saved = new double[cols];
            double lambda = -1;

            for (var iter = 1; iter <= maxIterations; iter++)
            {
                MultiplyJWJt(system.Jacobian, invWeight, rows, cols, a);
                if (lambda < 0)
                {
                    var maxDiag = 0.0;
                    for (var i = 0; i < rows; i++)
                    {
                        maxDiag = Math.Max(maxDiag, a[i * rows + i]);
                    }

                    lambda = 1e-10 * Math.Max(maxDiag, 1);
                }

                var improved = false;
                while (!improved)
                {
                    if (lambda > 1e12)
                    {
                        return new SolveResult(false, iter, system.MaxAbsResidual());
                    }

                    if (!SolveDamped(a, rows, lambda, system.Residuals, y))
                    {
                        lambda *= 10;
                        continue;
                    }

                    // dx = -W⁻¹ Jᵀ y
                    Array.Clear(dx);
                    for (var r = 0; r < rows; r++)
                    {
                        var yr = y[r];
                        if (yr == 0)
                        {
                            continue;
                        }

                        var offset = r * cols;
                        for (var c = 0; c < cols; c++)
                        {
                            dx[c] -= system.Jacobian[offset + c] * yr;
                        }
                    }

                    for (var c = 0; c < cols; c++)
                    {
                        saved[c] = variables[c].Value;
                        variables[c].Value += dx[c] * invWeight[c];
                    }

                    trial.Evaluate(sources);
                    var trialError = trial.SquaredError();
                    if (double.IsFinite(trialError) && trialError < error)
                    {
                        improved = true;
                        var relativeGain = (error - trialError) / Math.Max(error, 1e-300);
                        error = trialError;
                        lambda = Math.Max(lambda * 0.1, 1e-15);
                        system.Evaluate(sources);
                        if (system.MaxAbsResidual() < tolerance)
                        {
                            return new SolveResult(true, iter, system.MaxAbsResidual());
                        }

                        // Least-squares problems (soft targets) stop when progress stalls.
                        if (relativeGain < 1e-12)
                        {
                            return new SolveResult(false, iter, system.MaxAbsResidual());
                        }
                    }
                    else
                    {
                        for (var c = 0; c < cols; c++)
                        {
                            variables[c].Value = saved[c];
                        }

                        lambda *= 10;
                    }
                }
            }

            return new SolveResult(false, maxIterations, system.MaxAbsResidual());
        }
        finally
        {
            foreach (var p in variables)
            {
                p.Index = -1;
            }
        }
    }

    private static void MultiplyJWJt(double[] j, double[] invWeight, int rows, int cols, double[] result)
    {
        for (var r1 = 0; r1 < rows; r1++)
        {
            var o1 = r1 * cols;
            for (var r2 = r1; r2 < rows; r2++)
            {
                var o2 = r2 * cols;
                var sum = 0.0;
                for (var c = 0; c < cols; c++)
                {
                    sum += j[o1 + c] * j[o2 + c] * invWeight[c];
                }

                result[r1 * rows + r2] = sum;
                result[r2 * rows + r1] = sum;
            }
        }
    }

    /// <summary>Solves (A + λI) y = b with a Cholesky factorization. Returns false if not positive definite.</summary>
    private static bool SolveDamped(double[] a, int n, double lambda, double[] b, double[] y)
    {
        var l = new double[n * n];
        for (var i = 0; i < n; i++)
        {
            for (var k = 0; k <= i; k++)
            {
                var sum = a[i * n + k] + (i == k ? lambda : 0);
                for (var p = 0; p < k; p++)
                {
                    sum -= l[i * n + p] * l[k * n + p];
                }

                if (i == k)
                {
                    if (sum <= 0 || !double.IsFinite(sum))
                    {
                        return false;
                    }

                    l[i * n + i] = Math.Sqrt(sum);
                }
                else
                {
                    l[i * n + k] = sum / l[k * n + k];
                }
            }
        }

        // Forward substitution: L z = b
        for (var i = 0; i < n; i++)
        {
            var sum = b[i];
            for (var p = 0; p < i; p++)
            {
                sum -= l[i * n + p] * y[p];
            }

            y[i] = sum / l[i * n + i];
        }

        // Back substitution: Lᵀ y = z
        for (var i = n - 1; i >= 0; i--)
        {
            var sum = y[i];
            for (var p = i + 1; p < n; p++)
            {
                sum -= l[p * n + i] * y[p];
            }

            y[i] = sum / l[i * n + i];
        }

        return true;
    }
}
