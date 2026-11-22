using System.Collections.Concurrent;
using System.Diagnostics;
using Chamfer.Document;
using Chamfer.Geometry;

namespace Chamfer.Simulation;

/// <summary>One buckling mode: the load factor (critical load = factor × applied load) and its shape.</summary>
public sealed record BucklingMode(int Number, double LoadFactor, StudyResult Shape);

public sealed record BucklingResult(IReadOnlyList<BucklingMode> Modes, StudyResult Static, int Iterations, TimeSpan Elapsed);

/// <summary>
/// Linear buckling: solves the static study, builds the geometric stiffness from its stresses and
/// finds the lowest load factors λ of K φ = λ (−K_G) φ by subspace iteration.
/// </summary>
public static class BucklingSolver
{
    /// <summary>∫ ∂Nₐ/∂ξᵢ ∂N_b/∂ξⱼ over the unit cube, for i, j in x, y, z (index i * 3 + j).</summary>
    private static readonly Lazy<double[][,]> GradientProducts = new(() =>
    {
        var m = new double[9][,];
        for (var k = 0; k < 9; k++)
        {
            m[k] = new double[8, 8];
        }

        var g = 1 / Math.Sqrt(3);
        foreach (var xi in new[] { -g, g })
        {
            foreach (var eta in new[] { -g, g })
            {
                foreach (var zeta in new[] { -g, g })
                {
                    // Gradients on the unit cube: 2 × d/dξ on [−1, 1]; each Gauss point weighs 1/8 of the volume.
                    var grad = new double[8, 3];
                    for (var n = 0; n < 8; n++)
                    {
                        var (a, b, c) = Hex8.Corners[n];
                        grad[n, 0] = 2 * a * (1 + (b * eta)) * (1 + (c * zeta)) / 8;
                        grad[n, 1] = 2 * b * (1 + (a * xi)) * (1 + (c * zeta)) / 8;
                        grad[n, 2] = 2 * c * (1 + (a * xi)) * (1 + (b * eta)) / 8;
                    }

                    for (var i = 0; i < 3; i++)
                    {
                        for (var j = 0; j < 3; j++)
                        {
                            for (var p = 0; p < 8; p++)
                            {
                                for (var q = 0; q < 8; q++)
                                {
                                    m[(i * 3) + j][p, q] += grad[p, i] * grad[q, j] / 8;
                                }
                            }
                        }
                    }
                }
            }
        }

        return m;
    });

