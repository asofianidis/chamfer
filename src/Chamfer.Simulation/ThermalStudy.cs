using System.Diagnostics;
using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Simulation;

public enum ThermalLoadKind
{
    /// <summary>Holds the faces at a temperature (°C).</summary>
    Temperature,

    /// <summary>Puts heat into the part through the faces (W, total).</summary>
    HeatPower,

    /// <summary>Exchanges heat with surrounding fluid: coefficient h in W/(m²·K) and ambient temperature.</summary>
    Convection,
}

public sealed record ThermalLoad(TopoRef Face, ThermalLoadKind Kind, double Value, double Ambient = 20);

/// <summary>A steady-state heat conduction study.</summary>
public sealed class ThermalStudy
{
    public List<ThermalLoad> Loads { get; } = [];

    public int Resolution { get; set; } = 30;
}

public sealed class ThermalResult
{
    public required VoxelGrid Grid { get; init; }

    /// <summary>Temperature at each grid node, °C.</summary>
    public required double[] Temperatures { get; init; }

    public double MinTemperature => Temperatures.Min();

    public double MaxTemperature => Temperatures.Max();

    /// <summary>Heat leaving the part through the fixed-temperature faces (W).</summary>
    public double HeatThroughFixedFaces { get; init; }

    public int Iterations { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Trilinear temperature inside the part (NaN outside).</summary>
    public double TemperatureAt(Vec3 p)
    {
        if (Grid.CellAt(p) is not { } cell)
        {
            return double.NaN;
        }

        var (ci, cj, ck) = Grid.CellCoords[cell];
        var local = (p - Grid.Origin) / Grid.Size;
        double fx = Math.Clamp(local.X - ci, 0, 1), fy = Math.Clamp(local.Y - cj, 0, 1), fz = Math.Clamp(local.Z - ck, 0, 1);
        double t = 0;
        for (var n = 0; n < 8; n++)
        {
            var (a, b, c) = Hex8.Corners[n];
            t += Temperatures[Grid.CellNodes[cell][n]] * (a > 0 ? fx : 1 - fx) * (b > 0 ? fy : 1 - fy) * (c > 0 ? fz : 1 - fz);
        }

        return t;
    }

    /// <summary>The result as a plot: cells coloured from the coolest to the hottest, no deformation.</summary>
    public StudyResult AsPlot() => new()
    {
        Grid = Grid,
        Displacements = new Vec3[Grid.NodeCount],
        CellStress = [.. Grid.CellNodes.Select(nodes => nodes.Average(n => Temperatures[n]))],
        MaxStress = MaxTemperature,
        MinStress = MinTemperature,
        MaxDisplacement = 0,
        YieldStrength = 0,
        Iterations = Iterations,
        Elapsed = Elapsed,
    };
}

/// <summary>Steady heat conduction on the voxel grid: k ∇²T = 0 with temperature, heat power and convection on faces.</summary>
public static class ThermalSolver
{
    /// <summary>Conduction matrix of a unit-cube trilinear element (scale by k·h).</summary>
    internal static double[,] UnitConduction()
    {
        var g = 1 / Math.Sqrt(3);
        var k = new double[8, 8];
        foreach (var xi in new[] { -g, g })
        {
            foreach (var eta in new[] { -g, g })
            {
                foreach (var zeta in new[] { -g, g })
                {
                    var grad = new double[8, 3];
                    for (var n = 0; n < 8; n++)
                    {
                        var (a, b, c) = Hex8.Corners[n];
                        grad[n, 0] = a * (1 + (b * eta)) * (1 + (c * zeta)) / 8;
                        grad[n, 1] = b * (1 + (a * xi)) * (1 + (c * zeta)) / 8;
                        grad[n, 2] = c * (1 + (a * xi)) * (1 + (b * eta)) / 8;
                    }

                    // Physical gradient = 2 ∇ξ on a unit cube; dV = 1/8 per unit of ξ-volume; weights 1.
                    for (var i = 0; i < 8; i++)
                    {
                        for (var j = 0; j < 8; j++)
                        {
                            k[i, j] += 4 * ((grad[i, 0] * grad[j, 0]) + (grad[i, 1] * grad[j, 1]) + (grad[i, 2] * grad[j, 2])) / 8;
                        }
                    }
                }
            }
        }

        return k;
    }

