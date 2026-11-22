/*
 * chamfer_kernel: a flat C ABI over Open CASCADE Technology.
 *
 * Conventions
 *  - Every function that can fail returns NULL, a negative number, or 0 for
 *    "false", and records a message readable with chf_last_error() on the
 *    calling thread.
 *  - Handles (chf_shape, chf_op, chf_mesh) are owned by the caller and freed
 *    with the matching *_free function. Functions never take ownership of
 *    handles passed to them.
 *  - Sub-shapes (faces, edges, vertices) are addressed by 1-based index into
 *    the shape's indexed map for that type (TopExp::MapShapes order), which is
 *    stable for a given shape handle.
 *  - Lengths are millimetres, angles radians.
 *  - Strings are UTF-8.
 */
#ifndef CHAMFER_KERNEL_H
#define CHAMFER_KERNEL_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(CHF_BUILDING)
#    define CHF_API __declspec(dllexport)
#  else
#    define CHF_API __declspec(dllimport)
#  endif
#else
#  define CHF_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct chf_shape chf_shape;
typedef struct chf_op chf_op;
typedef struct chf_mesh chf_mesh;

/* Sub-shape kinds; values match TopAbs_ShapeEnum. */
enum chf_kind {
  CHF_COMPOUND = 0, CHF_COMPSOLID = 1, CHF_SOLID = 2, CHF_SHELL = 3,
  CHF_FACE = 4, CHF_WIRE = 5, CHF_EDGE = 6, CHF_VERTEX = 7
};

/* Profile segment kinds for chf_make_face. Each segment uses 8 doubles. */
enum chf_segment {
  CHF_SEG_LINE = 0,   /* x0 y0 x1 y1                                   */
  CHF_SEG_ARC = 1,    /* cx cy x0 y0 x1 y1 ccw(1|0)  (start -> end)     */
  CHF_SEG_CIRCLE = 2, /* cx cy r                                        */
  CHF_SEG_ELLIPSE = 3, /* cx cy major minor angle                        */
  CHF_SEG_BEZIER = 4   /* x0 y0 x1 y1 x2 y2 x3 y3 (cubic, 4 control points) */
};

enum chf_boolean { CHF_FUSE = 0, CHF_CUT = 1, CHF_COMMON = 2 };

enum chf_surface {
  CHF_SURF_PLANE = 0, CHF_SURF_CYLINDER = 1, CHF_SURF_CONE = 2, CHF_SURF_SPHERE = 3,
  CHF_SURF_TORUS = 4, CHF_SURF_BEZIER = 5, CHF_SURF_BSPLINE = 6, CHF_SURF_REVOLUTION = 7,
  CHF_SURF_EXTRUSION = 8, CHF_SURF_OFFSET = 9, CHF_SURF_OTHER = 10
};

enum chf_curve {
  CHF_CURVE_LINE = 0, CHF_CURVE_CIRCLE = 1, CHF_CURVE_ELLIPSE = 2, CHF_CURVE_HYPERBOLA = 3,
  CHF_CURVE_PARABOLA = 4, CHF_CURVE_BEZIER = 5, CHF_CURVE_BSPLINE = 6, CHF_CURVE_OFFSET = 7,
  CHF_CURVE_OTHER = 8
};

/* ---------------------------------------------------------------- errors */
CHF_API const char* chf_last_error(void);
CHF_API const char* chf_version(void);

/* ---------------------------------------------------------------- shapes */
CHF_API void chf_shape_free(chf_shape* s);
CHF_API int chf_shape_kind(const chf_shape* s);
CHF_API int chf_shape_is_null(const chf_shape* s);
CHF_API int chf_shape_count(chf_shape* s, int kind);
/* Returns a new handle to sub-shape `index` of `kind`. */
CHF_API chf_shape* chf_shape_sub(chf_shape* s, int kind, int index);
/* Index of `sub` in `s`'s map of `kind`, or 0 when absent. */
CHF_API int chf_shape_index_of(chf_shape* s, int kind, const chf_shape* sub);
CHF_API int chf_shape_is_valid(const chf_shape* s);
/* out[6] = xmin ymin zmin xmax ymax zmax */
CHF_API int chf_shape_bounds(const chf_shape* s, double* out);
/*
 * out[14] = volume area cx cy cz Ixx Iyy Izz Ixy Ixz Iyz (about the centre of
 * mass, unit density) and 3 reserved zeros.
 */
