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

    /// <summary>A copy moved by a rigid transform (positions, normals and edge polylines).</summary>
    public TriangleMesh Transformed(Transform3 t)
    {
        var p = new float[Positions.Length];
        var n = new float[Normals.Length];
        for (var i = 0; i + 2 < Positions.Length; i += 3)
        {
            var v = t.Apply(new Vec3(Positions[i], Positions[i + 1], Positions[i + 2]));
            var w = t.ApplyVector(new Vec3(Normals[i], Normals[i + 1], Normals[i + 2]));
            (p[i], p[i + 1], p[i + 2]) = ((float)v.X, (float)v.Y, (float)v.Z);
            (n[i], n[i + 1], n[i + 2]) = ((float)w.X, (float)w.Y, (float)w.Z);
        }

        var e = new float[EdgePoints.Length];
        for (var i = 0; i + 2 < EdgePoints.Length; i += 3)
        {
            var v = t.Apply(new Vec3(EdgePoints[i], EdgePoints[i + 1], EdgePoints[i + 2]));
            (e[i], e[i + 1], e[i + 2]) = ((float)v.X, (float)v.Y, (float)v.Z);
        }

        return new TriangleMesh(p, n, Triangles, TriangleFaces, e, EdgeOffsets);
    }

    public Vec3 Vertex(int i) => new(Positions[3 * i], Positions[(3 * i) + 1], Positions[(3 * i) + 2]);
    public Vec3 EdgePoint(int i) => new(EdgePoints[3 * i], EdgePoints[(3 * i) + 1], EdgePoints[(3 * i) + 2]);
}
