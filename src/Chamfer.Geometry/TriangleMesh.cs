namespace Chamfer.Geometry;

/// <summary>
/// A render-ready tessellation of a B-rep shape. Triangles carry the 1-based
/// index of the face they belong to, and edges are polylines indexed by the
/// shape's 1-based edge index, so picking maps straight back to topology.
/// </summary>
public sealed class TriangleMesh
{
    public TriangleMesh(float[] positions, float[] normals, int[] triangles, int[] triangleFaces, float[] edgePoints, int[] edgeOffsets)
    {
        Positions = positions;
        Normals = normals;
        Triangles = triangles;
        TriangleFaces = triangleFaces;
        EdgePoints = edgePoints;
        EdgeOffsets = edgeOffsets;
        var box = Box3.Empty;
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            box = box.Include(new Vec3(positions[i], positions[i + 1], positions[i + 2]));
        }

        for (var i = 0; i + 2 < edgePoints.Length; i += 3)
        {
            box = box.Include(new Vec3(edgePoints[i], edgePoints[i + 1], edgePoints[i + 2]));
        }

        Bounds = box;
    }

    public static TriangleMesh Empty { get; } = new([], [], [], [], [], [0]);

    /// <summary>xyz per vertex.</summary>
    public float[] Positions { get; }

    /// <summary>xyz unit normal per vertex.</summary>
    public float[] Normals { get; }

    /// <summary>Three 0-based vertex indices per triangle, counter-clockwise from outside.</summary>
    public int[] Triangles { get; }

    /// <summary>1-based face index per triangle.</summary>
    public int[] TriangleFaces { get; }

    /// <summary>xyz per edge polyline point.</summary>
    public float[] EdgePoints { get; }

    /// <summary>EdgeCount + 1 offsets into <see cref="EdgePoints"/> (in points).</summary>
    public int[] EdgeOffsets { get; }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Triangles.Length / 3;
    public int EdgeCount => EdgeOffsets.Length - 1;
    public Box3 Bounds { get; }

    public Vec3 Vertex(int i) => new(Positions[3 * i], Positions[(3 * i) + 1], Positions[(3 * i) + 2]);
    public Vec3 EdgePoint(int i) => new(EdgePoints[3 * i], EdgePoints[(3 * i) + 1], EdgePoints[(3 * i) + 2]);
}
