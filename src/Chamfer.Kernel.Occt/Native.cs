using System.Runtime.InteropServices;

namespace Chamfer.Kernel.Occt;

/// <summary>Raw P/Invoke declarations for chamfer_kernel.h. Use <see cref="Shape"/> and friends instead.</summary>
internal static unsafe partial class Native
{
    private const string Lib = "chamfer_kernel";

    [LibraryImport(Lib, EntryPoint = "chf_last_error")]
    private static partial IntPtr LastErrorPtr();

    [LibraryImport(Lib, EntryPoint = "chf_version")]
    private static partial IntPtr VersionPtr();

    public static string LastError() => Marshal.PtrToStringUTF8(LastErrorPtr()) ?? string.Empty;
    public static string Version() => Marshal.PtrToStringUTF8(VersionPtr()) ?? string.Empty;

    // ---------------------------------------------------------------- shapes
    [LibraryImport(Lib)] public static partial void chf_shape_free(IntPtr s);
    [LibraryImport(Lib)] public static partial int chf_shape_kind(ShapeHandle s);
    [LibraryImport(Lib)] public static partial int chf_shape_count(ShapeHandle s, int kind);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_shape_sub(ShapeHandle s, int kind, int index);
    [LibraryImport(Lib)] public static partial int chf_shape_index_of(ShapeHandle s, int kind, ShapeHandle sub);
    [LibraryImport(Lib)] public static partial int chf_shape_is_valid(ShapeHandle s);
    [LibraryImport(Lib)] public static partial int chf_shape_bounds(ShapeHandle s, double* out6);
    [LibraryImport(Lib)] public static partial int chf_shape_mass(ShapeHandle s, double* out14);
    [LibraryImport(Lib)] public static partial int chf_shape_ancestors(ShapeHandle s, int kind, int index, int of, int* output, int cap);
    [LibraryImport(Lib)] public static partial int chf_shape_children(ShapeHandle s, int kind, int index, int of, int* output, int cap);
    [LibraryImport(Lib)] public static partial int chf_face_info(ShapeHandle s, int index, double* out16);
    [LibraryImport(Lib)] public static partial int chf_face_plane(ShapeHandle s, int index, double* out9);
    [LibraryImport(Lib)] public static partial double chf_face_project(ShapeHandle s, int index, double* p, double* out6);
    [LibraryImport(Lib)] public static partial int chf_edge_info(ShapeHandle s, int index, double* out16);
    [LibraryImport(Lib)] public static partial int chf_edge_sample(ShapeHandle s, int index, double deflection, double* output, int cap);
    [LibraryImport(Lib)] public static partial double chf_distance(ShapeHandle a, ShapeHandle b, double* out6);
    [LibraryImport(Lib)] public static partial int chf_vertex_point(ShapeHandle s, int index, double* out3);

    // --------------------------------------------------------------- building
    [LibraryImport(Lib)] public static partial ShapeHandle chf_make_box(double dx, double dy, double dz);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_make_cylinder(double radius, double height);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_make_sphere(double radius);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_make_face(double* plane, int loopCount, int* loopSizes, int* segTypes, double* segData, int* edgeMap);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_make_wire(double* plane, int segCount, int* segTypes, double* segData);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_make_wire_3d(int pointCount, double* points, int spline, int closed);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_make_helix(double pitch, double height, double radius, double* axis9, int leftHanded);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_make_compound(int count, IntPtr* shapes);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_make_plane_face(double* plane9, double size);

