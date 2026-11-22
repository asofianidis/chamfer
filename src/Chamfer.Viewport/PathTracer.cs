using Chamfer.Geometry;

namespace Chamfer.Viewport;

/// <summary>How a surface reflects light in a photorealistic render.</summary>
public enum Finish
{
    /// <summary>Coloured diffuse base under a thin clear coat.</summary>
    Plastic,

    /// <summary>Coloured glossy reflection (steel, aluminium, brass).</summary>
    Metal,

    /// <summary>Rough, colour-only diffuse (wood, rubber).</summary>
    Matte,
}

public sealed class RenderSettings
{
    public int Samples { get; set; } = 64;

    public int MaxBounces { get; set; } = 4;

    /// <summary>Direction the sunlight comes from (towards the sun).</summary>
    public Vec3 SunDirection { get; set; } = new Vec3(0.45, 0.8, 0.4).Normalized();

    /// <summary>Angular radius of the sun disc, radians (softens shadows).</summary>
    public double SunRadius { get; set; } = 0.03;

    public double SunIntensity { get; set; } = 1.8;

    /// <summary>A floor under the model that catches shadows.</summary>
    public bool Floor { get; set; } = true;

    public double Exposure { get; set; } = 1;

    /// <summary>Finish per mesh (index into the scene's meshes); unlisted meshes are plastic.</summary>
    public Dictionary<int, Finish> Finishes { get; } = [];

    public double MetalRoughness { get; set; } = 0.25;

    public int Seed { get; set; } = 1;
}

/// <summary>
/// A small CPU path tracer for the scene's meshes: BVH-accelerated rays, diffuse, glossy
/// metal and clear-coated plastic surfaces, a soft sun with next-event estimation, a sky
/// gradient and a shadow-catching floor, tone-mapped to sRGB.
/// </summary>
public sealed class PathTracer
{
    private readonly List<Tri> _tris = [];
    private Node[] _nodes = [];
    private int[] _order = [];
    private double _floorY;
    private RenderSettings _settings = new();

    private readonly record struct Tri(Vec3 A, Vec3 E1, Vec3 E2, Vec3 Na, Vec3 Nb, Vec3 Nc, Vec3 Color, Finish Finish);

    private struct Node
    {
        public Vec3 Min;
        public Vec3 Max;
        public int Left;  // child index, or −1 for a leaf
        public int Right;
        public int Start; // first triangle (leaf)
        public int Count; // triangles (leaf)
    }