    public static BucklingResult Solve(PartDocument part, StaticStudy study, int modes = 2, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(study);
        if (study.Loads.Count == 0)
        {
            throw new InvalidOperationException("Buckling needs the loads that could buckle the part.");
        }

        var timer = Stopwatch.StartNew();
        var stat = StaticSolver.Solve(part, study, cancel);
        var system = VoxelSystem.Build(part, study.Fixtures, study.Resolution);
        var grid = system.Grid;
        var n = system.N;
        var h = grid.Size;
        var products = GradientProducts.Value;

        // Per cell: G = Σ σᵢⱼ Mᵢⱼ (8×8), scaled by the side length; the same block acts on each displacement component.
        var cellG = new double[grid.CellCount][,];
        Parallel.For(0, grid.CellCount, c =>
        {
            var s = stat.CellStressTensors.AsSpan(c * 6, 6);
            double[,] sigma =
            {
                { s[0], s[3], s[5] },
                { s[3], s[1], s[4] },
                { s[5], s[4], s[2] },
            };
            var gm = new double[8, 8];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var sij = sigma[i, j];
                    if (sij == 0)
                    {
                        continue;
                    }

                    var mij = products[(i * 3) + j];
                    for (var p = 0; p < 8; p++)
                    {
                        for (var q = 0; q < 8; q++)
                        {
                            gm[p, q] += sij * mij[p, q] * h;
                        }
                    }
                }
            }

            cellG[c] = gm;
        });

        var colours = Enumerable.Range(0, 8).Select(col => grid.CellCoords
            .Select((ijk, idx) => (ijk, idx))
            .Where(x => ((x.ijk.I & 1) | ((x.ijk.J & 1) << 1) | ((x.ijk.K & 1) << 2)) == col)
            .Select(x => x.idx).ToArray()).ToArray();

        // y = −K_G x (fixed degrees of freedom zeroed).
        void NegGeometric(double[] x, double[] y)
        {
            Array.Clear(y);
            foreach (var colour in colours)
            {
                Parallel.ForEach(Partitioner.Create(0, colour.Length), range =>
                {
                    for (var e = range.Item1; e < range.Item2; e++)
                    {
                        var cell = colour[e];
                        var nodes = grid.CellNodes[cell];
                        var gm = cellG[cell];
                        for (var p = 0; p < 8; p++)
                        {
                            double sx = 0, sy = 0, sz = 0;
                            for (var q = 0; q < 8; q++)
                            {
                                var w = gm[p, q];
                                var o = 3 * nodes[q];
                                sx += w * x[o];
                                sy += w * x[o + 1];
                                sz += w * x[o + 2];
                            }

                            var t = 3 * nodes[p];
                            y[t] -= sx;
                            y[t + 1] -= sy;
                            y[t + 2] -= sz;
                        }
                    }
                });
            }

            for (var i = 0; i < n; i++)
            {
                if (system.Fixed[i])
                {
                    y[i] = 0;
                }
            }
        }

        // Subspace iteration for the largest μ of −K_G φ = μ K φ (μ = 1/λ).
        var p0 = Math.Max(1, modes);
        var q = Math.Min(p0 + 6, system.Fixed.Count(f => !f));
        var rng = new Random(4321);
        var x = Enumerable.Range(0, q).Select(j =>
        {
            var v = new double[n];
            for (var i = 0; i < n; i++)
            {
                v[i] = system.Fixed[i] ? 0 : rng.NextDouble() - 0.5;
            }

            return v;
        }).ToArray();
        var y = Enumerable.Range(0, q).Select(_ => new double[n]).ToArray();
        var rhs = new double[n];
        var mu = new double[q];
        var previous = Enumerable.Repeat(double.MaxValue, q).ToArray();
        var iterations = 0;
        for (var sweep = 0; sweep < 80; sweep++)
        {
            cancel.ThrowIfCancellationRequested();
            for (var j = 0; j < q; j++)
            {
                NegGeometric(x[j], rhs);
                iterations += system.Solve(rhs, y[j], 1e-8, cancel);
            }

            var ky = Enumerable.Range(0, q).Select(_ => new double[n]).ToArray();
            var gy = Enumerable.Range(0, q).Select(_ => new double[n]).ToArray();
            for (var j = 0; j < q; j++)
            {
                system.Multiply(y[j], ky[j]);
                NegGeometric(y[j], gy[j]);
            }

            var kr = new double[q, q];
            var gr = new double[q, q];
            for (var a = 0; a < q; a++)
            {
                for (var b = a; b < q; b++)
                {
                    kr[a, b] = kr[b, a] = VoxelSystem.Dot(y[a], ky[b]);
                    gr[a, b] = gr[b, a] = 0.5 * (VoxelSystem.Dot(y[a], gy[b]) + VoxelSystem.Dot(y[b], gy[a]));
                }
            }

            var (values, vectors) = FrequencySolver.GeneralizedEigen(gr, kr);

            // Largest μ first.
            var order = Enumerable.Range(0, q).OrderByDescending(i => values[i]).ToArray();
            for (var j = 0; j < q; j++)
            {
                var col = order[j];
                mu[j] = values[col];
                var v = new double[n];
                for (var a = 0; a < q; a++)
                {
                    var c = vectors[a, col];
                    for (var i = 0; i < n; i++)
                    {
                        v[i] += c * y[a][i];
                    }
                }

                x[j] = v;
            }

            for (var j = 0; j < q; j++)
            {
                Array.Clear(y[j]);
            }

            var converged = Enumerable.Range(0, p0).All(j => Math.Abs(mu[j] - previous[j]) <= 1e-5 * Math.Abs(mu[j]));
            Array.Copy(mu, previous, q);
            if (converged)
            {
                break;
            }
        }

        var found = new List<BucklingMode>();
        for (var j = 0; j < q && found.Count < p0; j++)
        {
            if (mu[j] > 1e-12)
            {
                found.Add(new BucklingMode(found.Count + 1, 1 / mu[j], Shape(grid, x[j], iterations, timer.Elapsed)));
            }
        }

        return new BucklingResult(found, stat, iterations, timer.Elapsed);
    }

    private static StudyResult Shape(VoxelGrid grid, double[] v, int iterations, TimeSpan elapsed)
    {
        var disp = new Vec3[grid.NodeCount];
        double max = 0;
        for (var i = 0; i < grid.NodeCount; i++)
        {
            disp[i] = new Vec3(v[3 * i], v[(3 * i) + 1], v[(3 * i) + 2]);
            max = Math.Max(max, disp[i].Length);
        }

        for (var i = 0; i < disp.Length && max > 0; i++)
        {
            disp[i] /= max;
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
}
