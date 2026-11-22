using System.Runtime.InteropServices;
using Chamfer.Geometry;

namespace Chamfer.Kernel.Occt;

/// <summary>Shape construction and modelling operations.</summary>
public static unsafe class Modeling
{
    public static Shape Box(double dx, double dy, double dz) => Shape.Wrap(Native.chf_make_box(dx, dy, dz), "box");
    public static Shape Cylinder(double radius, double height) => Shape.Wrap(Native.chf_make_cylinder(radius, height), "cylinder");
    public static Shape Sphere(double radius) => Shape.Wrap(Native.chf_make_sphere(radius), "sphere");

    /// <summary>
    /// Builds a planar face from closed loops; the first loop is the outer
    /// boundary, the rest are holes. Each loop's segments must be in order and
    /// joined end to end. <paramref name="edgeMap"/> receives, for every input
    /// segment in order, the 1-based index of its edge in the face.
    /// </summary>
    public static Shape Face(Plane3 plane, IReadOnlyList<IReadOnlyList<ProfileSegment>> loops, out int[] edgeMap)
    {
        ArgumentNullException.ThrowIfNull(loops);
        var total = loops.Sum(l => l.Count);
        var sizes = loops.Select(l => l.Count).ToArray();
        var segs = loops.SelectMany(l => l).ToArray();
        Pack(segs, out var types, out var data);
        var pl = plane.ToArray();
        edgeMap = new int[total];
        fixed (double* pp = pl, pd = data)
        fixed (int* ps = sizes, pt = types, pm = edgeMap)
        {
            return Shape.Wrap(Native.chf_make_face(pp, loops.Count, ps, pt, pd, pm), "profile face");
        }
    }

    public static Shape Face(Plane3 plane, IReadOnlyList<IReadOnlyList<ProfileSegment>> loops) => Face(plane, loops, out _);

    /// <summary>A wire through segments in a plane (open or closed).</summary>
    public static Shape Wire(Plane3 plane, IReadOnlyList<ProfileSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Pack([.. segments], out var types, out var data);
        var pl = plane.ToArray();
        fixed (double* pp = pl, pd = data)
        fixed (int* pt = types)
        {
            return Shape.Wrap(Native.chf_make_wire(pp, segments.Count, pt, pd), "wire");
        }
    }

    /// <summary>A polyline or interpolated spline through 3D points.</summary>
    public static Shape Wire3D(IReadOnlyList<Vec3> points, bool spline, bool closed)
    {
        ArgumentNullException.ThrowIfNull(points);
        var data = points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        fixed (double* pd = data)
        {
            return Shape.Wrap(Native.chf_make_wire_3d(points.Count, pd, spline ? 1 : 0, closed ? 1 : 0), "3D wire");
        }
    }

    /// <summary>A 3D path of straight lines (Mid is ignored) and arcs through three points, joined end to end.</summary>
    public static Shape Path3D(IReadOnlyList<(bool Arc, Vec3 Start, Vec3 Mid, Vec3 End)> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var kinds = segments.Select(s => s.Arc ? 1 : 0).ToArray();
        var data = segments.SelectMany(s => new[] { s.Start.X, s.Start.Y, s.Start.Z, s.Mid.X, s.Mid.Y, s.Mid.Z, s.End.X, s.End.Y, s.End.Z }).ToArray();
        fixed (int* pk = kinds)
        fixed (double* pd = data)
        {
            return Shape.Wrap(Native.chf_make_wire_3d_mixed(kinds.Length, pk, pd), "3D path");
        }
    }

    /// <summary>A helix around the plane's normal starting on its X axis.</summary>
    public static Shape Helix(Plane3 axis, double pitch, double height, double radius, bool leftHanded = false)
    {
        var a = axis.ToArray();
        fixed (double* pa = a)
        {
            return Shape.Wrap(Native.chf_make_helix(pitch, height, radius, pa, leftHanded ? 1 : 0), "helix");
        }
    }

    public static Shape Compound(IReadOnlyList<Shape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        return WithHandles(shapes, (n, p) => Shape.Wrap(Native.chf_make_compound(n, p), "compound"));
    }

    /// <summary>A square planar face of half-size <paramref name="size"/>, for splitting and sectioning.</summary>
    public static Shape PlaneFace(Plane3 plane, double size)
    {
        var a = plane.ToArray();
        fixed (double* pa = a)
        {
            return Shape.Wrap(Native.chf_make_plane_face(pa, size), "plane face");
        }
    }

    // ------------------------------------------------------------- operations

