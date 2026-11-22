using Chamfer.Geometry;

namespace Chamfer.Viewport;

/// <summary>
/// A portable z-buffer renderer that draws a <see cref="Scene"/> into a BGRA
/// pixel buffer, plus two ID buffers for picking: one for faces and one for
/// overlays (edges, lines and points, drawn a few pixels wide so they're easy
/// to hit). It runs on any platform and headless, which keeps rendering
/// testable; a GPU backend can replace it behind the same scene description.
/// </summary>
public sealed class SoftwareRenderer
{
    private const int Bands = 32;
    private const int OverlayPickRadius = 3;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Premultiplied BGRA pixels, row-major, alpha always 255.</summary>
    public uint[] Pixels { get; private set; } = [];

    /// <summary>Depth along the view direction; +inf where nothing was drawn.</summary>
    public float[] Depth { get; private set; } = [];

    public int[] FaceIds { get; private set; } = [];

    public int[] OverlayIds { get; private set; } = [];

    private float[] _overlayDepth = [];
    private Plane3? _clip;
    private Rgba _cap;
    private bool _zebra;
    private Camera _camera = new();

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
        {
            return;
        }

        Width = width;
        Height = height;
        var n = width * height;
        Pixels = new uint[n];
        Depth = new float[n];
        FaceIds = new int[n];
        OverlayIds = new int[n];
        _overlayDepth = new float[n];
    }

    public void Render(Scene scene, Camera camera)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        _camera = camera;
        Clear(scene);

        var drawFaces = scene.Style != DisplayStyle.Wireframe;
        var drawEdges = scene.Style != DisplayStyle.Shaded;
        var hlr = scene.Style == DisplayStyle.HiddenLinesRemoved;
        var edgeBias = (float)(camera.ViewHeight * 0.004);

        _clip = scene.ClipPlane;
        _cap = scene.CapColor;
        _zebra = scene.Zebra;
        var projected = scene.Meshes.Select(m => Project(m.Mesh)).ToList();
        if (drawFaces)
        {
            for (var i = 0; i < scene.Meshes.Count; i++)
            {
                if (scene.Meshes[i].Opacity >= 1)
                {
                    DrawMesh(scene.Meshes[i], projected[i], hlr ? Blend(scene.BackgroundTop, scene.BackgroundBottom, 0.3) : null, opaque: true);
                }
            }
        }

        foreach (var q in scene.Quads)
        {
            DrawQuad(q);
        }

        if (drawFaces)
        {
            for (var i = 0; i < scene.Meshes.Count; i++)
            {
                if (scene.Meshes[i].Opacity < 1)
                {
                    DrawMesh(scene.Meshes[i], projected[i], null, opaque: false);
                }
            }
        }

        if (drawEdges || scene.Meshes.Any(m => m.EdgeColors.Count > 0))
        {
            foreach (var m in scene.Meshes)
            {
                var mesh = m.Mesh;
                for (var e = 0; e < mesh.EdgeCount; e++)
                {
                    var highlighted = m.EdgeColors.TryGetValue(e + 1, out var color);
                    if (!drawEdges && !highlighted)
                    {
                        continue;
                    }

                    var c = highlighted ? color : m.EdgeColor;
                    var pts = new List<Vec3>();
                    for (var k = mesh.EdgeOffsets[e]; k < mesh.EdgeOffsets[e + 1]; k++)
                    {
                        pts.Add(mesh.EdgePoint(k));
                    }

                    DrawPolyline(pts, false, c, highlighted ? 2.5 : 1.2, m.EdgePickBase == 0 ? 0 : m.EdgePickBase + e,
                        drawFaces ? edgeBias : float.PositiveInfinity, false, false);
                }
            }
        }

        foreach (var l in scene.Lines)
        {
            DrawPolyline(l.Points, l.Closed, l.Color, l.Width, l.PickId, l.OnTop ? edgeBias * 3 : edgeBias, l.ShowHidden, l.Dashed);
        }

        foreach (var p in scene.Points)
        {
            DrawPoint(p, edgeBias * 3);
        }
    }

    /// <summary>
    /// Pick id under a pixel: the nearest overlay (edge, line, point) within a
    /// few pixels wins; otherwise the face. 0 = nothing.
    /// </summary>
    public int Pick(int x, int y, int radius = OverlayPickRadius)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return 0;
        }

        var best = 0;
        var bestD = int.MaxValue;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= Width || py >= Height)
                {
                    continue;
                }

                var id = OverlayIds[(py * Width) + px];
                var d = (dx * dx) + (dy * dy);
                if (id != 0 && d < bestD)
                {
                    best = id;
                    bestD = d;
                }
            }
        }

        return best != 0 ? best : FaceIds[(y * Width) + x];
    }

    /// <summary>The model point under a pixel, or null over the background.</summary>
    public Vec3? PointAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return null;
        }

        var z = Depth[(y * Width) + x];
        if (float.IsPositiveInfinity(z))
        {
            return null;
        }

        var (origin, dir) = _camera.Ray(x + 0.5, y + 0.5, Width, Height);
        if (_camera.Perspective)
        {
            // Depth is distance along the view axis.
            var along = z / Vec3.Dot(dir, -_camera.Back);
            return origin + (dir * along);
        }

        var targetDepth = Vec3.Dot(origin - _camera.Target, _camera.Back);
        return origin + (dir * (targetDepth + z));
    }

    // ------------------------------------------------------------------ setup

    private void Clear(Scene scene)
    {
        var w = Width;
        Parallel.For(0, Height, y =>
        {
            var c = Pack(Blend(scene.BackgroundTop, scene.BackgroundBottom, Height <= 1 ? 0 : (double)y / (Height - 1)));
            var row = y * w;
            Array.Fill(Pixels, c, row, w);
            Array.Fill(Depth, float.PositiveInfinity, row, w);
            Array.Fill(_overlayDepth, float.PositiveInfinity, row, w);
            Array.Clear(FaceIds, row, w);
            Array.Clear(OverlayIds, row, w);
        });
    }

    private sealed class Projected
    {
        public required float[] X { get; init; }
        public required float[] Y { get; init; }
        public required float[] Z { get; init; }
        public required float[] Nx { get; init; }
        public required float[] Ny { get; init; }
        public required float[] Nz { get; init; }

        /// <summary>Signed distance to the clip plane per vertex (empty without clipping).</summary>
        public required float[] Clip { get; init; }
    }

    private Projected Project(TriangleMesh mesh)
    {
        var n = mesh.VertexCount;
        var p = new Projected
        {
            X = new float[n], Y = new float[n], Z = new float[n],
            Nx = new float[n], Ny = new float[n], Nz = new float[n],
            Clip = _clip is null ? [] : new float[n],
        };
        var clip = _clip;
        var cam = _camera;
        int w = Width, h = Height;
        Parallel.For(0, (n + 4095) / 4096, chunk =>
        {
            var end = Math.Min(n, (chunk + 1) * 4096);
            for (var i = chunk * 4096; i < end; i++)
            {
                var s = cam.Project(mesh.Vertex(i), w, h);
                p.X[i] = (float)s.X;
                p.Y[i] = (float)s.Y;
                p.Z[i] = (float)s.Z;
                var nn = new Vec3(mesh.Normals[3 * i], mesh.Normals[(3 * i) + 1], mesh.Normals[(3 * i) + 2]);
                p.Nx[i] = (float)Vec3.Dot(nn, cam.Right);
                p.Ny[i] = (float)Vec3.Dot(nn, cam.Up);
                p.Nz[i] = (float)Vec3.Dot(nn, cam.Back);
                if (clip is { } cp)
                {
                    p.Clip[i] = (float)cp.SignedDistance(mesh.Vertex(i));
                }
            }
        });
        return p;
    }

    // ----------------------------------------------------------------- meshes

    private void DrawMesh(MeshItem item, Projected p, Rgba? flatColor, bool opaque)
    {
        var mesh = item.Mesh;
        var tris = mesh.Triangles;
        var faces = mesh.TriangleFaces;
        var triCount = mesh.TriangleCount;
        int w = Width, h = Height;
        var bandHeight = (h + Bands - 1) / Bands;

        // Per-triangle screen bounds, computed once.
        var minY = new int[triCount];
        var maxY = new int[triCount];
        for (var t = 0; t < triCount; t++)
        {
            int a = tris[3 * t], b = tris[(3 * t) + 1], c = tris[(3 * t) + 2];
            minY[t] = (int)Math.Floor(Math.Min(p.Y[a], Math.Min(p.Y[b], p.Y[c])));
            maxY[t] = (int)Math.Ceiling(Math.Max(p.Y[a], Math.Max(p.Y[b], p.Y[c])));
        }

        var alpha = (float)Math.Clamp(item.Opacity, 0, 1);
        Parallel.For(0, Bands, band =>
        {
            var y0 = band * bandHeight;
            var y1 = Math.Min(h, y0 + bandHeight);
            for (var t = 0; t < triCount; t++)
            {
                if (maxY[t] < y0 || minY[t] >= y1)
                {
                    continue;
                }

                int ia = tris[3 * t], ib = tris[(3 * t) + 1], ic = tris[(3 * t) + 2];
                float x0 = p.X[ia], yA = p.Y[ia], x1 = p.X[ib], yB = p.Y[ib], x2 = p.X[ic], yC = p.Y[ic];
                var area = ((x1 - x0) * (yC - yA)) - ((x2 - x0) * (yB - yA));
                if (Math.Abs(area) < 1e-9f || float.IsNaN(area))
                {
                    continue;
                }

                var inv = 1f / area;
                var bx0 = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
                var bx1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
                var by0 = Math.Max(y0, minY[t]);
                var by1 = Math.Min(y1 - 1, maxY[t]);
                var face = faces[t];
                var color = item.FaceColors.TryGetValue(face, out var fc) ? fc : item.Color;
                var vc = item.VertexColors;
                var pick = item.FacePickBase == 0 ? 0 : item.FacePickBase + face - 1;
                for (var y = by0; y <= by1; y++)
                {
                    var py = y + 0.5f;
                    var row = y * w;
                    for (var x = bx0; x <= bx1; x++)
                    {
                        var px = x + 0.5f;
                        var wa = (((x1 - px) * (yC - py)) - ((x2 - px) * (yB - py))) * inv;
                        var wb = (((x2 - px) * (yA - py)) - ((x0 - px) * (yC - py))) * inv;
                        var wc = 1 - wa - wb;
                        const float eps = -1e-5f;
                        if (wa < eps || wb < eps || wc < eps)
                        {
                            continue;
                        }

                        var z = (wa * p.Z[ia]) + (wb * p.Z[ib]) + (wc * p.Z[ic]);
                        var i = row + x;
                        if (z >= Depth[i])
                        {
                            continue;
                        }

                        if (p.Clip.Length > 0 && (wa * p.Clip[ia]) + (wb * p.Clip[ib]) + (wc * p.Clip[ic]) > 0)
                        {
                            continue;
                        }

                        uint shaded;
                        if (flatColor is { } flat)
                        {
                            shaded = Pack(flat);
                        }
                        else
                        {
                            var nx = (wa * p.Nx[ia]) + (wb * p.Nx[ib]) + (wc * p.Nx[ic]);
                            var ny = (wa * p.Ny[ia]) + (wb * p.Ny[ib]) + (wc * p.Ny[ic]);
                            var nz = (wa * p.Nz[ia]) + (wb * p.Nz[ib]) + (wc * p.Nz[ic]);
                            var pixelColor = color;
                            if (vc is not null)
                            {
                                Rgba ca = vc[ia], cb = vc[ib], cc = vc[ic];
                                pixelColor = new Rgba(
                                    (byte)((wa * ca.R) + (wb * cb.R) + (wc * cc.R)),
                                    (byte)((wa * ca.G) + (wb * cb.G) + (wc * cc.G)),
                                    (byte)((wa * ca.B) + (wb * cb.B) + (wc * cc.B)));
                            }

                            // In a section view, faces seen from behind are the inside of the cut solid: show them as the cap.
                            shaded = p.Clip.Length > 0 && nz < 0 ? Shade(_cap, 0, 0, 1) : _zebra ? ZebraShade(nx, ny, nz) : Shade(pixelColor, nx, ny, nz);
                        }

                        if (opaque)
                        {
                            Depth[i] = z;
                            Pixels[i] = shaded;
                            FaceIds[i] = pick;
                        }
                        else
                        {
                            Pixels[i] = Mix(Pixels[i], shaded, alpha);
                        }
                    }
                }
            }
        });
    }

    /// <summary>Black and white bands from the reflected view direction, like a striped light box.</summary>
    private static uint ZebraShade(float nx, float ny, float nz)
    {
        var len = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (len < 1e-12f)
        {
            return 0xFF808080u;
        }

        nx /= len;
        ny /= len;
        nz /= len;
        if (nz < 0)
        {
            (nx, ny, nz) = (-nx, -ny, -nz);
        }

        // Reflect the view vector (0, 0, 1) about the normal; stripes follow its vertical angle.
        var ry = 2 * nz * ny;
        var band = (int)MathF.Floor((MathF.Asin(Math.Clamp(ry, -1, 1)) / MathF.PI * 16) + 100) & 1;
        var v = band == 0 ? 245 : 25;
        var light = (byte)Math.Clamp(v * (0.75f + (0.25f * nz)), 0, 255);
        return 0xFF000000u | ((uint)light << 16) | ((uint)light << 8) | light;
    }

    private static uint Shade(Rgba c, float nx, float ny, float nz)
    {
        var len = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (len < 1e-12f)
        {
            return Pack(c);
        }

        nx /= len;
        ny /= len;
        nz /= len;
        if (nz < 0)
        {
            // Back side (open shells, or looking from inside): light it as the front.
            nx = -nx;
            ny = -ny;
            nz = -nz;
        }

        // Key light from the upper left in front of the viewer, plus a soft fill from below right.
        const float kx = -0.35f, ky = 0.55f, kz = 0.76f;
        const float fx = 0.45f, fy = -0.35f, fz = 0.82f;
        var key = MathF.Max(0, (nx * kx) + (ny * ky) + (nz * kz));
        var fill = MathF.Max(0, (nx * fx) + (ny * fy) + (nz * fz));

        // Blinn-Phong highlight with the view direction (0, 0, 1).
        float hx = kx, hy = ky, hz = kz + 1;
        var hl = MathF.Sqrt((hx * hx) + (hy * hy) + (hz * hz));
        var spec = MathF.Pow(MathF.Max(0, ((nx * hx) + (ny * hy) + (nz * hz)) / hl), 48) * 0.35f;

        var diffuse = 0.30f + (0.58f * key) + (0.18f * fill);
        var r = Math.Min(255f, (c.R * diffuse) + (255 * spec));
        var g = Math.Min(255f, (c.G * diffuse) + (255 * spec));
        var b = Math.Min(255f, (c.B * diffuse) + (255 * spec));
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private void DrawQuad(QuadItem q)
    {
        var pts = new[] { q.A, q.B, q.C, q.D }.Select(v => _camera.Project(v, Width, Height)).ToArray();
        var color = Pack(q.Fill with { A = 255 });
        var alpha = q.Fill.A / 255f;
        FillTriangle(pts[0], pts[1], pts[2], color, alpha);
        FillTriangle(pts[0], pts[2], pts[3], color, alpha);
    }

    private void FillTriangle(Vec3 a, Vec3 b, Vec3 c, uint color, float alpha)
    {
        var area = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        if (Math.Abs(area) < 1e-9)
        {
            return;
        }

        var inv = 1 / area;
        var x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        var x1 = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        var y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        var y1 = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                var wa = (((b.X - px) * (c.Y - py)) - ((c.X - px) * (b.Y - py))) * inv;
                var wb = (((c.X - px) * (a.Y - py)) - ((a.X - px) * (c.Y - py))) * inv;
                var wc = 1 - wa - wb;
                if (wa < 0 || wb < 0 || wc < 0)
                {
                    continue;
                }

                var z = (float)((wa * a.Z) + (wb * b.Z) + (wc * c.Z));
                var i = (y * Width) + x;
                if (z < Depth[i])
                {
                    Pixels[i] = Mix(Pixels[i], color, alpha);
                }
            }
        }
    }

    // ------------------------------------------------------------------ lines

    private void DrawPolyline(IReadOnlyList<Vec3> points, bool closed, Rgba color, double width, int pickId, float bias, bool showHidden, bool dashed)
    {
        if (points.Count < 2)
        {
            return;
        }

        if (_clip is { } clip)
        {
            // Draw only the parts on the kept side of the section plane.
            var kept = new List<Vec3>();
            var count = closed ? points.Count : points.Count - 1;
            for (var s = 0; s < count; s++)
            {
                var a = points[s];
                var b = points[(s + 1) % points.Count];
                double da = clip.SignedDistance(a), db = clip.SignedDistance(b);
                if (da > 0 && db > 0)
                {
                    if (kept.Count > 1)
                    {
                        DrawPolylineUnclipped(kept, false, color, width, pickId, bias, showHidden, dashed);
                    }

                    kept.Clear();
                    continue;
                }

                var from = da > 0 ? Vec3.Lerp(a, b, da / (da - db)) : a;
                var to = db > 0 ? Vec3.Lerp(a, b, da / (da - db)) : b;
                if (kept.Count == 0 || Vec3.Distance(kept[^1], from) > 1e-9)
                {
                    if (kept.Count > 1)
                    {
                        DrawPolylineUnclipped(kept, false, color, width, pickId, bias, showHidden, dashed);
                    }

                    kept = [from];
                }

                kept.Add(to);
            }

            if (kept.Count > 1)
            {
                DrawPolylineUnclipped(kept, false, color, width, pickId, bias, showHidden, dashed);
            }

            return;
        }

        DrawPolylineUnclipped(points, closed, color, width, pickId, bias, showHidden, dashed);
    }

    private void DrawPolylineUnclipped(IReadOnlyList<Vec3> points, bool closed, Rgba color, double width, int pickId, float bias, bool showHidden, bool dashed)
    {
        var screen = points.Select(p => _camera.Project(p, Width, Height)).ToArray();
        var segments = closed ? screen.Length : screen.Length - 1;
        var packed = Pack(color with { A = 255 });
        var alpha = color.A / 255f;
        var dashPhase = 0.0;
        for (var s = 0; s < segments; s++)
        {
            var a = screen[s];
            var b = screen[(s + 1) % screen.Length];
            DrawSegment(a, b, packed, alpha, width, pickId, bias, showHidden, dashed, ref dashPhase, includeEnd: !closed && s == segments - 1);
        }
    }

    private void DrawSegment(Vec3 a, Vec3 b, uint color, float alpha, double width, int pickId, float bias, bool showHidden, bool dashed, ref double dashPhase, bool includeEnd)
    {
        if (double.IsNaN(a.X + a.Y + b.X + b.Y))
        {
            return;
        }

        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt((dx * dx) + (dy * dy));
        var steep = Math.Abs(dy) > Math.Abs(dx);
        var steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
        if (steps == 0)
        {
            steps = 1;
        }

        // Cull segments entirely off-screen.
        var margin = width + OverlayPickRadius + 2;
        if ((a.X < -margin && b.X < -margin) || (a.Y < -margin && b.Y < -margin) ||
            (a.X > Width + margin && b.X > Width + margin) || (a.Y > Height + margin && b.Y > Height + margin))
        {
            dashPhase += len;
            return;
        }

        // Limit absurdly long segments (e.g. after extreme zoom).
        if (steps > 20000)
        {
            steps = 20000;
        }

        var half = (width / 2) + 0.5;
        var last = includeEnd ? steps : steps - 1;
        for (var i = 0; i <= last; i++)
        {
            var t = (double)i / steps;
            var x = a.X + (dx * t);
            var y = a.Y + (dy * t);
            var z = (float)(a.Z + ((b.Z - a.Z) * t)) - bias;
            var along = dashPhase + (len * t);
            if (dashed && (along % 10) > 6)
            {
                continue;
            }

            // Pixels across the minor axis.
            var major = steep ? (int)Math.Floor(y) : (int)Math.Floor(x);
            var minor = steep ? x : y;
            var lo = (int)Math.Floor(minor - half - OverlayPickRadius);
            var hi = (int)Math.Ceiling(minor + half + OverlayPickRadius);
            for (var m = lo; m <= hi; m++)
            {
                int px = steep ? m : major, py = steep ? major : m;
                if (px < 0 || py < 0 || px >= Width || py >= Height)
                {
                    continue;
                }

                var idx = (py * Width) + px;
                var dist = Math.Abs(m + 0.5 - minor);
                var visible = z <= Depth[idx];
                if (pickId != 0 && visible && dist <= half + OverlayPickRadius && z <= _overlayDepth[idx])
                {
                    _overlayDepth[idx] = z;
                    OverlayIds[idx] = pickId;
                }

                var coverage = (float)Math.Clamp(half - dist, 0, 1);
                if (coverage <= 0)
                {
                    continue;
                }

                if (visible)
                {
                    Pixels[idx] = Mix(Pixels[idx], color, coverage * alpha);
                }
                else if (showHidden)
                {
                    Pixels[idx] = Mix(Pixels[idx], color, coverage * alpha * 0.3f);
                }
            }
        }

        dashPhase += len;
    }

    private void DrawPoint(PointItem p, float bias)
    {
        var s = _camera.Project(p.Position, Width, Height);
        var z = (float)s.Z - bias;
        var r = p.Size / 2;
        var color = Pack(p.Color with { A = 255 });
        var reach = (int)Math.Ceiling(r + OverlayPickRadius);
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var px = (int)Math.Floor(s.X) + dx;
                var py = (int)Math.Floor(s.Y) + dy;
                if (px < 0 || py < 0 || px >= Width || py >= Height)
                {
                    continue;
                }

                var idx = (py * Width) + px;
                if (z > Depth[idx])
                {
                    continue;
                }

                var d = Math.Sqrt(Math.Pow(px + 0.5 - s.X, 2) + Math.Pow(py + 0.5 - s.Y, 2));
                if (p.PickId != 0 && d <= r + OverlayPickRadius && z <= _overlayDepth[idx])
                {
                    _overlayDepth[idx] = z - 1e-3f;
                    OverlayIds[idx] = p.PickId;
                }

                var coverage = (float)Math.Clamp(r + 0.5 - d, 0, 1);
                if (coverage > 0)
                {
                    Pixels[idx] = Mix(Pixels[idx], color, coverage * (p.Color.A / 255f));
                }
            }
        }
    }

    // ----------------------------------------------------------------- colour

    private static Rgba Blend(Rgba a, Rgba b, double t) => Rgba.Lerp(a, b, t);

    private static uint Pack(Rgba c) => 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static uint Mix(uint dst, uint src, float a)
    {
        if (a >= 1)
        {
            return src;
        }

        var ia = 1 - a;
        var r = (((dst >> 16) & 0xFF) * ia) + (((src >> 16) & 0xFF) * a);
        var g = (((dst >> 8) & 0xFF) * ia) + (((src >> 8) & 0xFF) * a);
        var b = ((dst & 0xFF) * ia) + ((src & 0xFF) * a);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }
}
