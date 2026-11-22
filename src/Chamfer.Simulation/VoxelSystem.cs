using System.Collections.Concurrent;
using Chamfer.Document;
using Chamfer.Kernel.Occt;

namespace Chamfer.Simulation;

/// <summary>
/// The voxel finite-element system of a part: grid, fixed degrees of freedom, the matrix-free
/// stiffness operator and a Jacobi-preconditioned conjugate-gradient solver. Shared by the
/// static and frequency studies.
/// </summary>
internal sealed class VoxelSystem
{
    private readonly double[,] _ke;
    private readonly int[][] _colours;

    private VoxelSystem(VoxelGrid grid, Material material, bool[] fixedDof)
    {
        Grid = grid;
        Material = material;
        Fixed = fixedDof;
        N = grid.NodeCount * 3;
        _ke = Hex8.Stiffness(material.YoungModulus, material.PoissonRatio);

        // Cells coloured so that cells of one colour share no nodes: scatter in parallel per colour.
        _colours = [.. Enumerable.Range(0, 8).Select(c => grid.CellCoords
            .Select((ijk, idx) => (ijk, idx))
            .Where(x => ((x.ijk.I & 1) | ((x.ijk.J & 1) << 1) | ((x.ijk.K & 1) << 2)) == c)
            .Select(x => x.idx).ToArray())];

        var h = grid.Size;
        Diagonal = new double[N];
        foreach (var nodes in grid.CellNodes)
        {
            for (var r = 0; r < 24; r++)
            {
                Diagonal[(3 * nodes[r / 3]) + (r % 3)] += _ke[r, r] * h;
            }
        }
    }

    public VoxelGrid Grid { get; }

    public Material Material { get; }

    /// <summary>Degrees of freedom held at zero by fixtures.</summary>
    public bool[] Fixed { get; }

    public int N { get; }

    public double[] Diagonal { get; }

    /// <summary>Voxelises the part's solids and applies the fixtures.</summary>
    public static VoxelSystem Build(PartDocument part, IReadOnlyCollection<TopoRef> fixtures, int resolution)
    {
        var bodies = part.Bodies.Where(b => !b.IsSurface).ToList();
        if (bodies.Count == 0)
        {
            throw new InvalidOperationException("There is no solid to analyse.");
        }

        if (fixtures.Count == 0)
        {
            throw new InvalidOperationException("Add at least one fixture, or the part would fly away.");
        }

        using var compound = bodies.Count == 1 ? null : Modeling.Compound([.. bodies.Select(b => b.Shape)]);
        var grid = VoxelGrid.Build(compound ?? bodies[0].Shape, resolution);
        var fixedDof = new bool[grid.NodeCount * 3];
        foreach (var node in StaticSolver.NodesOnFaces(part, fixtures, grid))
        {
            fixedDof[3 * node] = fixedDof[(3 * node) + 1] = fixedDof[(3 * node) + 2] = true;
        }

        if (!fixedDof.Contains(true))
        {
            throw new InvalidOperationException("No mesh nodes lie on the fixed faces; raise the resolution.");
        }

        return new VoxelSystem(grid, part.Material, fixedDof);
    }

    /// <summary>y = K x, with fixed degrees of freedom zeroed.</summary>
    public void Multiply(double[] x, double[] y)
    {
        Array.Clear(y);
        var h = Grid.Size;
        foreach (var colour in _colours)
        {
            Parallel.ForEach(Partitioner.Create(0, colour.Length), range =>
            {
                var ue = new double[24];
                for (var e = range.Item1; e < range.Item2; e++)
                {
                    var nodes = Grid.CellNodes[colour[e]];
                    for (var a = 0; a < 8; a++)
                    {
                        ue[3 * a] = x[3 * nodes[a]];
                        ue[(3 * a) + 1] = x[(3 * nodes[a]) + 1];
                        ue[(3 * a) + 2] = x[(3 * nodes[a]) + 2];
                    }

                    for (var r = 0; r < 24; r++)
                    {
                        double s = 0;
                        for (var c = 0; c < 24; c++)
                        {
                            s += _ke[r, c] * ue[c];
                        }

                        y[(3 * nodes[r / 3]) + (r % 3)] += s * h;
                    }
                }
            });
        }

        for (var i = 0; i < N; i++)
        {
            if (Fixed[i])
            {
                y[i] = 0;
            }
        }
    }

    /// <summary>
    /// Solves K u = f by preconditioned conjugate gradients, starting from <paramref name="u"/>.
    /// Returns the number of iterations.
    /// </summary>
    public int Solve(double[] f, double[] u, double tolerance, CancellationToken cancel, int maxIterations = 20000)
    {
        var fNorm = Math.Sqrt(Dot(f, f));
        if (fNorm == 0)
        {
            Array.Clear(u);
            return 0;
        }

        var res = new double[N];
        Multiply(u, res);
        for (var i = 0; i < N; i++)
        {
            res[i] = Fixed[i] ? 0 : f[i] - res[i];
        }

        var z = new double[N];
        for (var i = 0; i < N; i++)
        {
            z[i] = Fixed[i] ? 0 : res[i] / Diagonal[i];
        }

        var p = (double[])z.Clone();
        var q = new double[N];
        var rz = Dot(res, z);
        var iterations = 0;
        for (; iterations < maxIterations; iterations++)
        {
            if (Math.Sqrt(Dot(res, res)) < tolerance * fNorm)
            {
                break;
            }

            cancel.ThrowIfCancellationRequested();
            Multiply(p, q);
            var alpha = rz / Dot(p, q);
            for (var i = 0; i < N; i++)
            {
                u[i] += alpha * p[i];
                res[i] -= alpha * q[i];
            }

            for (var i = 0; i < N; i++)
            {
                z[i] = Fixed[i] ? 0 : res[i] / Diagonal[i];
            }

            var rzNew = Dot(res, z);
            var beta = rzNew / rz;
            rz = rzNew;
            for (var i = 0; i < N; i++)
            {
                p[i] = z[i] + (beta * p[i]);
            }
        }

        return iterations;
    }

    public static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }
}