    /// <summary>Renders the scene from the camera; pixels are 0xAARRGGBB like the raster renderer's.</summary>
    public uint[] Render(Scene scene, Camera camera, int width, int height, RenderSettings? settings = null,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        _settings = settings ?? new RenderSettings();
        Build(scene);
        var pixels = new uint[width * height];
        var done = 0;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            var rng = new Random(unchecked((_settings.Seed * 7919) + y)); // deterministic per row
            for (var x = 0; x < width; x++)
            {
                var sum = Vec3.Zero;
                for (var s = 0; s < _settings.Samples; s++)
                {
                    var (o, d) = camera.Ray(x + rng.NextDouble(), y + rng.NextDouble(), width, height);
                    var sample = Trace(o, d, rng);

                    // Clamp rare very bright paths (fireflies) so low sample counts stay clean.
                    sum += new Vec3(Math.Min(sample.X, 3), Math.Min(sample.Y, 3), Math.Min(sample.Z, 3));
                }

                pixels[(y * width) + x] = ToPixel(sum / _settings.Samples * _settings.Exposure);
            }

            progress?.Report((double)Interlocked.Increment(ref done) / height);
        });
        return pixels;
    }

    private void Build(Scene scene)
    {
        _tris.Clear();
        var bounds = Box3.Empty;
        for (var m = 0; m < scene.Meshes.Count; m++)
        {
            var item = scene.Meshes[m];
            if (item.Opacity < 0.5)
            {
                continue;
            }

            var mesh = item.Mesh;
            var finish = _settings.Finishes.TryGetValue(m, out var f) ? f : Finish.Plastic;
            var baseColor = Linear(item.Color);
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                var (i0, i1, i2) = (mesh.Triangles[3 * t], mesh.Triangles[(3 * t) + 1], mesh.Triangles[(3 * t) + 2]);
                var (a, b, c) = (item.WorldVertex(i0), item.WorldVertex(i1), item.WorldVertex(i2));
                var color = item.VertexColors is { } vc ? (Linear(vc[i0]) + Linear(vc[i1]) + Linear(vc[i2])) / 3 : baseColor;
                _tris.Add(new Tri(a, b - a, c - a, item.WorldNormal(i0), item.WorldNormal(i1), item.WorldNormal(i2), color, finish));
                bounds = bounds.Include(a).Include(b).Include(c);
            }
        }

        _floorY = bounds.IsEmpty ? 0 : bounds.Min.Y - (bounds.Diagonal * 0.002);
        _order = [.. Enumerable.Range(0, _tris.Count)];
        var nodes = new List<Node>(Math.Max(1, _tris.Count * 2 / 4));
        if (_tris.Count > 0)
        {
            BuildNode(nodes, 0, _tris.Count);
        }

        _nodes = [.. nodes];
    }

    private int BuildNode(List<Node> nodes, int start, int count)
    {
        var index = nodes.Count;
        nodes.Add(default);
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = -min;
        var cmin = min;
        var cmax = max;
        for (var i = start; i < start + count; i++)
        {
            var t = _tris[_order[i]];
            foreach (var p in new[] { t.A, t.A + t.E1, t.A + t.E2 })
            {
                min = VMin(min, p);
                max = VMax(max, p);
            }

            var centroid = t.A + ((t.E1 + t.E2) / 3);
            cmin = VMin(cmin, centroid);
            cmax = VMax(cmax, centroid);
        }

        var node = new Node { Min = min, Max = max, Left = -1, Start = start, Count = count };
        if (count > 4)
        {
            var size = cmax - cmin;
            var axis = size.X > size.Y && size.X > size.Z ? 0 : size.Y > size.Z ? 1 : 2;
            var keys = _order.AsSpan(start, count).ToArray()
                .OrderBy(i => Axis(_tris[i].A + ((_tris[i].E1 + _tris[i].E2) / 3), axis)).ToArray();
            keys.CopyTo(_order, start);
            var half = count / 2;
            node.Left = BuildNode(nodes, start, half);
            node.Right = BuildNode(nodes, start + half, count - half);
        }

        nodes[index] = node;
        return index;
    }

    private static Vec3 VMin(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    private static Vec3 VMax(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    private static double Axis(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>Nearest hit along a ray: distance, triangle, barycentrics.</summary>
    private (double T, int Tri, double U, double V) Intersect(Vec3 o, Vec3 d, double tMax)
    {
        var best = (T: tMax, Tri: -1, U: 0.0, V: 0.0);
        if (_nodes.Length == 0)
        {
            return best;
        }

        var inv = new Vec3(1 / d.X, 1 / d.Y, 1 / d.Z);
        Span<int> stack = stackalloc int[64];
        var sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (!HitBox(n.Min, n.Max, o, inv, best.T))
            {
                continue;
            }

            if (n.Left < 0)
            {
                for (var i = n.Start; i < n.Start + n.Count; i++)
                {
                    var tri = _tris[_order[i]];
                    var p = Vec3.Cross(d, tri.E2);
                    var det = Vec3.Dot(tri.E1, p);
                    if (Math.Abs(det) < 1e-14)
                    {
                        continue;
                    }

                    var invDet = 1 / det;
                    var s = o - tri.A;
                    var u = Vec3.Dot(s, p) * invDet;
                    if (u < 0 || u > 1)
                    {
                        continue;
                    }

                    var q = Vec3.Cross(s, tri.E1);
                    var v = Vec3.Dot(d, q) * invDet;
                    if (v < 0 || u + v > 1)
                    {
                        continue;
                    }

                    var t = Vec3.Dot(tri.E2, q) * invDet;
                    if (t > 1e-6 && t < best.T)
                    {
                        best = (t, _order[i], u, v);
                    }
                }
            }
            else if (sp < stack.Length - 2)
            {
                stack[sp++] = n.Left;
                stack[sp++] = n.Right;
            }
        }

        return best;
    }

    private static bool HitBox(Vec3 min, Vec3 max, Vec3 o, Vec3 inv, double tMax)
    {
        double t0 = 0, t1 = tMax;
        for (var a = 0; a < 3; a++)
        {
            var (lo, hi, oo, ii) = a == 0 ? (min.X, max.X, o.X, inv.X) : a == 1 ? (min.Y, max.Y, o.Y, inv.Y) : (min.Z, max.Z, o.Z, inv.Z);
            var tn = (lo - oo) * ii;
            var tf = (hi - oo) * ii;
            if (tn > tf)
            {
                (tn, tf) = (tf, tn);
            }

            t0 = Math.Max(t0, tn);
            t1 = Math.Min(t1, tf);
            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }

    private static Vec3 Sky(Vec3 d)
    {
        var t = Math.Clamp((d.Y * 0.5) + 0.5, 0, 1);
        var horizon = new Vec3(0.95, 0.96, 1.0);
        var zenith = new Vec3(0.45, 0.6, 0.9);
        return ((horizon * (1 - t)) + (zenith * t)) * 0.5;
    }

    private Vec3 Trace(Vec3 o, Vec3 d, Random rng)
    {
        var throughput = new Vec3(1, 1, 1);
        var radiance = Vec3.Zero;
        var sunDir = _settings.SunDirection.Normalized();
        var sun = new Vec3(1, 0.97, 0.9) * _settings.SunIntensity;
        for (var bounce = 0; bounce <= _settings.MaxBounces; bounce++)
        {
            var floorT = _settings.Floor && d.Y < -1e-9 ? (_floorY - o.Y) / d.Y : double.MaxValue;
            var hit = Intersect(o, d, floorT > 1e-6 ? floorT : double.MaxValue);
            Vec3 p, n, color;
            Finish finish;
            if (hit.Tri >= 0)
            {
                var tri = _tris[hit.Tri];
                p = o + (d * hit.T);
                n = ((tri.Na * (1 - hit.U - hit.V)) + (tri.Nb * hit.U) + (tri.Nc * hit.V)).Normalized();
                if (n.LengthSquared < 0.5)
                {
                    n = Vec3.Cross(tri.E1, tri.E2).Normalized();
                }

                if (Vec3.Dot(n, d) > 0)
                {
                    n = -n;
                }

                color = tri.Color;
                finish = tri.Finish;
            }
            else if (floorT is > 1e-6 and < double.MaxValue)
            {
                p = o + (d * floorT);
                n = Vec3.UnitY;
                color = new Vec3(0.55, 0.55, 0.55);
                finish = Finish.Matte;
            }
            else
            {
                return radiance + Mul(throughput, Sky(d));
            }

            var origin = p + (n * 1e-4 * Math.Max(1, p.Length));

            // Direct sunlight through a jittered point on the sun disc.
            var toSun = Jitter(sunDir, _settings.SunRadius, rng);
            var cos = Vec3.Dot(n, toSun);
            if (cos > 0 && Intersect(origin, toSun, double.MaxValue).Tri < 0)
            {
                var direct = (finish == Finish.Metal ? Glossy(color, n, -d, toSun) : color) * cos;
                radiance += Mul(throughput, Mul(direct, sun));
            }

            // Next bounce.
            Vec3 next;
            switch (finish)
            {
                case Finish.Metal:
                    next = Jitter(Reflect(d, n), _settings.MetalRoughness, rng);
                    if (Vec3.Dot(next, n) <= 0)
                    {
                        return radiance;
                    }

                    throughput = Mul(throughput, color);
                    break;
                case Finish.Plastic:
                    // The clear coat reflects the sky (weighted by Fresnel, no extra noise); the rest scatters diffusely.
                    var fresnel = 0.04 + (0.96 * Math.Pow(1 - Math.Max(0, Vec3.Dot(-d, n)), 5));
                    var mirror = Reflect(d, n);
                    if (Intersect(origin, mirror, double.MaxValue).Tri < 0)
                    {
                        radiance += Mul(throughput, Sky(mirror)) * (fresnel * (mirror.Y < 0 && _settings.Floor ? 0.6 : 1));
                    }

                    next = CosineSample(n, rng);
                    throughput = Mul(throughput, color) * (1 - fresnel);
                    break;
                default:
                    next = CosineSample(n, rng);
                    throughput = Mul(throughput, color);
                    break;
            }

            // Russian roulette after a few bounces.
            if (bounce > 2)
            {
                var survive = Math.Clamp(Math.Max(throughput.X, Math.Max(throughput.Y, throughput.Z)), 0.05, 1);
                if (rng.NextDouble() > survive)
                {
                    return radiance;
                }

                throughput /= survive;
            }

            o = origin;
            d = next;
        }

        return radiance;
    }

    private static Vec3 Glossy(Vec3 color, Vec3 n, Vec3 view, Vec3 light)
    {
        var h = (view + light).Normalized();
        var spec = Math.Pow(Math.Max(0, Vec3.Dot(n, h)), 60) * 3;
        return color * (0.15 + spec);
    }

    private static Vec3 Reflect(Vec3 d, Vec3 n) => d - (n * (2 * Vec3.Dot(d, n)));

    private static Vec3 Jitter(Vec3 dir, double radius, Random rng)
    {
        if (radius <= 0)
        {
            return dir;
        }

        var u = dir.AnyPerpendicular().Normalized();
        var v = Vec3.Cross(dir, u);
        var r = radius * Math.Sqrt(rng.NextDouble());
        var a = 2 * Math.PI * rng.NextDouble();
        return (dir + (u * (r * Math.Cos(a))) + (v * (r * Math.Sin(a)))).Normalized();
    }

    private static Vec3 CosineSample(Vec3 n, Random rng)
    {
        var u = n.AnyPerpendicular().Normalized();
        var v = Vec3.Cross(n, u);
        var r = Math.Sqrt(rng.NextDouble());
        var a = 2 * Math.PI * rng.NextDouble();
        return ((u * (r * Math.Cos(a))) + (v * (r * Math.Sin(a))) + (n * Math.Sqrt(Math.Max(0, 1 - (r * r))))).Normalized();
    }

    private static Vec3 Mul(Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    private static Vec3 Linear(Rgba c) => new(Math.Pow(c.R / 255.0, 2.2), Math.Pow(c.G / 255.0, 2.2), Math.Pow(c.B / 255.0, 2.2));

    /// <summary>ACES-style tone curve, then sRGB gamma.</summary>
    private static uint ToPixel(Vec3 c)
    {
        static byte Channel(double x)
        {
            x = Math.Max(0, x);
            var mapped = (x * ((2.51 * x) + 0.03)) / ((x * ((2.43 * x) + 0.59)) + 0.14);
            return (byte)Math.Clamp(Math.Pow(Math.Clamp(mapped, 0, 1), 1 / 2.2) * 255 + 0.5, 0, 255);
        }

        return 0xFF000000u | ((uint)Channel(c.X) << 16) | ((uint)Channel(c.Y) << 8) | Channel(c.Z);
    }
}
