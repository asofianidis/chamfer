// Shape lifetime, topology and geometric queries.
#include "internal.h"

#include <BRepAdaptor_Curve.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepBndLib.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepExtrema_DistShapeShape.hxx>
#include <BRepGProp.hxx>
#include <BRepGProp_Face.hxx>
#include <BRepLProp_SLProps.hxx>
#include <BRepTools.hxx>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <GCPnts_AbscissaPoint.hxx>
#include <GCPnts_TangentialDeflection.hxx>
#include <GProp_GProps.hxx>
#include <Geom_Surface.hxx>
#include <GeomAPI_ProjectPointOnSurf.hxx>
#include <ShapeAnalysis_Surface.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Face.hxx>
#include <gp_Pln.hxx>

#include <algorithm>
#include <vector>

namespace {

void put3(double* out, const gp_XYZ& v) {
  out[0] = v.X();
  out[1] = v.Y();
  out[2] = v.Z();
}

int writeList(const std::vector<int>& items, int* out, int cap) {
  int n = static_cast<int>(items.size());
  for (int i = 0; i < n && i < cap; ++i) out[i] = items[i];
  return n;
}

bool validIndex(chf_shape* s, int kind, int index) {
  if (!s || index < 1 || index > s->map(kind).Extent()) {
    chf_set_error("sub-shape index out of range");
    return false;
  }
  return true;
}

// Outward normal of a face at parameter (u, v).
bool faceNormal(const TopoDS_Face& face, double u, double v, gp_Pnt& p, gp_Dir& n) {
  BRepAdaptor_Surface surf(face);
  BRepLProp_SLProps props(surf, u, v, 1, 1e-7);
  p = props.Value();
  if (!props.IsNormalDefined()) return false;
  n = props.Normal();
  if (face.Orientation() == TopAbs_REVERSED) n.Reverse();
  return true;
}

}  // namespace