    // ------------------------------------------------------------- operations
    [LibraryImport(Lib)] public static partial void chf_op_free(IntPtr op);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_op_shape(OpHandle op);
    [LibraryImport(Lib)] public static partial int chf_op_history(OpHandle op, ShapeHandle input, int kind, int index, int mode, int of, int* output, int cap);
    [LibraryImport(Lib)] public static partial int chf_op_caps(OpHandle op, int which, int* output, int cap);
    [LibraryImport(Lib)] public static partial OpHandle chf_extrude(ShapeHandle profile, double* dir);
    [LibraryImport(Lib)] public static partial OpHandle chf_revolve(ShapeHandle profile, double* axis6, double angle);
    [LibraryImport(Lib)] public static partial OpHandle chf_boolean(ShapeHandle a, ShapeHandle b, int kind);
    [LibraryImport(Lib)] public static partial OpHandle chf_fillet(ShapeHandle s, int count, int* edges, double* radii);
    [LibraryImport(Lib)] public static partial OpHandle chf_chamfer(ShapeHandle s, int count, int* edges, int* faces, double* d1, double* d2);
    [LibraryImport(Lib)] public static partial OpHandle chf_shell(ShapeHandle s, int count, int* faces, double thickness);
    [LibraryImport(Lib)] public static partial OpHandle chf_draft(ShapeHandle s, int count, int* faces, double* neutral6, double angle);
    [LibraryImport(Lib)] public static partial OpHandle chf_transform(ShapeHandle s, double* m12, int copy);
    [LibraryImport(Lib)] public static partial OpHandle chf_mirror(ShapeHandle s, double* plane6, int copy);
    [LibraryImport(Lib)] public static partial OpHandle chf_sweep(ShapeHandle profile, ShapeHandle path, int mode);
    [LibraryImport(Lib)] public static partial OpHandle chf_loft(int count, IntPtr* sections, int solid, int ruled);
    [LibraryImport(Lib)] public static partial OpHandle chf_rib(ShapeHandle baseShape, ShapeHandle wire, double* plane6, double thickness1, double thickness2);
    [LibraryImport(Lib)] public static partial OpHandle chf_thicken(ShapeHandle s, double thickness);
    [LibraryImport(Lib)] public static partial OpHandle chf_sew(int count, IntPtr* shapes, double tolerance);
    [LibraryImport(Lib)] public static partial OpHandle chf_split(ShapeHandle s, ShapeHandle tool);
    [LibraryImport(Lib)] public static partial ShapeHandle chf_section(ShapeHandle s, double* plane6);
    [LibraryImport(Lib)] public static partial OpHandle chf_unify(ShapeHandle s);

    // ----------------------------------------------------------- tessellation
    [LibraryImport(Lib)] public static partial IntPtr chf_tessellate(ShapeHandle s, double linearDeflection, double angularDeflection);
    [LibraryImport(Lib)] public static partial void chf_mesh_free(IntPtr m);
    [LibraryImport(Lib)] public static partial void chf_mesh_counts(IntPtr m, int* out4);
    [LibraryImport(Lib)] public static partial void chf_mesh_copy(IntPtr m, float* positions, float* normals, int* triangles, int* triangleFaces, float* edgePoints, int* edgeOffsets);

    // --------------------------------------------------------------- drawings
    [LibraryImport(Lib)] public static partial IntPtr chf_hlr(ShapeHandle s, double* axis9, double deflection);
    [LibraryImport(Lib)] public static partial void chf_lines_free(IntPtr l);
    [LibraryImport(Lib)] public static partial void chf_lines_counts(IntPtr l, int* out2);
    [LibraryImport(Lib)] public static partial void chf_lines_copy(IntPtr l, double* points, int* offsets, int* kinds);

    // -------------------------------------------------------------------- I/O
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int chf_write_step(ShapeHandle s, string path);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial ShapeHandle chf_read_step(string path);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int chf_write_iges(ShapeHandle s, string path);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial ShapeHandle chf_read_iges(string path);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int chf_write_stl(ShapeHandle s, string path, double deflection, int ascii);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int chf_write_brep(ShapeHandle s, string path);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial ShapeHandle chf_read_brep(string path);
    [LibraryImport(Lib)] public static partial IntPtr chf_to_brep(ShapeHandle s);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial ShapeHandle chf_from_brep(string text);
    [LibraryImport(Lib)] public static partial void chf_free_string(IntPtr str);
}

/// <summary>Owns a native chf_shape.</summary>
internal sealed class ShapeHandle : SafeHandle
{
    public ShapeHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        Native.chf_shape_free(handle);
        return true;
    }
}

/// <summary>Owns a native chf_op.</summary>
internal sealed class OpHandle : SafeHandle
{
    public OpHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        Native.chf_op_free(handle);
        return true;
    }
}
