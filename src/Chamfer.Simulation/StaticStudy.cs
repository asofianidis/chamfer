using System.Diagnostics;
using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Simulation;

public enum LoadKind
{
    /// <summary>A total force in newtons, spread over the face, along <see cref="Load.Direction"/>.</summary>
    Force,

    /// <summary>A total force in newtons pushing into the face (against its outward normal).</summary>
    NormalForce,

    /// <summary>A pressure in MPa (N/mm²) pushing into the face.</summary>
    Pressure,
}

public sealed record Load(TopoRef Face, LoadKind Kind, double Magnitude, Vec3 Direction = default);

/// <summary>A linear static study: fixed faces, loads, and the mesh resolution.</summary>
public sealed class StaticStudy
{
    public List<TopoRef> Fixtures { get; } = [];

    public List<Load> Loads { get; } = [];

    /// <summary>Voxels along the part's longest side.</summary>
    public int Resolution { get; set; } = 30;
}

/// <summary>Results of a static study.</summary>
public sealed class StudyResult
{
    public required VoxelGrid Grid { get; init; }

    /// <summary>Displacement per grid node (mm), indexed like <see cref="VoxelGrid.NodeIndex"/>.</summary>
    public required Vec3[] Displacements { get; init; }

    /// <summary>Von Mises stress per active cell (MPa).</summary>
    public required double[] CellStress { get; init; }

    public double MaxStress { get; init; }

    public double MaxDisplacement { get; init; }

    public double YieldStrength { get; init; }

    public double FactorOfSafety => MaxStress > 0 ? YieldStrength / MaxStress : double.PositiveInfinity;

    public int Iterations { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Von Mises stress at a point (from the cell containing it, or the nearest active one).</summary>
    public double StressAt(Vec3 p) => Grid.CellAt(p) is { } c ? CellStress[c] : 0;

    /// <summary>Displacement at a point, trilinear within its cell.</summary>
    public Vec3 DisplacementAt(Vec3 p)
    {
        if (Grid.CellAt(p) is not { } cell)
        {
            return Vec3.Zero;
        }

        var (ci, cj, ck) = Grid.CellCoords[cell];
        var local = (p - Grid.Origin) / Grid.Size;
        double fx = Math.Clamp(local.X - ci, 0, 1), fy = Math.Clamp(local.Y - cj, 0, 1), fz = Math.Clamp(local.Z - ck, 0, 1);
        var d = Vec3.Zero;
        for (var n = 0; n < 8; n++)
        {
            var (a, b, c) = Hex8.Corners[n];
            var w = (a > 0 ? fx : 1 - fx) * (b > 0 ? fy : 1 - fy) * (c > 0 ? fz : 1 - fz);
            d += Displacements[Grid.CellNodes[cell][n]] * w;
        }

        return d;
    }
}

/// <summary>A regular grid of cube voxels filling a solid.</summary>
public sealed class VoxelGrid
{
    public required Vec3 Origin { get; init; }
    public required double Size { get; init; }
    public required int Nx { get; init; }
    public required int Ny { get; init; }
    public required int Nz { get; init; }

    /// <summary>Grid coordinates of each active cell.</summary>
    public required List<(int I, int J, int K)> CellCoords { get; init; }

    /// <summary>Compact node numbers of each active cell's eight corners.</summary>
    public required List<int[]> CellNodes { get; init; }

    /// <summary>Position of each compact node.</summary>
    public required List<Vec3> NodePositions { get; init; }

    public required Dictionary<(int, int, int), int> CellIndex { get; init; }

    public int CellCount => CellCoords.Count;
    public int NodeCount => NodePositions.Count;

