using System.Runtime.InteropServices;
using Chamfer.Geometry;

namespace Chamfer.Kernel.Occt;

/// <summary>
/// An immutable B-rep shape owned by the native kernel. Sub-shapes are
/// addressed by 1-based index per <see cref="TopoKind"/>; indices are stable for
/// the lifetime of this object. A shape is not safe to use from several threads
/// at once, because the native side builds lookup maps lazily.
/// </summary>
public sealed unsafe class Shape : IDisposable
{
    internal Shape(ShapeHandle handle)
    {
        Handle = handle;
    }

    internal ShapeHandle Handle { get; }

    /// <summary>Kernel and OCCT version string.</summary>
    public static string KernelVersion => Native.Version();

    internal static ShapeHandle Check(ShapeHandle h, string what)
    {
        if (h.IsInvalid)
        {
            h.Dispose();
            throw new KernelException($"{what}: {Native.LastError()}");
        }

        return h;
    }

    internal static Shape Wrap(ShapeHandle h, string what) => new(Check(h, what));

    public void Dispose() => Handle.Dispose();

    public TopoKind Kind => (TopoKind)Native.chf_shape_kind(Handle);

    public int Count(TopoKind kind) => Native.chf_shape_count(Handle, (int)kind);

    public int SolidCount => Count(TopoKind.Solid);
    public int FaceCount => Count(TopoKind.Face);
    public int EdgeCount => Count(TopoKind.Edge);
    public int VertexCount => Count(TopoKind.Vertex);

    public bool IsValid => Native.chf_shape_is_valid(Handle) != 0;

    /// <summary>Checks the shape for problems typical of imported geometry.</summary>
    public unsafe ShapeDiagnostics Diagnose(double tolerance = 1e-4)
    {
        var o = stackalloc int[7];
        if (Native.chf_diagnose(Handle, tolerance, o) == 0)
        {
            throw new KernelException("Shape diagnostics failed.");
        }

        return new ShapeDiagnostics(o[0], o[1], o[2], o[3], o[4], o[5], o[6]);
    }

    /// <summary>Sews gaps within the tolerance, fixes faces and shells, and turns closed shells into solids.</summary>
    public Shape Heal(double tolerance = 1e-3) => Wrap(Native.chf_heal(Handle, tolerance), "healing");

    /// <summary>A new shape referencing sub-shape <paramref name="index"/> of <paramref name="kind"/>.</summary>
    public Shape Sub(TopoKind kind, int index) => Wrap(Native.chf_shape_sub(Handle, (int)kind, index), "sub-shape");

    /// <summary>Index of <paramref name="sub"/> in this shape, or 0.</summary>
    public int IndexOf(TopoKind kind, Shape sub)
    {
        ArgumentNullException.ThrowIfNull(sub);
        return Native.chf_shape_index_of(Handle, (int)kind, sub.Handle);
    }

    public Box3 Bounds
    {
        get
        {
            var b = stackalloc double[6];
            return Native.chf_shape_bounds(Handle, b) == 0
                ? Box3.Empty
                : new Box3(new Vec3(b[0], b[1], b[2]), new Vec3(b[3], b[4], b[5]));
        }
    }

    public MassProperties MassProperties
    {
        get
        {
            var m = stackalloc double[14];
            if (Native.chf_shape_mass(Handle, m) == 0)
            {
                throw new KernelException($"mass properties: {Native.LastError()}");
            }

            return new MassProperties(m[0], m[1], new Vec3(m[2], m[3], m[4]), m[5], m[6], m[7], m[8], m[9], m[10]);
        }
    }

    /// <summary>Sub-shapes of kind <paramref name="of"/> that contain sub-shape (<paramref name="kind"/>, <paramref name="index"/>).</summary>
    public int[] Ancestors(TopoKind kind, int index, TopoKind of) =>
        ReadList((p, cap) => Native.chf_shape_ancestors(Handle, (int)kind, index, (int)of, p, cap), "ancestors");

    /// <summary>Sub-shapes of kind <paramref name="of"/> inside sub-shape (<paramref name="kind"/>, <paramref name="index"/>).</summary>
    public int[] Children(TopoKind kind, int index, TopoKind of) =>
        ReadList((p, cap) => Native.chf_shape_children(Handle, (int)kind, index, (int)of, p, cap), "children");

    public int[] FacesOfEdge(int edge) => Ancestors(TopoKind.Edge, edge, TopoKind.Face);
    public int[] EdgesOfFace(int face) => Children(TopoKind.Face, face, TopoKind.Edge);

