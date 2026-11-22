namespace Chamfer.Geometry;

/// <summary>Casts rays against a triangle mesh, through a bounding-volume hierarchy.</summary>
public sealed class MeshRayCaster
{
    private readonly Vec3[] _a;
    private readonly Vec3[] _e1;
    private readonly Vec3[] _e2;
    private readonly int[] _order;
    private readonly List<Node> _nodes = [];

    private struct Node
    {
        public Vec3 Min;
        public Vec3 Max;
        public int Left;
        public int Right;
        public int Start;
        public int Count;
    }

    public MeshRayCaster(TriangleMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var n = mesh.TriangleCount;
        _a = new Vec3[n];
        _e1 = new Vec3[n];
        _e2 = new Vec3[n];
        var centers = new Vec3[n];
        Vec3 P(int v) => new(mesh.Positions[3 * v], mesh.Positions[(3 * v) + 1], mesh.Positions[(3 * v) + 2]);
        for (var t = 0; t < n; t++)
        {
            var (a, b, c) = (P(mesh.Triangles[3 * t]), P(mesh.Triangles[(3 * t) + 1]), P(mesh.Triangles[(3 * t) + 2]));
            (_a[t], _e1[t], _e2[t]) = (a, b - a, c - a);
            centers[t] = (a + b + c) / 3;
        }

        _order = [.. Enumerable.Range(0, n)];
        if (n > 0)
        {
            Build(0, n, centers);
        }
    }

    private int Build(int start, int count, Vec3[] centers)
    {
        var node = new Node { Min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue), Max = new Vec3(double.MinValue, double.MinValue, double.MinValue), Left = -1, Right = -1 };
        for (var i = start; i < start + count; i++)
        {
            var t = _order[i];
            foreach (var p in new[] { _a[t], _a[t] + _e1[t], _a[t] + _e2[t] })
            {
                node.Min = new Vec3(Math.Min(node.Min.X, p.X), Math.Min(node.Min.Y, p.Y), Math.Min(node.Min.Z, p.Z));
                node.Max = new Vec3(Math.Max(node.Max.X, p.X), Math.Max(node.Max.Y, p.Y), Math.Max(node.Max.Z, p.Z));
            }
        }

        var index = _nodes.Count;
        _nodes.Add(node);
        if (count <= 4)
        {
            node.Start = start;
            node.Count = count;
            _nodes[index] = node;
            return index;
        }