CHF_API int chf_shape_mass(const chf_shape* s, double* out);
/* Ancestors (kind `of`) of sub-shape (kind, index). Writes up to cap indices; returns the total. */
CHF_API int chf_shape_ancestors(chf_shape* s, int kind, int index, int of, int* out, int cap);
/* Sub-shapes (kind `of`) contained in sub-shape (kind, index). */
CHF_API int chf_shape_children(chf_shape* s, int kind, int index, int of, int* out, int cap);

/*
 * Face info. out[16]:
 *   [0] surface type   [1] area   [2..4] centroid   [5..7] normal at centroid (outward)
 *   [8..10] axis/plane origin   [11..13] axis direction / plane normal   [14] radius   [15] reversed(1|0)
 */
CHF_API int chf_face_info(chf_shape* s, int index, double* out);
/* Plane of a planar face: origin(3) normal(3, outward) xdir(3). Returns 0 if not planar. */
CHF_API int chf_face_plane(chf_shape* s, int index, double* out9);
/* Closest point on face to p: out[6] = point(3) normal(3). Returns distance or -1. */
CHF_API double chf_face_project(chf_shape* s, int index, const double* p, double* out6);
/*
 * Edge info. out[16]:
 *   [0] curve type  [1] length  [2..4] midpoint  [5..7] start  [8..10] end
 *   [11..13] circle centre / line direction  [14] radius  [15] closed(1|0)
 */
CHF_API int chf_edge_info(chf_shape* s, int index, double* out);
/* Samples an edge as a polyline. Writes up to cap points (3 doubles each); returns the total. */
CHF_API int chf_edge_sample(chf_shape* s, int index, double deflection, double* out, int cap);
/* Minimum distance between two shapes; out6 receives the closest points. Returns -1 on failure. */
CHF_API double chf_distance(const chf_shape* a, const chf_shape* b, double* out6);
/*
 * Point-in-solid tests: for each of count points (3 doubles each) writes 1
 * (inside), 0 (outside) or 2 (on the boundary) to out. Returns 1 on success.
 */
CHF_API int chf_classify(const chf_shape* s, int count, const double* points, unsigned char* out);
/* Vertex position. */
CHF_API int chf_vertex_point(chf_shape* s, int index, double* out3);

/* --------------------------------------------------------------- building */
CHF_API chf_shape* chf_make_box(double dx, double dy, double dz);
CHF_API chf_shape* chf_make_cylinder(double radius, double height);
CHF_API chf_shape* chf_make_sphere(double radius);
/*
 * Builds a planar face. plane = origin(3) normal(3) xdir(3). The first loop is
 * the outer boundary; the rest are holes. segData holds 8 doubles per segment
 * in plane coordinates. edgeMap (optional, sized to the total segment count)
 * receives, for each input segment, its 1-based edge index in the returned face.
 */
CHF_API chf_shape* chf_make_face(const double* plane, int loopCount, const int* loopSizes,
                                 const int* segTypes, const double* segData, int* edgeMap);
/* Builds an open or closed wire (no face) from segments, e.g. for sweep paths. */
CHF_API chf_shape* chf_make_wire(const double* plane, int segCount, const int* segTypes,
                                 const double* segData);
/* 3D polyline/spline wire through points (3 doubles each). */
CHF_API chf_shape* chf_make_wire_3d(int pointCount, const double* points, int spline, int closed);
/* A 3D wire of lines (kind 0: start, unused, end) and arcs through three points (kind 1: start, mid, end); 9 doubles each. */
CHF_API chf_shape* chf_make_wire_3d_mixed(int segCount, const int* kinds, const double* data9);
/* Helix wire: pitch, height, radius, axis origin(3) dir(3) xdir(3). */
CHF_API chf_shape* chf_make_helix(double pitch, double height, double radius, const double* axis9, int leftHanded);
CHF_API chf_shape* chf_make_compound(int count, chf_shape* const* shapes);

/* --------------------------------------------------------------- operations
 * Operations return a chf_op that holds the result and its modelling history.
 * Use chf_op_shape to get the result and chf_op_history to map input
 * sub-shapes to result sub-shapes.
 */
CHF_API void chf_op_free(chf_op* op);
CHF_API chf_shape* chf_op_shape(chf_op* op);
/*
 * History of input sub-shape (kind, index of `input`) in the result: which
 * result sub-shapes of kind `of` it became. mode 0 = modified, 1 = generated.
 * Returns the total count (writes up to cap 1-based indices into the result's
 * map), or -1 if the input sub-shape was deleted.
 */