    public FaceInfo Face(int index)
    {
        var o = stackalloc double[16];
        if (Native.chf_face_info(Handle, index, o) == 0)
        {
            throw new KernelException($"face {index}: {Native.LastError()}");
        }

        return new FaceInfo(index, (SurfaceKind)(int)o[0], o[1], new Vec3(o[2], o[3], o[4]), new Vec3(o[5], o[6], o[7]),
            new Vec3(o[8], o[9], o[10]), new Vec3(o[11], o[12], o[13]), o[14]);
    }

    /// <summary>The plane of a planar face, oriented with its outward normal; null when the face isn't planar.</summary>
    public Plane3? FacePlane(int index)
    {
        var o = stackalloc double[9];
        return Native.chf_face_plane(Handle, index, o) == 0
            ? null
            : new Plane3(new Vec3(o[0], o[1], o[2]), new Vec3(o[3], o[4], o[5]), new Vec3(o[6], o[7], o[8]));
    }

    /// <summary>Closest point on a face and the outward normal there.</summary>
    public (Vec3 Point, Vec3 Normal, double Distance)? ProjectOnFace(int index, Vec3 p)
    {
        var input = stackalloc double[3] { p.X, p.Y, p.Z };
        var o = stackalloc double[6];
        var d = Native.chf_face_project(Handle, index, input, o);
        return d < 0 ? null : (new Vec3(o[0], o[1], o[2]), new Vec3(o[3], o[4], o[5]), d);
    }

    public EdgeInfo Edge(int index)
    {
        var o = stackalloc double[16];
        if (Native.chf_edge_info(Handle, index, o) == 0)
        {
            throw new KernelException($"edge {index}: {Native.LastError()}");
        }

        return new EdgeInfo(index, (CurveKind)(int)o[0], o[1], new Vec3(o[2], o[3], o[4]), new Vec3(o[5], o[6], o[7]),
            new Vec3(o[8], o[9], o[10]), new Vec3(o[11], o[12], o[13]), o[14], o[15] != 0);
    }

    public IReadOnlyList<Vec3> SampleEdge(int index, double deflection = 0.05)
    {
        var cap = 256;
        while (true)
        {
            var buffer = new double[cap * 3];
            int n;
            fixed (double* p = buffer)
            {
                n = Native.chf_edge_sample(Handle, index, deflection, p, cap);
            }

            if (n < 0)
            {
                throw new KernelException($"edge {index}: {Native.LastError()}");
            }

            if (n <= cap)
            {
                var result = new Vec3[n];
                for (var i = 0; i < n; i++)
                {
                    result[i] = new Vec3(buffer[3 * i], buffer[(3 * i) + 1], buffer[(3 * i) + 2]);
                }

                return result;
            }

            cap = n;
        }
    }

    /// <summary>Point-in-solid tests: 1 inside, 0 outside, 2 on the boundary, per point.</summary>
    public byte[] Classify(IReadOnlyList<Vec3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var data = new double[points.Count * 3];
        for (var i = 0; i < points.Count; i++)
        {
            (data[3 * i], data[(3 * i) + 1], data[(3 * i) + 2]) = (points[i].X, points[i].Y, points[i].Z);
        }

        var result = new byte[points.Count];
        fixed (double* pd = data)
        fixed (byte* pr = result)
        {
            if (Native.chf_classify(Handle, points.Count, pd, pr) == 0)
            {
                throw new KernelException($"classify: {Native.LastError()}");
            }
        }

        return result;
    }

    /// <summary>Minimum distance to another shape and the closest points on each.</summary>
    public (double Distance, Vec3 OnThis, Vec3 OnOther) DistanceTo(Shape other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var o = stackalloc double[6];
        var d = Native.chf_distance(Handle, other.Handle, o);
        if (d < 0)
        {
            throw new KernelException($"distance: {Native.LastError()}");
        }

        return (d, new Vec3(o[0], o[1], o[2]), new Vec3(o[3], o[4], o[5]));
    }

    public Vec3 Vertex(int index)
    {
        var o = stackalloc double[3];
        if (Native.chf_vertex_point(Handle, index, o) == 0)
        {
            throw new KernelException($"vertex {index}: {Native.LastError()}");
        }

        return new Vec3(o[0], o[1], o[2]);
    }