    /// <summary>The active cell containing a point, or the nearest active neighbour.</summary>
    public int? CellAt(Vec3 p)
    {
        var l = (p - Origin) / Size;
        var (i, j, k) = ((int)Math.Floor(l.X), (int)Math.Floor(l.Y), (int)Math.Floor(l.Z));
        if (CellIndex.TryGetValue((i, j, k), out var c))
        {
            return c;
        }

        int? best = null;
        var bestD = double.PositiveInfinity;
        for (var di = -1; di <= 1; di++)
        {
            for (var dj = -1; dj <= 1; dj++)
            {
                for (var dk = -1; dk <= 1; dk++)
                {
                    if (CellIndex.TryGetValue((i + di, j + dj, k + dk), out var n))
                    {
                        var center = Origin + (new Vec3(i + di + 0.5, j + dj + 0.5, k + dk + 0.5) * Size);
                        var d = Vec3.Distance(center, p);
                        if (d < bestD)
                        {
                            bestD = d;
                            best = n;
                        }
                    }
                }
            }
        }

        return best;
    }

    public static VoxelGrid Build(Shape solid, int resolution)
    {
        ArgumentNullException.ThrowIfNull(solid);
        var box = solid.Bounds;
        var size = box.Size;
        var longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        var shortest = Math.Min(size.X, Math.Min(size.Y, size.Z));
        var h = Math.Min(longest / Math.Max(4, resolution), shortest / 3);
        var nx = Math.Max(1, (int)Math.Ceiling(size.X / h));
        var ny = Math.Max(1, (int)Math.Ceiling(size.Y / h));
        var nz = Math.Max(1, (int)Math.Ceiling(size.Z / h));
        if ((long)nx * ny * nz > 400_000)
        {
            throw new InvalidOperationException("The mesh would be too large; lower the resolution.");
        }

        // Centre the grid on the part.
        var origin = box.Center - (new Vec3(nx, ny, nz) * (h / 2));
        var centers = new List<Vec3>(nx * ny * nz);
        var coords = new List<(int, int, int)>(nx * ny * nz);
        for (var k = 0; k < nz; k++)
        {
            for (var j = 0; j < ny; j++)
            {
                for (var i = 0; i < nx; i++)
                {
                    centers.Add(origin + (new Vec3(i + 0.5, j + 0.5, k + 0.5) * h));
                    coords.Add((i, j, k));
                }
            }
        }

        var inside = solid.Classify(centers);
        var cells = new List<(int, int, int)>();
        var cellIndex = new Dictionary<(int, int, int), int>();
        var nodeIndex = new Dictionary<(int, int, int), int>();
        var nodePositions = new List<Vec3>();
        var cellNodes = new List<int[]>();
        for (var c = 0; c < coords.Count; c++)
        {
            if (inside[c] == 0)
            {
                continue;
            }

            var (i, j, k) = coords[c];
            cellIndex[(i, j, k)] = cells.Count;
            cells.Add((i, j, k));
            var nodes = new int[8];
            for (var n = 0; n < 8; n++)
            {
                var (a, b, cc) = Hex8.Corners[n];
                var key = (i + (a > 0 ? 1 : 0), j + (b > 0 ? 1 : 0), k + (cc > 0 ? 1 : 0));
                if (!nodeIndex.TryGetValue(key, out var id))
                {
                    id = nodePositions.Count;
                    nodeIndex[key] = id;
                    nodePositions.Add(origin + (new Vec3(key.Item1, key.Item2, key.Item3) * h));
                }

                nodes[n] = id;
            }

            cellNodes.Add(nodes);
        }

        if (cells.Count == 0)
        {
            throw new InvalidOperationException("The part is too thin for this mesh; raise the resolution.");
        }

        return new VoxelGrid
        {
            Origin = origin,
            Size = h,
            Nx = nx,
            Ny = ny,
            Nz = nz,
            CellCoords = cells,
            CellNodes = cellNodes,
            NodePositions = nodePositions,
            CellIndex = cellIndex,
        };
    }
}

/// <summary>Runs linear static studies on parts.</summary>
public static class StaticSolver
{
    public static StudyResult Solve(PartDocument part, StaticStudy study, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(study);
        var timer = Stopwatch.StartNew();
        var bodies = part.Bodies.Where(b => !b.IsSurface).ToList();
        if (bodies.Count == 0)
        {
            throw new InvalidOperationException("There is no solid to analyse.");
        }

        if (study.Fixtures.Count == 0)
        {
            throw new InvalidOperationException("Add at least one fixture, or the part would fly away.");
        }

        using var compound = bodies.Count == 1 ? null : Modeling.Compound([.. bodies.Select(b => b.Shape)]);
        var solid = compound ?? bodies[0].Shape;
        var grid = VoxelGrid.Build(solid, study.Resolution);
        var h = grid.Size;
        var mat = part.Material;
        var ke = Hex8.Stiffness(mat.YoungModulus, mat.PoissonRatio);
        var n = grid.NodeCount * 3;

        // Boundary conditions.
        var fixedDof = new bool[n];
        foreach (var node in NodesOnFaces(part, study.Fixtures, grid))
        {
            fixedDof[3 * node] = fixedDof[(3 * node) + 1] = fixedDof[(3 * node) + 2] = true;
        }

        if (!fixedDof.Contains(true))
        {
            throw new InvalidOperationException("No mesh nodes lie on the fixed faces; raise the resolution.");
        }

        var f = new double[n];
        foreach (var load in study.Loads)
        {
            var nodes = NodesOnFaces(part, [load.Face], grid).ToList();
            if (nodes.Count == 0)
            {
                continue;
            }

            var (body, index) = Resolve(part, load.Face);
            var face = body.Face(index);
            var total = load.Kind switch
            {
                LoadKind.Force => load.Direction.Normalized() * load.Magnitude,
                LoadKind.NormalForce => face.Normal.Normalized() * -load.Magnitude,
                _ => face.Normal.Normalized() * -(load.Magnitude * face.Area),
            };
            var share = total / nodes.Count;
            foreach (var node in nodes)
            {
                f[3 * node] += share.X;
                f[(3 * node) + 1] += share.Y;
                f[(3 * node) + 2] += share.Z;
            }
        }

        for (var i = 0; i < n; i++)
        {
            if (fixedDof[i])
            {
                f[i] = 0;
            }
        }

        // Cells coloured so that cells of one colour share no nodes: scatter in parallel per colour.
        var colours = Enumerable.Range(0, 8).Select(c => grid.CellCoords
            .Select((ijk, idx) => (ijk, idx))
            .Where(x => ((x.ijk.I & 1) | ((x.ijk.J & 1) << 1) | ((x.ijk.K & 1) << 2)) == c)
            .Select(x => x.idx).ToArray()).ToArray();

        void Multiply(double[] x, double[] y)
        {
            Array.Clear(y);
            foreach (var colour in colours)
            {
                Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, colour.Length), range =>
                {
                    var ue = new double[24];
                    for (var e = range.Item1; e < range.Item2; e++)
                    {
                        var nodes = grid.CellNodes[colour[e]];
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
                                s += ke[r, c] * ue[c];
                            }

                            y[(3 * nodes[r / 3]) + (r % 3)] += s * h;
                        }
                    }
                });
            }

            for (var i = 0; i < n; i++)
            {
                if (fixedDof[i])
                {
                    y[i] = 0;
                }
            }
        }

        // Jacobi preconditioner from the assembled diagonal.
        var diag = new double[n];
        foreach (var nodes in grid.CellNodes)
        {
            for (var r = 0; r < 24; r++)
            {
                diag[(3 * nodes[r / 3]) + (r % 3)] += ke[r, r] * h;
            }
        }

        var u = new double[n];
        var res = (double[])f.Clone();
        var z = new double[n];
        for (var i = 0; i < n; i++)
        {
            z[i] = fixedDof[i] ? 0 : res[i] / diag[i];
        }

        var p = (double[])z.Clone();
        var q = new double[n];
        var rz = Dot(res, z);
        var fNorm = Math.Sqrt(Dot(f, f));
        var iterations = 0;
        if (fNorm > 0)
        {
            for (; iterations < 20000; iterations++)
            {
                cancel.ThrowIfCancellationRequested();
                Multiply(p, q);
                var alpha = rz / Dot(p, q);
                for (var i = 0; i < n; i++)
                {
                    u[i] += alpha * p[i];
                    res[i] -= alpha * q[i];
                }

                if (Math.Sqrt(Dot(res, res)) < 1e-8 * fNorm)
                {
                    break;
                }

                for (var i = 0; i < n; i++)
                {
                    z[i] = fixedDof[i] ? 0 : res[i] / diag[i];
                }

                var rzNew = Dot(res, z);
                var beta = rzNew / rz;
                rz = rzNew;
                for (var i = 0; i < n; i++)
                {
                    p[i] = z[i] + (beta * p[i]);
                }
            }
        }

        // Stress at each cell centre.
        var d = Hex8.Elasticity(mat.YoungModulus, mat.PoissonRatio);
        var b0 = Hex8.B(0, 0, 0);
        var stress = new double[grid.CellCount];
        Parallel.For(0, grid.CellCount, c =>
        {
            var nodes = grid.CellNodes[c];
            var eps = new double[6];
            for (var r = 0; r < 6; r++)
            {
                double s = 0;
                for (var a = 0; a < 24; a++)
                {
                    s += b0[r, a] * u[(3 * nodes[a / 3]) + (a % 3)];
                }

                eps[r] = s / h;
            }

            var sig = new double[6];
            for (var r = 0; r < 6; r++)
            {
                for (var k = 0; k < 6; k++)
                {
                    sig[r] += d[r, k] * eps[k];
                }
            }

            stress[c] = Math.Sqrt((0.5 * (Math.Pow(sig[0] - sig[1], 2) + Math.Pow(sig[1] - sig[2], 2) + Math.Pow(sig[2] - sig[0], 2))) +
                (3 * ((sig[3] * sig[3]) + (sig[4] * sig[4]) + (sig[5] * sig[5]))));
        });

        var disp = new Vec3[grid.NodeCount];
        for (var i = 0; i < grid.NodeCount; i++)
        {
            disp[i] = new Vec3(u[3 * i], u[(3 * i) + 1], u[(3 * i) + 2]);
        }

        return new StudyResult
        {
            Grid = grid,
            Displacements = disp,
            CellStress = stress,
            MaxStress = stress.Max(),
            MaxDisplacement = disp.Max(v => v.Length),
            YieldStrength = mat.YieldStrength,
            Iterations = iterations,
            Elapsed = timer.Elapsed,
        };
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }

    private static (Body Body, int Index) Resolve(PartDocument part, TopoRef face)
    {
        foreach (var b in part.Bodies)
        {
            if (b.HasFace(face.Name))
            {
                return (b, b.ResolveFace(face, out _));
            }
        }

        var first = part.Bodies[0];
        return (first, first.ResolveFace(face, out _));
    }

    /// <summary>Grid nodes lying on (within about one voxel of) the given faces.</summary>
    private static HashSet<int> NodesOnFaces(PartDocument part, IEnumerable<TopoRef> faces, VoxelGrid grid)
    {
        var result = new HashSet<int>();
        var tol = grid.Size * 0.87;
        foreach (var r in faces)
        {
            var (body, index) = Resolve(part, r);
            using var face = body.Shape.Sub(TopoKind.Face, index);
            var box = face.Bounds;
            for (var node = 0; node < grid.NodeCount; node++)
            {
                var p = grid.NodePositions[node];
                if (p.X < box.Min.X - tol || p.Y < box.Min.Y - tol || p.Z < box.Min.Z - tol ||
                    p.X > box.Max.X + tol || p.Y > box.Max.Y + tol || p.Z > box.Max.Z + tol)
                {
                    continue;
                }

                if (body.Shape.ProjectOnFace(index, p) is { } hit && hit.Distance <= tol)
                {
                    result.Add(node);
                }
            }
        }

        return result;
    }
}
