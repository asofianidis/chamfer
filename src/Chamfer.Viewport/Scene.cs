using Chamfer.Geometry;

namespace Chamfer.Viewport;

/// <summary>An sRGB colour with alpha, 0-255 per channel.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba FromHex(uint rgb, byte a = 255) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, a);

    public Rgba WithAlpha(byte a) => this with { A = a };

    public static Rgba Lerp(Rgba x, Rgba y, double t) => new(
        (byte)Math.Round(x.R + ((y.R - x.R) * t)),
        (byte)Math.Round(x.G + ((y.G - x.G) * t)),
        (byte)Math.Round(x.B + ((y.B - x.B) * t)),
        (byte)Math.Round(x.A + ((y.A - x.A) * t)));
}

public enum DisplayStyle
{
    ShadedWithEdges,
    Shaded,
    Wireframe,
    HiddenLinesRemoved,
}

/// <summary>A triangle mesh to draw, with per-face pick ids and optional per-face colours.</summary>
public sealed class MeshItem
{
    public required TriangleMesh Mesh { get; init; }

    public Rgba Color { get; set; } = Rgba.FromHex(0xB4BCC6);

    public Rgba EdgeColor { get; set; } = Rgba.FromHex(0x20242A);

    /// <summary>Overrides for individual faces (1-based face index), e.g. hover and selection.</summary>
    public Dictionary<int, Rgba> FaceColors { get; } = [];

    /// <summary>Overrides for individual edges (1-based edge index).</summary>
    public Dictionary<int, Rgba> EdgeColors { get; } = [];

    /// <summary>Pick id of face 1; face i gets FacePickBase + i - 1. 0 = not pickable.</summary>
    public int FacePickBase { get; set; }

    /// <summary>Pick id of edge 1; edge i gets EdgePickBase + i - 1. 0 = not pickable.</summary>
    public int EdgePickBase { get; set; }

    /// <summary>Per-vertex colours (e.g. a stress plot); when set they replace <see cref="Color"/>.</summary>
    public Rgba[]? VertexColors { get; set; }

    /// <summary>0-1; values below 1 draw the mesh see-through, without writing depth.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>
    /// Placement of the mesh in the scene (instancing: many items can share one mesh). Null means
    /// the mesh is already in scene coordinates.
    /// </summary>
    public Transform3? Transform { get; set; }

    /// <summary>The mesh's bounds in scene coordinates.</summary>
    public Box3 WorldBounds
    {
        get
        {
            var b = Mesh.Bounds;
            if (Transform is not { } t || b.IsEmpty)
            {
                return b;
            }

            var box = Box3.Empty;
            for (var k = 0; k < 8; k++)
            {
                box = box.Include(t.Apply(new Vec3((k & 1) == 0 ? b.Min.X : b.Max.X, (k & 2) == 0 ? b.Min.Y : b.Max.Y, (k & 4) == 0 ? b.Min.Z : b.Max.Z)));
            }

            return box;
        }
    }

    /// <summary>A vertex in scene coordinates.</summary>
    public Vec3 WorldVertex(int i) => Transform is { } t ? t.Apply(Mesh.Vertex(i)) : Mesh.Vertex(i);

    /// <summary>A vertex normal in scene coordinates.</summary>
    public Vec3 WorldNormal(int i)
    {
        var n = new Vec3(Mesh.Normals[3 * i], Mesh.Normals[(3 * i) + 1], Mesh.Normals[(3 * i) + 2]);
        return Transform is { } t ? t.ApplyVector(n) : n;
    }

    /// <summary>An edge polyline point in scene coordinates.</summary>
    public Vec3 WorldEdgePoint(int k) => Transform is { } t ? t.Apply(Mesh.EdgePoint(k)) : Mesh.EdgePoint(k);
}

/// <summary>A text label anchored at a model point.</summary>
public sealed record LabelItem(Vec3 At, string Text, Rgba Color);

/// <summary>A polyline overlay: sketch geometry, axes, plane borders.</summary>
public sealed class LineItem
{
    public required IReadOnlyList<Vec3> Points { get; init; }

    public bool Closed { get; init; }

    public Rgba Color { get; set; } = Rgba.FromHex(0x1D4ED8);

    public double Width { get; set; } = 1.5;

    /// <summary>Hidden parts are drawn too, faintly.</summary>
    public bool ShowHidden { get; set; }

    public bool Dashed { get; set; }

    public int PickId { get; set; }

    /// <summary>Pulled towards the camera so lines on faces win the depth test.</summary>
    public bool OnTop { get; set; }
}

/// <summary>A point marker.</summary>
public sealed class PointItem
{
    public required Vec3 Position { get; init; }

    public Rgba Color { get; set; } = Rgba.FromHex(0x1D4ED8);

    public double Size { get; set; } = 5;

    public int PickId { get; set; }
}

/// <summary>A translucent planar quad, for reference planes.</summary>
public sealed class QuadItem
{
    public required Vec3 A { get; init; }
    public required Vec3 B { get; init; }
    public required Vec3 C { get; init; }
    public required Vec3 D { get; init; }

    public Rgba Fill { get; set; } = Rgba.FromHex(0x60A5FA, 40);

    public int PickId { get; set; }
}

/// <summary>Everything to draw in one frame.</summary>
public sealed class Scene
{
    public List<MeshItem> Meshes { get; } = [];
    public List<LineItem> Lines { get; } = [];
    public List<PointItem> Points { get; } = [];
    public List<QuadItem> Quads { get; } = [];

    /// <summary>Text anchored at points in the model (3D annotations), drawn over the image.</summary>
    public List<LabelItem> Labels { get; } = [];

    public DisplayStyle Style { get; set; } = DisplayStyle.ShadedWithEdges;

    /// <summary>Section view: geometry on the positive side of this plane is cut away.</summary>
    public Plane3? ClipPlane { get; set; }

    /// <summary>Colour of the cut faces in a section view.</summary>
    public Rgba CapColor { get; set; } = Rgba.FromHex(0xE0A458);

    /// <summary>Zebra stripes instead of normal shading, to judge surface continuity.</summary>
    public bool Zebra { get; set; }

    public Rgba BackgroundTop { get; set; } = Rgba.FromHex(0xF3F5F8);
    public Rgba BackgroundBottom { get; set; } = Rgba.FromHex(0xC5CED8);

    public Box3 Bounds
    {
        get
        {
            var box = Box3.Empty;
            foreach (var m in Meshes)
            {
                box = box.Union(m.WorldBounds);
            }

            foreach (var l in Lines)
            {
                foreach (var p in l.Points)
                {
                    box = box.Include(p);
                }
            }

            foreach (var p in Points)
            {
                box = box.Include(p.Position);
            }

            foreach (var q in Quads)
            {
                box = box.Include(q.A).Include(q.B).Include(q.C).Include(q.D);
            }

            return box;
        }
    }
}