    /// <summary>Triangulates the shape for display.</summary>
    /// <param name="linearDeflection">Maximum chord deviation in mm.</param>
    /// <param name="angularDeflection">Maximum angle between adjacent facets, radians.</param>
    public TriangleMesh Tessellate(double linearDeflection = 0.05, double angularDeflection = 0.3)
    {
        var mesh = Native.chf_tessellate(Handle, linearDeflection, angularDeflection);
        if (mesh == IntPtr.Zero)
        {
            throw new KernelException($"tessellation: {Native.LastError()}");
        }

        try
        {
            var counts = stackalloc int[4];
            Native.chf_mesh_counts(mesh, counts);
            var positions = new float[counts[0] * 3];
            var normals = new float[counts[0] * 3];
            var triangles = new int[counts[1] * 3];
            var faces = new int[counts[1]];
            var edgePoints = new float[counts[2] * 3];
            var edgeOffsets = new int[counts[3] + 1];
            fixed (float* pp = positions, pn = normals, pe = edgePoints)
            fixed (int* pt = triangles, pf = faces, po = edgeOffsets)
            {
                Native.chf_mesh_copy(mesh, pp, pn, pt, pf, pe, po);
            }

            return new TriangleMesh(positions, normals, triangles, faces, edgePoints, edgeOffsets);
        }
        finally
        {
            Native.chf_mesh_free(mesh);
        }
    }

    /// <summary>
    /// Hidden-line projection onto a view plane. <paramref name="towardViewer"/> points from the
    /// model to the eye; <paramref name="xDirection"/> is the view's right. Coordinates are in that
    /// frame, relative to <paramref name="origin"/>.
    /// </summary>
    public IReadOnlyList<ProjectedLine> Project(Vec3 origin, Vec3 towardViewer, Vec3 xDirection, double deflection = 0.01)
    {
        var a = stackalloc double[9] { origin.X, origin.Y, origin.Z, towardViewer.X, towardViewer.Y, towardViewer.Z, xDirection.X, xDirection.Y, xDirection.Z };
        var lines = Native.chf_hlr(Handle, a, deflection);
        if (lines == IntPtr.Zero)
        {
            throw new KernelException($"projection: {Native.LastError()}");
        }

        try
        {
            var counts = stackalloc int[2];
            Native.chf_lines_counts(lines, counts);
            var points = new double[counts[0] * 2];
            var offsets = new int[counts[1] + 1];
            var kinds = new int[counts[1]];
            fixed (double* pp = points)
            fixed (int* po = offsets, pk = kinds)
            {
                Native.chf_lines_copy(lines, pp, po, pk);
            }

            var result = new ProjectedLine[counts[1]];
            for (var i = 0; i < counts[1]; i++)
            {
                var pts = new Point2[offsets[i + 1] - offsets[i]];
                for (var k = 0; k < pts.Length; k++)
                {
                    var j = offsets[i] + k;
                    pts[k] = new Point2(points[2 * j], points[(2 * j) + 1]);
                }

                result[i] = new ProjectedLine((LineKind)kinds[i], pts);
            }

            return result;
        }
        finally
        {
            Native.chf_lines_free(lines);
        }
    }

    // ---------------------------------------------------------------- I/O

    public string ToBrep()
    {
        var p = Native.chf_to_brep(Handle);
        if (p == IntPtr.Zero)
        {
            throw new KernelException($"BRep export: {Native.LastError()}");
        }

        try
        {
            return Marshal.PtrToStringUTF8(p) ?? string.Empty;
        }
        finally
        {
            Native.chf_free_string(p);
        }
    }

    public static Shape FromBrep(string text) => Wrap(Native.chf_from_brep(text), "BRep import");

    public void WriteStep(string path) => CheckWrite(Native.chf_write_step(Handle, path), "STEP export");
    public void WriteIges(string path) => CheckWrite(Native.chf_write_iges(Handle, path), "IGES export");
    public void WriteStl(string path, double deflection = 0.05, bool ascii = false) =>
        CheckWrite(Native.chf_write_stl(Handle, path, deflection, ascii ? 1 : 0), "STL export");
    public void WriteBrep(string path) => CheckWrite(Native.chf_write_brep(Handle, path), "BRep export");

    public static Shape ReadStep(string path) => Wrap(Native.chf_read_step(path), "STEP import");
    public static Shape ReadIges(string path) => Wrap(Native.chf_read_iges(path), "IGES import");
    public static Shape ReadBrep(string path) => Wrap(Native.chf_read_brep(path), "BRep import");

    private static void CheckWrite(int ok, string what)
    {
        if (ok == 0)
        {
            throw new KernelException($"{what}: {Native.LastError()}");
        }
    }

    internal delegate int ListReader(int* buffer, int cap);

    internal static int[] ReadList(ListReader read, string what)
    {
        var cap = 32;
        while (true)
        {
            var buffer = new int[cap];
            int n;
            fixed (int* p = buffer)
            {
                n = read(p, cap);
            }

            if (n < 0)
            {
                throw new KernelException($"{what}: {Native.LastError()}");
            }

            if (n <= cap)
            {
                return buffer[..n];
            }

            cap = n;
        }
    }
}