        // Split at the median along the longest side.
        var size = node.Max - node.Min;
        var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        double Along(Vec3 v) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        Array.Sort(_order, start, count, Comparer<int>.Create((x, y) => Along(centers[x]).CompareTo(Along(centers[y]))));
        var half = count / 2;
        node.Left = Build(start, half, centers);
        node.Right = Build(start + half, count - half, centers);
        _nodes[index] = node;
        return index;
    }

    /// <summary>Distance along <paramref name="dir"/> (unit) to the nearest triangle, or null within <paramref name="maxDistance"/>.</summary>
    public double? Hit(Vec3 origin, Vec3 dir, double maxDistance = double.PositiveInfinity)
    {
        if (_nodes.Count == 0)
        {
            return null;
        }

        var best = maxDistance;
        var found = false;
        var inv = new Vec3(1 / dir.X, 1 / dir.Y, 1 / dir.Z);
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var node = _nodes[stack.Pop()];
            if (!Box(node.Min, node.Max, origin, inv, best))
            {
                continue;
            }

            if (node.Left < 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    var t = _order[i];
                    if (Triangle(t, origin, dir) is { } d && d < best)
                    {
                        best = d;
                        found = true;
                    }
                }

                continue;
            }

            stack.Push(node.Left);
            stack.Push(node.Right);
        }

        return found ? best : null;
    }

    private double? Triangle(int t, Vec3 o, Vec3 d)
    {
        // Möller–Trumbore.
        var p = Vec3.Cross(d, _e2[t]);
        var det = Vec3.Dot(_e1[t], p);
        if (Math.Abs(det) < 1e-14)
        {
            return null;
        }

        var inv = 1 / det;
        var s = o - _a[t];
        var u = Vec3.Dot(s, p) * inv;
        if (u < 0 || u > 1)
        {
            return null;
        }

        var q = Vec3.Cross(s, _e1[t]);
        var v = Vec3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1)
        {
            return null;
        }

        var dist = Vec3.Dot(_e2[t], q) * inv;
        return dist > 1e-9 ? dist : null;
    }

    private static bool Box(Vec3 min, Vec3 max, Vec3 o, Vec3 inv, double tMax)
    {
        double t0 = 0, t1 = tMax;
        for (var axis = 0; axis < 3; axis++)
        {
            var (lo, hi, oo, ii) = axis switch
            {
                0 => (min.X, max.X, o.X, inv.X),
                1 => (min.Y, max.Y, o.Y, inv.Y),
                _ => (min.Z, max.Z, o.Z, inv.Z),
            };
            var a = (lo - oo) * ii;
            var b = (hi - oo) * ii;
            if (a > b)
            {
                (a, b) = (b, a);
            }

            t0 = Math.Max(t0, double.IsNaN(a) ? t0 : a);
            t1 = Math.Min(t1, double.IsNaN(b) ? t1 : b);
            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Manufacturability checks on a body's mesh: wall thickness and mould undercuts. Rays start at
/// triangle centres (never on an edge, where a ray could graze a neighbouring face); each vertex
/// takes the worst of the triangles around it.
/// </summary>
public static class MeshAnalysis
{
    /// <summary>
    /// For each vertex, how far the material goes straight in (along the inward normal) before
    /// the opposite wall; infinity when the ray finds none.
    /// </summary>
    public static double[] Thickness(TriangleMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var rays = new MeshRayCaster(mesh);
        var result = Enumerable.Repeat(double.PositiveInfinity, mesh.VertexCount).ToArray();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (c, n) = Center(mesh, t);
            var d = rays.Hit(c - (n * 1e-6), n * -1) ?? double.PositiveInfinity;
            foreach (var v in Corners(mesh, t))
            {
                result[v] = Math.Min(result[v], d);
            }
        }

        return result;
    }

    /// <summary>
    /// For each vertex, whether it's an undercut for a two-part mould opening along ±<paramref name="pull"/>:
    /// it faces one way but other material blocks its way out in that direction.
    /// </summary>
    public static bool[] Undercuts(TriangleMesh mesh, Vec3 pull)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var rays = new MeshRayCaster(mesh);
        pull = pull.Normalized();
        var result = new bool[mesh.VertexCount];
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (c, n) = Center(mesh, t);
            var facing = Vec3.Dot(n, pull);
            if (Math.Abs(facing) < 1e-3)
            {
                continue; // walls parallel to the pull slide out
            }

            var way = facing > 0 ? pull : pull * -1;
            if (rays.Hit(c + (n * 1e-6), way) is not null)
            {
                foreach (var v in Corners(mesh, t))
                {
                    result[v] = true;
                }
            }
        }

        return result;
    }

    private static int[] Corners(TriangleMesh mesh, int t) => [mesh.Triangles[3 * t], mesh.Triangles[(3 * t) + 1], mesh.Triangles[(3 * t) + 2]];

    /// <summary>A triangle's centre and outward normal (its geometric normal, turned to agree with its vertex normals).</summary>
    private static (Vec3 C, Vec3 N) Center(TriangleMesh mesh, int t)
    {
        Vec3 P(int v) => new(mesh.Positions[3 * v], mesh.Positions[(3 * v) + 1], mesh.Positions[(3 * v) + 2]);
        Vec3 N(int v) => new(mesh.Normals[3 * v], mesh.Normals[(3 * v) + 1], mesh.Normals[(3 * v) + 2]);
        var (a, b, c) = (mesh.Triangles[3 * t], mesh.Triangles[(3 * t) + 1], mesh.Triangles[(3 * t) + 2]);
        var n = Vec3.Cross(P(b) - P(a), P(c) - P(a));
        var shading = N(a) + N(b) + N(c);
        if (n.LengthSquared < 1e-24)
        {
            n = shading;
        }
        else if (Vec3.Dot(n, shading) < 0)
        {
            n = n * -1;
        }

        return ((P(a) + P(b) + P(c)) / 3, n.Normalized());
    }
}