    public static Operation Extrude(Shape profile, Vec3 direction)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var d = stackalloc double[3] { direction.X, direction.Y, direction.Z };
        return new Operation(Native.chf_extrude(profile.Handle, d), "extrude");
    }

    public static Operation Revolve(Shape profile, Vec3 axisOrigin, Vec3 axisDirection, double angle)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var a = stackalloc double[6] { axisOrigin.X, axisOrigin.Y, axisOrigin.Z, axisDirection.X, axisDirection.Y, axisDirection.Z };
        return new Operation(Native.chf_revolve(profile.Handle, a, angle), "revolve");
    }

    public static Operation Boolean(Shape a, Shape b, BooleanKind kind)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return new Operation(Native.chf_boolean(a.Handle, b.Handle, (int)kind), kind.ToString().ToLowerInvariant());
    }

    public static Operation Fillet(Shape s, IReadOnlyList<int> edges, IReadOnlyList<double> radii)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(radii);
        var e = edges.ToArray();
        var r = radii.ToArray();
        fixed (int* pe = e)
        fixed (double* pr = r)
        {
            return new Operation(Native.chf_fillet(s.Handle, e.Length, pe, pr), "fillet");
        }
    }

    /// <summary>Variable-radius fillet: each edge's radius runs linearly from <paramref name="start"/> (at its first vertex) to <paramref name="end"/>.</summary>
    public static Operation FilletVariable(Shape s, IReadOnlyList<int> edges, IReadOnlyList<double> start, IReadOnlyList<double> end)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        var e = edges.ToArray();
        var a = start.ToArray();
        var b = end.ToArray();
        fixed (int* pe = e)
        fixed (double* pa = a)
        fixed (double* pb = b)
        {
            return new Operation(Native.chf_fillet_variable(s.Handle, e.Length, pe, pa, pb), "fillet");
        }
    }

    /// <summary>Distance-distance chamfer; <paramref name="faces"/> (0 = any) picks the face <paramref name="d1"/> is measured on.</summary>
    public static Operation Chamfer(Shape s, IReadOnlyList<int> edges, IReadOnlyList<int>? faces, IReadOnlyList<double> d1, IReadOnlyList<double> d2)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(d1);
        ArgumentNullException.ThrowIfNull(d2);
        var e = edges.ToArray();
        var f = faces?.ToArray() ?? new int[e.Length];
        var a = d1.ToArray();
        var b = d2.ToArray();
        fixed (int* pe = e, pf = f)
        fixed (double* pa = a, pb = b)
        {
            return new Operation(Native.chf_chamfer(s.Handle, e.Length, pe, pf, pa, pb), "chamfer");
        }
    }

    /// <summary>Hollows a solid inward by <paramref name="thickness"/>, removing <paramref name="openFaces"/>.</summary>
    public static Operation Shell(Shape s, IReadOnlyList<int> openFaces, double thickness)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(openFaces);
        var f = openFaces.ToArray();
        fixed (int* pf = f)
        {
            return new Operation(Native.chf_shell(s.Handle, f.Length, pf, thickness), "shell");
        }
    }

    /// <summary>Tapers faces by <paramref name="angle"/> about a neutral plane whose normal is the pull direction.</summary>
    public static Operation Draft(Shape s, IReadOnlyList<int> faces, Vec3 neutralOrigin, Vec3 pullDirection, double angle)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(faces);
        var f = faces.ToArray();
        var n = stackalloc double[6] { neutralOrigin.X, neutralOrigin.Y, neutralOrigin.Z, pullDirection.X, pullDirection.Y, pullDirection.Z };
        fixed (int* pf = f)
        {
            return new Operation(Native.chf_draft(s.Handle, f.Length, pf, n, angle), "draft");
        }
    }

    /// <summary>Removes faces (holes, fillets, bosses), healing the gap from the faces around them.</summary>
    public static Operation Defeature(Shape s, IReadOnlyList<int> faces)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(faces);
        var f = faces.ToArray();
        fixed (int* pf = f)
        {
            return new Operation(Native.chf_defeature(s.Handle, f.Length, pf), "defeature");
        }
    }

    /// <summary>
    /// Reshapes a face (1-based) into a smooth patch on its own boundary that passes through
    /// <paramref name="points"/>, sewn back into the solid. Face history isn't kept.
    /// </summary>
    public static Operation Freeform(Shape s, int face, IReadOnlyList<Vec3> points)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(points);
        var xyz = points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        fixed (double* px = xyz)
        {
            return new Operation(Native.chf_freeform(s.Handle, face, points.Count, px), "freeform");
        }
    }

    /// <summary>Applies a rigid transform. Use <see cref="Mirror"/> for reflections.</summary>
    public static Operation Transform(Shape s, Transform3 t, bool copy = true)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!t.IsUsable)
        {
            // Degenerate matrices can send the kernel into unbounded recursion; refuse them here.
            throw new KernelException("The transform has invalid values.");
        }

        var m = t.ToArray();
        fixed (double* pm = m)
        {
            return new Operation(Native.chf_transform(s.Handle, pm, copy ? 1 : 0), "transform");
        }
    }

    public static Operation Mirror(Shape s, Vec3 planeOrigin, Vec3 planeNormal, bool copy = true)
    {
        ArgumentNullException.ThrowIfNull(s);
        var p = stackalloc double[6] { planeOrigin.X, planeOrigin.Y, planeOrigin.Z, planeNormal.X, planeNormal.Y, planeNormal.Z };
        return new Operation(Native.chf_mirror(s.Handle, p, copy ? 1 : 0), "mirror");
    }

    public static Operation Sweep(Shape profile, Shape path, bool correctedFrenet = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(path);
        return new Operation(Native.chf_sweep(profile.Handle, path.Handle, correctedFrenet ? 1 : 0), "sweep");
    }

    public static Operation Loft(IReadOnlyList<Shape> sections, bool solid = true, bool ruled = false)
    {
        ArgumentNullException.ThrowIfNull(sections);
        return WithHandles(sections, (n, p) => new Operation(Native.chf_loft(n, p, solid ? 1 : 0, ruled ? 1 : 0), "loft"));
    }

    /// <summary>
    /// Grows an open wire lying in a plane into <paramref name="baseShape"/>,
    /// <paramref name="thickness1"/> along the plane normal and <paramref name="thickness2"/>
    /// against it, until it meets the base. The result includes the base.
    /// </summary>
    public static Operation Rib(Shape baseShape, Shape wire, Vec3 planeOrigin, Vec3 planeNormal, double thickness1, double thickness2)
    {
        ArgumentNullException.ThrowIfNull(baseShape);
        ArgumentNullException.ThrowIfNull(wire);
        var p = stackalloc double[6] { planeOrigin.X, planeOrigin.Y, planeOrigin.Z, planeNormal.X, planeNormal.Y, planeNormal.Z };
        return new Operation(Native.chf_rib(baseShape.Handle, wire.Handle, p, thickness1, thickness2), "rib");
    }

    public static Operation Thicken(Shape s, double thickness)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new Operation(Native.chf_thicken(s.Handle, thickness), "thicken");
    }

    /// <summary>Offsets surfaces by a distance along their normals.</summary>
    public static Operation OffsetShape(Shape s, double offset)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new Operation(Native.chf_offset_shape(s.Handle, offset), "offset surface");
    }

    /// <summary>A smooth surface patch bounded by a closed wire.</summary>
    public static Shape Fill(Shape wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return Shape.Wrap(Native.chf_fill(wire.Handle), "fill");
    }

    public static Operation Sew(IReadOnlyList<Shape> shapes, double tolerance = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        return WithHandles(shapes, (n, p) => new Operation(Native.chf_sew(n, p, tolerance), "sew"));
    }

    public static Operation Split(Shape s, Shape tool)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(tool);
        return new Operation(Native.chf_split(s.Handle, tool.Handle), "split");
    }

    /// <summary>
    /// Writes a STEP file with product structure: each part once (named and coloured 0xRRGGBB),
    /// placed by named instances.
    /// </summary>
    public static void WriteStepAssembly(IReadOnlyList<(Shape Shape, string Name, uint Color)> parts,
        IReadOnlyList<(int Part, Transform3 Placement, string Name)> instances, string assemblyName, string path)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(path);
        if (instances.Any(i => !i.Placement.IsUsable || i.Part < 0 || i.Part >= parts.Count))
        {
            throw new KernelException("An instance has an invalid placement or part.");
        }

        var strings = new List<IntPtr>();
        IntPtr Utf8(string s)
        {
            var p = Marshal.StringToCoTaskMemUTF8(s);
            strings.Add(p);
            return p;
        }

        try
        {
            var partNames = parts.Select(p => Utf8(p.Name)).ToArray();
            var colors = parts.Select(p => p.Color).ToArray();
            var which = instances.Select(i => i.Part).ToArray();
            var matrices = instances.SelectMany(i => i.Placement.ToArray()).ToArray();
            var instanceNames = instances.Select(i => Utf8(i.Name)).ToArray();
            var ok = WithHandles([.. parts.Select(p => p.Shape)], (n, handles) =>
            {
                fixed (IntPtr* pn = partNames)
                fixed (uint* pc = colors)
                fixed (int* pw = which)
                fixed (double* pm = matrices)
                fixed (IntPtr* inames = instanceNames)
                {
                    return Native.chf_write_step_assembly(n, handles, pn, pc, which.Length, pw, pm, inames, assemblyName, path);
                }
            });
            if (ok == 0)
            {
                throw new KernelException($"STEP export failed: {Native.LastError()}");
            }
        }
        finally
        {
            foreach (var p in strings)
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    /// <summary>
    /// Reads a STEP file's product structure, flattened to distinct parts (with names and colours,
    /// null when the file has none) and placed instances.
    /// </summary>
    public static (IReadOnlyList<(Shape Shape, string Name, uint? Color)> Parts, IReadOnlyList<(int Part, Transform3 Placement, string Name)> Instances)
        ReadStepAssembly(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var x = Native.chf_xcaf_read_step(path);
        if (x == IntPtr.Zero)
        {
            throw new KernelException($"STEP import failed: {Native.LastError()}");
        }

        try
        {
            var counts = stackalloc int[2];
            Native.chf_xcaf_counts(x, counts);
            var parts = new List<(Shape, string, uint?)>();
            for (var i = 0; i < counts[0]; i++)
            {
                uint color;
                var shape = Shape.Wrap(Native.chf_xcaf_part(x, i, &color), "STEP part");
                parts.Add((shape, Marshal.PtrToStringUTF8(Native.chf_xcaf_part_name(x, i)) ?? $"Part{i + 1}", color == 0xFFFFFFFFu ? null : color));
            }

            var instances = new List<(int, Transform3, string)>();
            var m = stackalloc double[12];
            for (var i = 0; i < counts[1]; i++)
            {
                var part = Native.chf_xcaf_instance(x, i, m);
                var t = new Transform3(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11]);
                instances.Add((part, t, Marshal.PtrToStringUTF8(Native.chf_xcaf_instance_name(x, i)) ?? parts[part].Item2));
            }

            return (parts, instances);
        }
        finally
        {
            Native.chf_xcaf_free(x);
        }
    }

    /// <summary>Intersection curves of a shape with a plane.</summary>
    public static Shape Section(Shape s, Vec3 planeOrigin, Vec3 planeNormal)
    {
        ArgumentNullException.ThrowIfNull(s);
        var p = stackalloc double[6] { planeOrigin.X, planeOrigin.Y, planeOrigin.Z, planeNormal.X, planeNormal.Y, planeNormal.Z };
        return Shape.Wrap(Native.chf_section(s.Handle, p), "section");
    }

    /// <summary>Merges coplanar faces and collinear edges.</summary>
    public static Operation Unify(Shape s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new Operation(Native.chf_unify(s.Handle), "unify");
    }

    // ---------------------------------------------------------------- helpers

    private static void Pack(ProfileSegment[] segs, out int[] types, out double[] data)
    {
        types = new int[segs.Length];
        data = new double[segs.Length * 8];
        for (var i = 0; i < segs.Length; i++)
        {
            var s = segs[i];
            types[i] = (int)s.Kind;
            var o = i * 8;
            data[o] = s.A;
            data[o + 1] = s.B;
            data[o + 2] = s.C;
            data[o + 3] = s.D;
            data[o + 4] = s.E;
            data[o + 5] = s.F;
            data[o + 6] = s.G;
            data[o + 7] = s.H;
        }
    }

    private delegate T HandlesCall<out T>(int count, IntPtr* handles);

    private static T WithHandles<T>(IReadOnlyList<Shape> shapes, HandlesCall<T> call)
    {
        var refs = new bool[shapes.Count];
        var ptrs = new IntPtr[shapes.Count];
        try
        {
            for (var i = 0; i < shapes.Count; i++)
            {
                shapes[i].Handle.DangerousAddRef(ref refs[i]);
                ptrs[i] = shapes[i].Handle.DangerousGetHandle();
            }

            fixed (IntPtr* p = ptrs)
            {
                return call(ptrs.Length, p);
            }
        }
        finally
        {
            for (var i = 0; i < shapes.Count; i++)
            {
                if (refs[i])
                {
                    shapes[i].Handle.DangerousRelease();
                }
            }
        }
    }
}
