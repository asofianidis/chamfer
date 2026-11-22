using System.Diagnostics;
using Chamfer.Document;
using Chamfer.Geometry;

namespace Chamfer.Simulation;

/// <summary>A natural-frequency (modal) study: fixtures and how many modes to find.</summary>
public sealed class FrequencyStudy
{
    public List<TopoRef> Fixtures { get; } = [];

    public int Modes { get; set; } = 4;

    /// <summary>Voxels along the longest side of the part.</summary>
    public int Resolution { get; set; } = 30;
}

/// <summary>One natural mode: its frequency and shape (displacements normalised to a maximum of 1).</summary>
public sealed record VibrationMode(int Number, double Frequency, StudyResult Shape);

public sealed record FrequencyResult(IReadOnlyList<VibrationMode> Modes, int Iterations, TimeSpan Elapsed);

/// <summary>
/// Lowest natural frequencies by subspace iteration on K φ = ω² M φ, with the voxel stiffness
/// operator and a lumped mass matrix. Units: mm, N, tonnes, seconds.
/// </summary>
public static class FrequencySolver
{
    public static FrequencyResult Solve(PartDocument part, FrequencyStudy study, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(study);
        var timer = Stopwatch.StartNew();
        var system = VoxelSystem.Build(part, study.Fixtures, study.Resolution);
        var grid = system.Grid;
        var n = system.N;

        // Lumped mass: each voxel's mass split equally over its eight nodes. kg/m³ → t/mm³.
        var rho = part.Material.Density * 1e-12;
        var cellMass = rho * Math.Pow(grid.Size, 3);
        var mass = new double[n];
        foreach (var nodes in grid.CellNodes)
        {
            foreach (var node in nodes)
            {
                for (var k = 0; k < 3; k++)
                {
                    mass[(3 * node) + k] += cellMass / 8;
                }
            }
        }

        for (var i = 0; i < n; i++)
        {
            if (system.Fixed[i])
            {
                mass[i] = 0;
            }
        }

        var p = Math.Max(1, study.Modes);
        var free = system.Fixed.Count(f => !f);
        var q = Math.Min(Math.Min(2 * p, p + 8), free);
        p = Math.Min(p, q);

        // Starting vectors: mass-weighted, varied by coordinate so they span bending and twisting shapes.
        var rng = new Random(12345);
        var x = new double[q][];
        for (var j = 0; j < q; j++)
        {
            x[j] = new double[n];
            for (var i = 0; i < n; i++)
            {
                x[j][i] = system.Fixed[i] ? 0 : mass[i] * (j == 0 ? 1 : rng.NextDouble() - 0.5);
            }
        }

        var lambda = new double[q];
        var previous = Enumerable.Repeat(double.MaxValue, q).ToArray();
        var y = Enumerable.Range(0, q).Select(_ => new double[n]).ToArray();
        var rhs = new double[n];
        var iterations = 0;
        for (var sweep = 0; sweep < 60; sweep++)
        {
            cancel.ThrowIfCancellationRequested();

            // Inverse iteration: Y = K⁻¹ M X (warm-started from the last sweep).
            for (var j = 0; j < q; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    rhs[i] = mass[i] * x[j][i];
                }

                iterations += system.Solve(rhs, y[j], 1e-7, cancel);
            }

            // Rayleigh–Ritz on the subspace: Kr φ = λ Mr φ.
            var ky = Enumerable.Range(0, q).Select(_ => new double[n]).ToArray();
            for (var j = 0; j < q; j++)
            {
                system.Multiply(y[j], ky[j]);
            }

            var kr = new double[q, q];
            var mr = new double[q, q];
            for (var a = 0; a < q; a++)
            {
                for (var b = a; b < q; b++)
                {
                    double sk = 0, sm = 0;
                    for (var i = 0; i < n; i++)
                    {
                        sk += y[a][i] * ky[b][i];
                        sm += y[a][i] * mass[i] * y[b][i];
                    }

                    kr[a, b] = kr[b, a] = sk;
                    mr[a, b] = mr[b, a] = sm;
                }
            }

            var (values, vectors) = GeneralizedEigen(kr, mr);
            lambda = values;
            for (var j = 0; j < q; j++)
            {
                var v = new double[n];
                for (var a = 0; a < q; a++)
                {
                    var c = vectors[a, j];
                    for (var i = 0; i < n; i++)
                    {
                        v[i] += c * y[a][i];
                    }
                }

                x[j] = v;
            }

            // Warm starts for the next sweep: the new Ritz vectors scaled like K⁻¹ M x ≈ x / λ.
            for (var j = 0; j < q; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    y[j][i] = x[j][i] / Math.Max(lambda[j], 1e-30);
                }
            }