extern "C" {

void chf_shape_free(chf_shape* s) { delete s; }

int chf_shape_kind(const chf_shape* s) {
  return s && !s->shape.IsNull() ? static_cast<int>(s->shape.ShapeType()) : -1;
}

int chf_shape_is_null(const chf_shape* s) { return !s || s->shape.IsNull(); }

int chf_shape_count(chf_shape* s, int kind) {
  return chf_guard(-1, [&] { return s ? s->map(kind).Extent() : -1; });
}

chf_shape* chf_shape_sub(chf_shape* s, int kind, int index) {
  return chf_guard<chf_shape*>(nullptr, [&]() -> chf_shape* {
    if (!validIndex(s, kind, index)) return nullptr;
    return new chf_shape(s->map(kind).FindKey(index));
  });
}

int chf_shape_index_of(chf_shape* s, int kind, const chf_shape* sub) {
  return chf_guard(0, [&] { return s && sub ? s->map(kind).FindIndex(sub->shape) : 0; });
}

int chf_shape_is_valid(const chf_shape* s) {
  return chf_guard(0, [&] {
    if (!s || s->shape.IsNull()) return 0;
    BRepCheck_Analyzer check(s->shape);
    return check.IsValid() ? 1 : 0;
  });
}

int chf_shape_bounds(const chf_shape* s, double* out) {
  return chf_guard(0, [&] {
    if (!s || s->shape.IsNull()) return 0;
    Bnd_Box box;
    BRepBndLib::AddOptimal(s->shape, box, false, false);
    if (box.IsVoid()) return 0;
    box.Get(out[0], out[1], out[2], out[3], out[4], out[5]);
    return 1;
  });
}

int chf_shape_mass(const chf_shape* s, double* out) {
  return chf_guard(0, [&] {
    if (!s || s->shape.IsNull()) return 0;
    std::fill(out, out + 14, 0.0);
    GProp_GProps surface;
    BRepGProp::SurfaceProperties(s->shape, surface);
    out[1] = surface.Mass();

    GProp_GProps volume;
    BRepGProp::VolumeProperties(s->shape, volume);
    out[0] = volume.Mass();
    const GProp_GProps& props = std::abs(volume.Mass()) > 1e-12 ? volume : surface;
    put3(out + 2, props.CentreOfMass().XYZ());
    gp_Mat m = props.MatrixOfInertia();  // about the centre of mass
    out[5] = m(1, 1);
    out[6] = m(2, 2);
    out[7] = m(3, 3);
    out[8] = m(1, 2);
    out[9] = m(1, 3);
    out[10] = m(2, 3);
    return 1;
  });
}

int chf_shape_ancestors(chf_shape* s, int kind, int index, int of, int* out, int cap) {
  return chf_guard(-1, [&] {
    if (!validIndex(s, kind, index)) return -1;
    TopTools_IndexedDataMapOfShapeListOfShape anc;
    TopExp::MapShapesAndUniqueAncestors(s->shape, static_cast<TopAbs_ShapeEnum>(kind),
                                        static_cast<TopAbs_ShapeEnum>(of), anc);
    const TopoDS_Shape& sub = s->map(kind).FindKey(index);
    std::vector<int> result;
    if (const TopTools_ListOfShape* list = anc.Seek(sub)) {
      const auto& ofMap = s->map(of);
      for (const TopoDS_Shape& a : *list) {
        int i = ofMap.FindIndex(a);
        if (i > 0 && std::find(result.begin(), result.end(), i) == result.end()) result.push_back(i);
      }
    }
    return writeList(result, out, cap);
  });
}

int chf_shape_children(chf_shape* s, int kind, int index, int of, int* out, int cap) {
  return chf_guard(-1, [&] {
    if (!validIndex(s, kind, index)) return -1;
    const TopoDS_Shape& sub = s->map(kind).FindKey(index);
    const auto& ofMap = s->map(of);
    std::vector<int> result;
    TopTools_IndexedMapOfShape seen;
    for (TopExp_Explorer ex(sub, static_cast<TopAbs_ShapeEnum>(of)); ex.More(); ex.Next()) {
      int before = seen.Extent();
      if (seen.Add(ex.Current()) > before) {
        int i = ofMap.FindIndex(ex.Current());
        if (i > 0) result.push_back(i);
      }
    }
    return writeList(result, out, cap);
  });
}

int chf_face_info(chf_shape* s, int index, double* out) {
  return chf_guard(0, [&] {
    if (!validIndex(s, CHF_FACE, index)) return 0;
    std::fill(out, out + 16, 0.0);
    TopoDS_Face face = TopoDS::Face(s->map(CHF_FACE).FindKey(index));
    BRepAdaptor_Surface surf(face, false);
    out[0] = static_cast<double>(std::min<int>(surf.GetType(), CHF_SURF_OTHER));
    GProp_GProps props;
    BRepGProp::SurfaceProperties(face, props);
    out[1] = props.Mass();
    gp_Pnt c = props.CentreOfMass();

    // Normal at the point of the face closest to the centroid.
    double u1, u2, v1, v2;
    BRepTools::UVBounds(face, u1, u2, v1, v2);
    double u = (u1 + u2) / 2, v = (v1 + v2) / 2;
    Handle(Geom_Surface) gs = BRep_Tool::Surface(face);
    GeomAPI_ProjectPointOnSurf proj(c, gs, u1, u2, v1, v2);
    if (proj.NbPoints() > 0) proj.LowerDistanceParameters(u, v);
    gp_Pnt p;
    gp_Dir n(0, 0, 1);
    faceNormal(face, u, v, p, n);
    put3(out + 2, c.XYZ());
    put3(out + 5, n.XYZ());

    switch (surf.GetType()) {
      case GeomAbs_Plane: {
        gp_Pln pl = surf.Plane();
        put3(out + 8, pl.Location().XYZ());
        gp_Dir pn = pl.Axis().Direction();
        if (face.Orientation() == TopAbs_REVERSED) pn.Reverse();
        put3(out + 11, pn.XYZ());
        break;
      }
      case GeomAbs_Cylinder:
        put3(out + 8, surf.Cylinder().Location().XYZ());
        put3(out + 11, surf.Cylinder().Axis().Direction().XYZ());
        out[14] = surf.Cylinder().Radius();
        break;
      case GeomAbs_Cone:
        put3(out + 8, surf.Cone().Apex().XYZ());
        put3(out + 11, surf.Cone().Axis().Direction().XYZ());
        out[14] = surf.Cone().RefRadius();
        break;
      case GeomAbs_Sphere:
        put3(out + 8, surf.Sphere().Location().XYZ());
        out[14] = surf.Sphere().Radius();
        break;
      case GeomAbs_Torus:
        put3(out + 8, surf.Torus().Location().XYZ());
        put3(out + 11, surf.Torus().Axis().Direction().XYZ());
        out[14] = surf.Torus().MajorRadius();
        break;
      default:
        break;
    }
    out[15] = face.Orientation() == TopAbs_REVERSED ? 1 : 0;
    return 1;
  });
}

int chf_face_plane(chf_shape* s, int index, double* out9) {
  return chf_guard(0, [&] {
    if (!validIndex(s, CHF_FACE, index)) return 0;
    TopoDS_Face face = TopoDS::Face(s->map(CHF_FACE).FindKey(index));
    BRepAdaptor_Surface surf(face, false);
    if (surf.GetType() != GeomAbs_Plane) return 0;
    gp_Ax3 ax = surf.Plane().Position();
    gp_Dir n = ax.Direction();
    gp_Dir x = ax.XDirection();
    if (face.Orientation() == TopAbs_REVERSED) {
      n.Reverse();
      x.Reverse();  // keeps the frame right-handed with y unchanged
    }
    put3(out9, ax.Location().XYZ());
    put3(out9 + 3, n.XYZ());
    put3(out9 + 6, x.XYZ());
    return 1;
  });
}

double chf_face_project(chf_shape* s, int index, const double* p, double* out6) {
  return chf_guard(-1.0, [&] {
    if (!validIndex(s, CHF_FACE, index)) return -1.0;
    TopoDS_Face face = TopoDS::Face(s->map(CHF_FACE).FindKey(index));
    Handle(Geom_Surface) gs = BRep_Tool::Surface(face);
    double u1, u2, v1, v2;
    BRepTools::UVBounds(face, u1, u2, v1, v2);
    gp_Pnt target(p[0], p[1], p[2]);
    GeomAPI_ProjectPointOnSurf proj(target, gs, u1, u2, v1, v2);
    if (proj.NbPoints() == 0) return -1.0;
    double u, v;
    proj.LowerDistanceParameters(u, v);
    gp_Pnt q;
    gp_Dir n(0, 0, 1);
    faceNormal(face, u, v, q, n);
    put3(out6, q.XYZ());
    put3(out6 + 3, n.XYZ());
    return proj.LowerDistance();
  });
}

int chf_edge_info(chf_shape* s, int index, double* out) {
  return chf_guard(0, [&] {
    if (!validIndex(s, CHF_EDGE, index)) return 0;
    std::fill(out, out + 16, 0.0);
    TopoDS_Edge edge = TopoDS::Edge(s->map(CHF_EDGE).FindKey(index));
    if (BRep_Tool::Degenerated(edge)) {
      out[0] = CHF_CURVE_OTHER;
      return 1;
    }
    BRepAdaptor_Curve curve(edge);
    out[0] = static_cast<double>(std::min<int>(curve.GetType(), CHF_CURVE_OTHER));
    out[1] = GCPnts_AbscissaPoint::Length(curve);
    double t0 = curve.FirstParameter(), t1 = curve.LastParameter();
    put3(out + 2, curve.Value((t0 + t1) / 2).XYZ());
    put3(out + 5, curve.Value(t0).XYZ());
    put3(out + 8, curve.Value(t1).XYZ());
    if (curve.GetType() == GeomAbs_Circle) {
      put3(out + 11, curve.Circle().Location().XYZ());
      out[14] = curve.Circle().Radius();
    } else if (curve.GetType() == GeomAbs_Line) {
      put3(out + 11, curve.Line().Direction().XYZ());
    }
    out[15] = BRep_Tool::IsClosed(edge) || curve.IsClosed() ? 1 : 0;
    return 1;
  });
}

int chf_edge_sample(chf_shape* s, int index, double deflection, double* out, int cap) {
  return chf_guard(-1, [&] {
    if (!validIndex(s, CHF_EDGE, index)) return -1;
    TopoDS_Edge edge = TopoDS::Edge(s->map(CHF_EDGE).FindKey(index));
    if (BRep_Tool::Degenerated(edge)) return 0;
    BRepAdaptor_Curve curve(edge);
    GCPnts_TangentialDeflection pts(curve, 0.1, deflection, 2);
    int n = pts.NbPoints();
    for (int i = 0; i < n && i < cap; ++i) put3(out + 3 * i, pts.Value(i + 1).XYZ());
    return n;
  });
}

double chf_distance(const chf_shape* a, const chf_shape* b, double* out6) {
  return chf_guard(-1.0, [&] {
    if (!a || !b) return -1.0;
    BRepExtrema_DistShapeShape dist(a->shape, b->shape);
    if (!dist.IsDone() || dist.NbSolution() < 1) return -1.0;
    put3(out6, dist.PointOnShape1(1).XYZ());
    put3(out6 + 3, dist.PointOnShape2(1).XYZ());
    return dist.Value();
  });
}

int chf_vertex_point(chf_shape* s, int index, double* out3) {
  return chf_guard(0, [&] {
    if (!validIndex(s, CHF_VERTEX, index)) return 0;
    put3(out3, BRep_Tool::Pnt(TopoDS::Vertex(s->map(CHF_VERTEX).FindKey(index))).XYZ());
    return 1;
  });
}

}  // extern "C"
