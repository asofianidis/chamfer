using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document;

/// <summary>
/// A persistent reference to a face or edge: its history-based name plus the
/// position it had when the reference was made, used to repair the reference
/// when the name no longer exists after an upstream edit.
/// </summary>
public sealed record TopoRef(string Name, Vec3 Hint);

/// <summary>
/// A solid (or compound of solids) produced by the feature history, with a
/// persistent name for every face and edge. Bodies are immutable and may be
/// shared between rebuild snapshots.
/// </summary>
public sealed class Body
{
    private readonly Dictionary<string, int> _faceByName;
    private readonly Dictionary<string, int> _edgeByName;
    private TriangleMesh? _mesh;
    private FaceInfo?[]? _faces;
    private EdgeInfo?[]? _edges;

    public Body(Shape shape, string[] faceNames, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(faceNames);
        Shape = shape;
        FaceNames = faceNames;
        EdgeNames = Naming.NameEdges(shape, faceNames);
        Name = name ?? "Body";
        _faceByName = Index(FaceNames);
        _edgeByName = Index(EdgeNames);
    }

    public Shape Shape { get; }

    /// <summary>Name of face i+1.</summary>
    public string[] FaceNames { get; }

    /// <summary>Name of edge i+1.</summary>
    public string[] EdgeNames { get; }

    public string Name { get; }

    public int FaceCount => FaceNames.Length;
    public int EdgeCount => EdgeNames.Length;

    public string FaceName(int index) => FaceNames[index - 1];
    public string EdgeName(int index) => EdgeNames[index - 1];

    public FaceInfo Face(int index) => (_faces ??= new FaceInfo?[FaceCount])[index - 1] ??= Shape.Face(index);
    public EdgeInfo Edge(int index) => (_edges ??= new EdgeInfo?[EdgeCount])[index - 1] ??= Shape.Edge(index);

    /// <summary>Display tessellation, computed once.</summary>
    public TriangleMesh Mesh => _mesh ??= Shape.Tessellate(MeshDeflection(), 0.25);

    public TopoRef FaceRef(int index) => new(FaceName(index), Face(index).Centroid);
    public TopoRef EdgeRef(int index) => new(EdgeName(index), Edge(index).Midpoint);

    /// <summary>Finds a face by name, falling back to the face nearest the hint.</summary>
    public int ResolveFace(TopoRef r, out bool repaired)
    {
        ArgumentNullException.ThrowIfNull(r);
        repaired = false;
        if (_faceByName.TryGetValue(r.Name, out var i))
        {
            return i;
        }

        repaired = true;
        return Nearest(FaceCount, k => Face(k).Centroid, r.Hint);
    }

    /// <summary>Finds an edge by name, falling back to the edge nearest the hint.</summary>
    public int ResolveEdge(TopoRef r, out bool repaired)
    {
        ArgumentNullException.ThrowIfNull(r);
        repaired = false;
        if (_edgeByName.TryGetValue(r.Name, out var i))
        {
            return i;
        }

        repaired = true;
        return Nearest(EdgeCount, k => Edge(k).Midpoint, r.Hint);
    }

    public bool HasFace(string name) => _faceByName.ContainsKey(name);
    public bool HasEdge(string name) => _edgeByName.ContainsKey(name);

    private double MeshDeflection()
    {
        var d = Shape.Bounds.Diagonal;
        return Math.Clamp(d * 0.0008, 0.002, 0.5);
    }

    private static int Nearest(int count, Func<int, Vec3> position, Vec3 hint)
    {
        var best = 0;
        var bestD = double.PositiveInfinity;
        for (var k = 1; k <= count; k++)
        {
            var d = Vec3.Distance(position(k), hint);
            if (d < bestD)
            {
                bestD = d;
                best = k;
            }
        }

        return best;
    }

    private static Dictionary<string, int> Index(string[] names)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i++)
        {
            map.TryAdd(names[i], i + 1);
        }

        return map;
    }
}