CHF_API int chf_op_history(chf_op* op, chf_shape* input, int kind, int index, int mode, int of, int* out, int cap);

/*
 * Start (which = 0) or end (which = 1) cap faces of an extrude or revolve, as
 * 1-based face indices in the result. Returns the total count.
 */
CHF_API int chf_op_caps(chf_op* op, int which, int* out, int cap);

/* Prism along dir(3). */
CHF_API chf_op* chf_extrude(chf_shape* profile, const double* dir);
/* Revolve about axis origin(3) dir(3) by angle. */
CHF_API chf_op* chf_revolve(chf_shape* profile, const double* axis6, double angle);
CHF_API chf_op* chf_boolean(chf_shape* a, chf_shape* b, int kind);
/* Constant-radius fillet on edges (1-based indices into s). */
CHF_API chf_op* chf_fillet(chf_shape* s, int count, const int* edges, const double* radii);
/* Distance-distance chamfer. face (optional, 0 = any) picks which side d1 is measured on. */
CHF_API chf_op* chf_chamfer(chf_shape* s, int count, const int* edges, const int* faces,
                            const double* d1, const double* d2);
/* Hollow a solid, removing faces. thickness > 0 offsets inward. */
CHF_API chf_op* chf_shell(chf_shape* s, int count, const int* faces, double thickness);
/* Draft faces about a neutral plane (origin(3) normal(3)), pull direction = normal. */
CHF_API chf_op* chf_draft(chf_shape* s, int count, const int* faces, const double* neutral6, double angle);
/* Removes faces (holes, fillets, bosses) and heals the gap from the neighbouring faces. */
CHF_API chf_op* chf_defeature(chf_shape* s, int count, const int* faces);
/* Rigid (or uniformly scaled) transform, m = row-major 3x4. copy != 0 duplicates geometry. */
CHF_API chf_op* chf_transform(chf_shape* s, const double* m12, int copy);
/* Mirror about a plane origin(3) normal(3). */
CHF_API chf_op* chf_mirror(chf_shape* s, const double* plane6, int copy);
/* Sweep a profile (face or wire) along a path wire. mode: 0 = frenet, 1 = corrected frenet. */
CHF_API chf_op* chf_sweep(chf_shape* profile, chf_shape* path, int mode);
/* Loft through sections (wires or faces). solid != 0 builds a closed solid. */
CHF_API chf_op* chf_loft(int count, chf_shape* const* sections, int solid, int ruled);
/*
 * Rib: grows an open wire lying in a plane (origin(3) normal(3)) into the base
 * solid, thickness1 along the normal and thickness2 against it, until it meets
 * the base. The result is the base with the rib fused on.
 */
CHF_API chf_op* chf_rib(chf_shape* base, chf_shape* wire, const double* plane6, double thickness1, double thickness2);
/* Offset a face/shell into a solid of given thickness. */
CHF_API chf_op* chf_thicken(chf_shape* s, double thickness);
/* Offsets a face, shell or solid's surfaces by a distance (positive = along the normals). */
CHF_API chf_op* chf_offset_shape(chf_shape* s, double offset);
/* A smooth patch filling a closed wire (n-sided boundary surface). */
CHF_API chf_shape* chf_fill(chf_shape* wire);
/* Sew faces/shells into a shell or solid. */
CHF_API chf_op* chf_sew(int count, chf_shape* const* shapes, double tolerance);
/* Split a shape by a tool shape (e.g. a plane face). */
CHF_API chf_op* chf_split(chf_shape* s, chf_shape* tool);
/* Planar section of a shape: plane = origin(3) normal(3). */
CHF_API chf_shape* chf_section(chf_shape* s, const double* plane6);
CHF_API chf_shape* chf_make_plane_face(const double* plane9, double size);
/* Unify same-domain faces and edges (cleans boolean seams). */
CHF_API chf_op* chf_unify(chf_shape* s);