    public static ThermalResult Solve(PartDocument part, ThermalStudy study, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(study);
        var timer = Stopwatch.StartNew();
        var bodies = part.Bodies.Where(b => !b.IsSurface).ToList();
        if (bodies.Count == 0)
        {
            throw new InvalidOperationException("There is no solid to analyse.");
        }

        if (!study.Loads.Any(l => l.Kind is ThermalLoadKind.Temperature or ThermalLoadKind.Convection))
        {
            throw new InvalidOperationException("Hold some faces at a temperature or add convection, or the temperature is undefined.");
        }

        using var compound = bodies.Count == 1 ? null : Modeling.Compound([.. bodies.Select(b => b.Shape)]);
        var grid = VoxelGrid.Build(compound ?? bodies[0].Shape, study.Resolution);
        var n = grid.NodeCount;
        var kScale = part.Material.ThermalConductivity / 1000 * grid.Size; // W/(mm·K) × h
        var ke = UnitConduction();
        var diag = new double[n];
        foreach (var nodes in grid.CellNodes)
        {
            for (var a = 0; a < 8; a++)
            {
                diag[nodes[a]] += ke[a, a] * kScale;
            }
        }

        // A boundary node's share of a face: the voxels touching it (4 inside a face, 2 on an edge, 1 at a corner).
        var touching = new int[n];
        foreach (var nodes in grid.CellNodes)
        {
            foreach (var node in nodes)
            {
                touching[node]++;
            }
        }

        double[] Shares(List<int> nodes)
        {
            var total = nodes.Sum(i => (double)Math.Min(touching[i], 4));
            return [.. nodes.Select(i => Math.Min(touching[i], 4) / total)];
        }

        var fixedT = new double?[n];
        var f = new double[n];
        var convection = new double[n];
        foreach (var load in study.Loads)
        {
            var nodes = StaticSolver.NodesOnFaces(part, [load.Face], grid).ToList();
            if (nodes.Count == 0)
            {
                continue;
            }

            var area = ResolveArea(part, load.Face);
            switch (load.Kind)
            {
                case ThermalLoadKind.Temperature:
                    foreach (var node in nodes)
                    {
                        fixedT[node] = load.Value;
                    }

                    break;
                case ThermalLoadKind.HeatPower:
                    var powerShares = Shares(nodes);
                    for (var k = 0; k < nodes.Count; k++)
                    {
                        f[nodes[k]] += load.Value * powerShares[k];
                    }

                    break;
                case ThermalLoadKind.Convection:
                    var areaShares = Shares(nodes);
                    for (var k = 0; k < nodes.Count; k++)
                    {
                        var hA = load.Value / 1e6 * area * areaShares[k]; // W/(m²K) → W/(mm²K)
                        convection[nodes[k]] += hA;
                        diag[nodes[k]] += hA;
                        f[nodes[k]] += hA * load.Ambient;
                    }

                    break;
            }
        }

        // y = K x over all nodes (conduction plus convection on the diagonal).
        void Full(double[] x, double[] y)
        {
            Array.Clear(y);
            foreach (var nodes in grid.CellNodes)
            {
                for (var a = 0; a < 8; a++)
                {
                    double s = 0;
                    for (var b = 0; b < 8; b++)
                    {
                        s += ke[a, b] * x[nodes[b]];
                    }

                    y[nodes[a]] += s * kScale;
                }
            }

            for (var i = 0; i < n; i++)
            {
                y[i] += convection[i] * x[i];
            }
        }

        // Eliminate fixed temperatures: solve K_ff T_f = f_f − K_fc T_c.
        var t = new double[n];
        for (var i = 0; i < n; i++)
        {
            t[i] = fixedT[i] ?? 0;
        }

        var kt = new double[n];
        Full(t, kt);
        var rhs = new double[n];
        for (var i = 0; i < n; i++)
        {
            rhs[i] = fixedT[i] is null ? f[i] - kt[i] : 0;
        }

        void Free(double[] x, double[] y)
        {
            Full(x, y);
            for (var i = 0; i < n; i++)
            {
                if (fixedT[i] is not null)
                {
                    y[i] = 0;
                }
            }
        }

        var u = new double[n];
        var iterations = Pcg(Free, diag, fixedT, rhs, u, cancel);
        for (var i = 0; i < n; i++)
        {
            t[i] = fixedT[i] ?? u[i];
        }

        // Reactions: K T − f at a fixed node is the heat put in there to hold its temperature;
        // negative values are heat taken out. Report what leaves through the fixed faces.
        Full(t, kt);
        double leaving = 0;
        for (var i = 0; i < n; i++)
        {
            if (fixedT[i] is not null)
            {
                leaving += Math.Max(0, -(kt[i] - f[i]));
            }
        }

        return new ThermalResult { Grid = grid, Temperatures = t, HeatThroughFixedFaces = leaving, Iterations = iterations, Elapsed = timer.Elapsed };
    }

    private static int Pcg(Action<double[], double[]> multiply, double[] diag, double?[] fixedT, double[] b, double[] x, CancellationToken cancel)
    {
        var n = b.Length;
        var bNorm = Math.Sqrt(b.Sum(v => v * v));
        if (bNorm == 0)
        {
            return 0;
        }

        var r = (double[])b.Clone();
        var z = new double[n];
        for (var i = 0; i < n; i++)
        {
            z[i] = fixedT[i] is null ? r[i] / diag[i] : 0;
        }

        var p = (double[])z.Clone();
        var q = new double[n];
        var rz = VoxelSystem.Dot(r, z);
        var it = 0;
        for (; it < 20000; it++)
        {
            cancel.ThrowIfCancellationRequested();
            multiply(p, q);
            var alpha = rz / VoxelSystem.Dot(p, q);
            for (var i = 0; i < n; i++)
            {
                x[i] += alpha * p[i];
                r[i] -= alpha * q[i];
            }

            if (Math.Sqrt(VoxelSystem.Dot(r, r)) < 1e-10 * bNorm)
            {
                break;
            }

            for (var i = 0; i < n; i++)
            {
                z[i] = fixedT[i] is null ? r[i] / diag[i] : 0;
            }

            var rzNew = VoxelSystem.Dot(r, z);
            var beta = rzNew / rz;
            rz = rzNew;
            for (var i = 0; i < n; i++)
            {
                p[i] = z[i] + (beta * p[i]);
            }
        }

        return it;
    }

    private static double ResolveArea(PartDocument part, TopoRef face)
    {
        foreach (var b in part.Bodies)
        {
            if (b.HasFace(face.Name))
            {
                var index = b.ResolveFace(face, out _);
                return b.Face(index).Area;
            }
        }

        return 0;
    }
}