            var converged = Enumerable.Range(0, p).All(j => Math.Abs(lambda[j] - previous[j]) <= 1e-5 * Math.Abs(lambda[j]));
            Array.Copy(lambda, previous, q);
            if (converged)
            {
                break;
            }
        }

        var modes = new List<VibrationMode>();
        for (var j = 0; j < p; j++)
        {
            modes.Add(new VibrationMode(j + 1, Math.Sqrt(Math.Max(lambda[j], 0)) / (2 * Math.PI), Shape(grid, x[j], iterations, timer.Elapsed)));
        }

        return new FrequencyResult(modes, iterations, timer.Elapsed);
    }

    /// <summary>A mode shape as a study result: displacement normalised to 1, cells coloured by amplitude.</summary>
    private static StudyResult Shape(VoxelGrid grid, double[] v, int iterations, TimeSpan elapsed)
    {
        var disp = new Vec3[grid.NodeCount];
        double max = 0;
        for (var i = 0; i < grid.NodeCount; i++)
        {
            disp[i] = new Vec3(v[3 * i], v[(3 * i) + 1], v[(3 * i) + 2]);
            max = Math.Max(max, disp[i].Length);
        }

        if (max > 0)
        {
            for (var i = 0; i < disp.Length; i++)
            {
                disp[i] /= max;
            }
        }

        var amplitude = grid.CellNodes.Select(nodes => nodes.Average(nd => disp[nd].Length)).ToArray();
        return new StudyResult
        {
            Grid = grid,
            Displacements = disp,
            CellStress = amplitude,
            MaxStress = amplitude.Length > 0 ? amplitude.Max() : 0,
            MaxDisplacement = 1,
            YieldStrength = 0,
            Iterations = iterations,
            Elapsed = elapsed,
        };
    }

    /// <summary>Solves A φ = λ B φ for small dense symmetric A and positive-definite B; λ ascending, φ B-orthonormal.</summary>
    internal static (double[] Values, double[,] Vectors) GeneralizedEigen(double[,] a, double[,] b)
    {
        var q = a.GetLength(0);

        // B = L Lᵀ; C = L⁻¹ A L⁻ᵀ is symmetric with the same eigenvalues.
        var l = new double[q, q];
        for (var i = 0; i < q; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var s = b[i, j];
                for (var k = 0; k < j; k++)
                {
                    s -= l[i, k] * l[j, k];
                }

                l[i, j] = i == j ? Math.Sqrt(Math.Max(s, 1e-300)) : s / l[j, j];
            }
        }

        double[,] SolveLower(double[,] m)
        {
            // Returns L⁻¹ m.
            var r = new double[q, q];
            for (var c = 0; c < q; c++)
            {
                for (var i = 0; i < q; i++)
                {
                    var s = m[i, c];
                    for (var k = 0; k < i; k++)
                    {
                        s -= l[i, k] * r[k, c];
                    }

                    r[i, c] = s / l[i, i];
                }
            }

            return r;
        }

        static double[,] Transpose(double[,] m)
        {
            var n = m.GetLength(0);
            var t = new double[n, n];
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    t[i, j] = m[j, i];
                }
            }

            return t;
        }

        var c1 = SolveLower(a);
        var cMat = SolveLower(Transpose(c1));
        for (var i = 0; i < q; i++)
        {
            for (var j = i + 1; j < q; j++)
            {
                cMat[i, j] = cMat[j, i] = 0.5 * (cMat[i, j] + cMat[j, i]);
            }
        }

        var (values, z) = Jacobi(cMat);

        // φ = L⁻ᵀ z (back substitution).
        var phi = new double[q, q];
        for (var c = 0; c < q; c++)
        {
            for (var i = q - 1; i >= 0; i--)
            {
                var s = z[i, c];
                for (var k = i + 1; k < q; k++)
                {
                    s -= l[k, i] * phi[k, c];
                }

                phi[i, c] = s / l[i, i];
            }
        }

        var order = Enumerable.Range(0, q).OrderBy(i => values[i]).ToArray();
        var sortedValues = order.Select(i => values[i]).ToArray();
        var sortedVectors = new double[q, q];
        for (var c = 0; c < q; c++)
        {
            for (var r = 0; r < q; r++)
            {
                sortedVectors[r, c] = phi[r, order[c]];
            }
        }

        return (sortedValues, sortedVectors);
    }

    /// <summary>Cyclic Jacobi eigen-decomposition of a symmetric matrix.</summary>
    private static (double[] Values, double[,] Vectors) Jacobi(double[,] m)
    {
        var n = m.GetLength(0);
        var a = (double[,])m.Clone();
        var v = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            v[i, i] = 1;
        }

        for (var sweep = 0; sweep < 100; sweep++)
        {
            double off = 0, total = 0;
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    total += a[i, j] * a[i, j];
                    if (i != j)
                    {
                        off += a[i, j] * a[i, j];
                    }
                }
            }

            if (off <= 1e-24 * total)
            {
                break;
            }

            for (var p = 0; p < n - 1; p++)
            {
                for (var r = p + 1; r < n; r++)
                {
                    if (Math.Abs(a[p, r]) < 1e-300)
                    {
                        continue;
                    }

                    var theta = (a[r, r] - a[p, p]) / (2 * a[p, r]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt((theta * theta) + 1));
                    if (theta == 0)
                    {
                        t = 1;
                    }

                    var c = 1 / Math.Sqrt((t * t) + 1);
                    var s = t * c;
                    for (var k = 0; k < n; k++)
                    {
                        var akp = a[k, p];
                        var akr = a[k, r];
                        a[k, p] = (c * akp) - (s * akr);
                        a[k, r] = (s * akp) + (c * akr);
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var apk = a[p, k];
                        var ark = a[r, k];
                        a[p, k] = (c * apk) - (s * ark);
                        a[r, k] = (s * apk) + (c * ark);
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var vkp = v[k, p];
                        var vkr = v[k, r];
                        v[k, p] = (c * vkp) - (s * vkr);
                        v[k, r] = (s * vkp) + (c * vkr);
                    }
                }
            }
        }

        return ([.. Enumerable.Range(0, n).Select(i => a[i, i])], v);
    }
}