/* ----------------------------------------------------------- tessellation */
CHF_API chf_mesh* chf_tessellate(chf_shape* s, double linearDeflection, double angularDeflection);
CHF_API void chf_mesh_free(chf_mesh* m);
/* out[4] = vertexCount triangleCount edgePointCount edgeCount */
CHF_API void chf_mesh_counts(const chf_mesh* m, int* out4);
/*
 * Copies the mesh out. positions/normals: 3 floats per vertex. triangles: 3 ints
 * per triangle (0-based vertex indices, CCW seen from outside). triangleFaces:
 * 1-based face index per triangle. edgePoints: 3 floats per point.
 * edgeOffsets: edgeCount+1 entries; edge i (1-based index i+1) owns points
 * [edgeOffsets[i], edgeOffsets[i+1]).
 */
CHF_API void chf_mesh_copy(const chf_mesh* m, float* positions, float* normals, int* triangles,
                           int* triangleFaces, float* edgePoints, int* edgeOffsets);

/* ---------------------------------------------------------- drawings (HLR) */
typedef struct chf_lines chf_lines;
/*
 * Hidden-line projection for a drawing view. axis9 = origin(3), direction
 * towards the viewer(3), view X direction(3). Produces 2D polylines in view
 * coordinates, each tagged: 0 visible sharp edge, 1 visible tangent edge,
 * 2 visible silhouette, 3 hidden edge, 4 hidden silhouette.
 */
CHF_API chf_lines* chf_hlr(chf_shape* s, const double* axis9, double deflection);
CHF_API void chf_lines_free(chf_lines* l);
/* out2 = pointCount lineCount */
CHF_API void chf_lines_counts(const chf_lines* l, int* out2);
/* points: 2 doubles per point; offsets: lineCount+1; kinds: lineCount. */
CHF_API void chf_lines_copy(const chf_lines* l, double* points, int* offsets, int* kinds);

/* -------------------------------------------------------------- healing */
/*
 * Diagnostics: out7 = invalid faces, invalid edges, free edges (gaps or open
 * boundaries), edges shorter than tolerance, solids, shells, faces.
 */
CHF_API int chf_diagnose(const chf_shape* s, double tolerance, int* out7);
/* Sews gaps within tolerance, fixes wires, faces and shells, and makes closed shells solid. */
CHF_API chf_shape* chf_heal(const chf_shape* s, double tolerance);

/* ------------------------------------------------------------------- I/O */
/*
 * STEP with product structure, names and colours. parts: the distinct part shapes with names and
 * 0xRRGGBB colours; instances: part index, placement (12 doubles, row-major 3x4) and name each.
 */
CHF_API int chf_write_step_assembly(int partCount, chf_shape* const* parts, const char* const* partNames, const unsigned* partColors,
                                    int instanceCount, const int* instancePart, const double* matrices12, const char* const* instanceNames,
                                    const char* assemblyName, const char* path);

/* Reads a STEP file's product structure, flattened: distinct parts (shape, name, colour or 0xFFFFFFFF)
 * and placed instances (part index, row-major 3x4 placement, name). */
typedef struct chf_xcaf chf_xcaf;
CHF_API chf_xcaf* chf_xcaf_read_step(const char* path);
CHF_API void chf_xcaf_free(chf_xcaf* x);
/* out2 = partCount instanceCount */
CHF_API void chf_xcaf_counts(const chf_xcaf* x, int* out2);
CHF_API chf_shape* chf_xcaf_part(const chf_xcaf* x, int i, unsigned* color);
CHF_API const char* chf_xcaf_part_name(const chf_xcaf* x, int i);
/* Returns the instance's part index and writes its placement. */
CHF_API int chf_xcaf_instance(const chf_xcaf* x, int i, double* m12);
CHF_API const char* chf_xcaf_instance_name(const chf_xcaf* x, int i);
CHF_API int chf_write_step(const chf_shape* s, const char* path);
CHF_API chf_shape* chf_read_step(const char* path);
CHF_API int chf_write_iges(const chf_shape* s, const char* path);
CHF_API chf_shape* chf_read_iges(const char* path);
CHF_API int chf_write_stl(const chf_shape* s, const char* path, double deflection, int ascii);
CHF_API int chf_write_brep(const chf_shape* s, const char* path);
CHF_API chf_shape* chf_read_brep(const char* path);
/* BRep text round trip in memory. chf_to_brep returns a malloc'd string the caller frees with chf_free_string. */
CHF_API char* chf_to_brep(const chf_shape* s);
CHF_API chf_shape* chf_from_brep(const char* text);
CHF_API void chf_free_string(char* str);

#ifdef __cplusplus
}
#endif

#endif /* CHAMFER_KERNEL_H */
