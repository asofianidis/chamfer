// Primitive and profile construction.
#include "internal.h"

#include <BRepAdaptor_Curve.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepLib.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <BRep_Builder.hxx>
#include <GCE2d_MakeSegment.hxx>
#include <GeomAPI_Interpolate.hxx>
#include <Geom_BSplineCurve.hxx>
#include <Geom_CylindricalSurface.hxx>
#include <Geom2d_Line.hxx>
#include <ShapeFix_Face.hxx>
#include <ShapeFix_Wire.hxx>
#include <TColgp_HArray1OfPnt.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Compound.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Wire.hxx>
#include <gp_Ax3.hxx>
#include <gp_Circ.hxx>
#include <gp_Elips.hxx>
#include <gp_Pln.hxx>

#include <cmath>
#include <stdexcept>
#include <vector>

namespace {

constexpr double kPi = 3.14159265358979323846;

struct Frame {
  gp_Pnt origin;
  gp_Dir normal, x, y;

  explicit Frame(const double* p)
      : origin(p[0], p[1], p[2]), normal(p[3], p[4], p[5]), x(p[6], p[7], p[8]), y(normal.Crossed(x)) {}

  gp_Pnt at(double u, double v) const { return origin.Translated(gp_Vec(x) * u + gp_Vec(y) * v); }
  gp_Ax3 ax3() const { return gp_Ax3(origin, normal, x); }
};

TopoDS_Edge makeSegment(const Frame& f, int type, const double* d) {
  switch (type) {
    case CHF_SEG_LINE: {
      BRepBuilderAPI_MakeEdge me(f.at(d[0], d[1]), f.at(d[2], d[3]));
      if (!me.IsDone()) throw std::runtime_error("degenerate line segment");
      return me.Edge();
    }
    case CHF_SEG_ARC: {
      gp_Pnt c = f.at(d[0], d[1]);
      gp_Pnt p0 = f.at(d[2], d[3]);
      gp_Pnt p1 = f.at(d[4], d[5]);
      double r = c.Distance(p0);
      gp_Dir axis = d[6] != 0 ? f.normal : f.normal.Reversed();
      gp_Circ circ(gp_Ax2(c, axis, f.x), r);
      BRepBuilderAPI_MakeEdge me(circ, p0, p1);
      if (!me.IsDone()) throw std::runtime_error("degenerate arc segment");
      return me.Edge();
    }
    case CHF_SEG_CIRCLE: {
      gp_Circ circ(gp_Ax2(f.at(d[0], d[1]), f.normal, f.x), d[2]);
      return BRepBuilderAPI_MakeEdge(circ).Edge();
    }
    case CHF_SEG_ELLIPSE: {
      gp_Dir major = gp_Dir(gp_Vec(f.x) * std::cos(d[4]) + gp_Vec(f.y) * std::sin(d[4]));
      gp_Elips el(gp_Ax2(f.at(d[0], d[1]), f.normal, major), d[2], d[3]);
      return BRepBuilderAPI_MakeEdge(el).Edge();
    }
    default:
      throw std::runtime_error("unknown segment type");
  }
}

gp_Pnt midpoint(const TopoDS_Edge& e) {
  BRepAdaptor_Curve c(e);
  return c.Value((c.FirstParameter() + c.LastParameter()) / 2);
}

TopoDS_Wire makeWire(const Frame& f, int count, const int* types, const double* data,
                     std::vector<TopoDS_Edge>& edges) {
  BRepBuilderAPI_MakeWire mw;
  for (int i = 0; i < count; ++i) {
    TopoDS_Edge e = makeSegment(f, types[i], data + 8 * i);
    edges.push_back(e);
    mw.Add(e);
    if (!mw.IsDone()) throw std::runtime_error("profile segments do not connect into a chain");
  }
  return mw.Wire();
}

}  // namespace

extern "C" {

chf_shape* chf_make_box(double dx, double dy, double dz) {
  return chf_guard<chf_shape*>(nullptr, [&] { return new chf_shape(BRepPrimAPI_MakeBox(dx, dy, dz).Shape()); });
}

chf_shape* chf_make_cylinder(double radius, double height) {
  return chf_guard<chf_shape*>(nullptr,
                               [&] { return new chf_shape(BRepPrimAPI_MakeCylinder(radius, height).Shape()); });
}

chf_shape* chf_make_sphere(double radius) {
  return chf_guard<chf_shape*>(nullptr, [&] { return new chf_shape(BRepPrimAPI_MakeSphere(radius).Shape()); });
}

chf_shape* chf_make_face(const double* plane, int loopCount, const int* loopSizes, const int* segTypes,
                         const double* segData, int* edgeMap) {
  return chf_guard<chf_shape*>(nullptr, [&]() -> chf_shape* {
    if (loopCount < 1) throw std::runtime_error("a face needs at least one loop");
    Frame f(plane);
    std::vector<TopoDS_Edge> edges;
    int offset = 0;
    TopoDS_Wire outer = makeWire(f, loopSizes[0], segTypes, segData, edges);
    offset += loopSizes[0];
    BRepBuilderAPI_MakeFace mf(gp_Pln(f.ax3()), outer, true);
    if (!mf.IsDone()) throw std::runtime_error("could not build a face from the outer loop");
    for (int l = 1; l < loopCount; ++l) {
      TopoDS_Wire hole = makeWire(f, loopSizes[l], segTypes + offset, segData + 8 * offset, edges);
      offset += loopSizes[l];
      mf.Add(hole);
    }
    ShapeFix_Face fix(mf.Face());
    fix.FixOrientation();
    fix.Perform();
    TopoDS_Face face = fix.Face();
    auto result = new chf_shape(face);
    if (edgeMap) {
      // MakeWire and ShapeFix may rebuild edges to share vertices, so match
      // by identity first and fall back to the nearest edge midpoint.
      const auto& map = result->map(CHF_EDGE);
      std::vector<gp_Pnt> mids;
      for (int j = 1; j <= map.Extent(); ++j) mids.push_back(midpoint(TopoDS::Edge(map.FindKey(j))));
      for (size_t i = 0; i < edges.size(); ++i) {
        int idx = map.FindIndex(edges[i]);
        if (idx == 0) {
          gp_Pnt m = midpoint(edges[i]);
          double best = 1e300;
          for (size_t j = 0; j < mids.size(); ++j) {
            double d = m.SquareDistance(mids[j]);
            if (d < best) {
              best = d;
              idx = static_cast<int>(j) + 1;
            }
          }
        }
        edgeMap[i] = idx;
      }
    }
    return result;
  });
}

chf_shape* chf_make_wire(const double* plane, int segCount, const int* segTypes, const double* segData) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    Frame f(plane);
    std::vector<TopoDS_Edge> edges;
    return new chf_shape(makeWire(f, segCount, segTypes, segData, edges));
  });
}

chf_shape* chf_make_wire_3d(int pointCount, const double* points, int spline, int closed) {
  return chf_guard<chf_shape*>(nullptr, [&]() -> chf_shape* {
    if (pointCount < 2) throw std::runtime_error("a wire needs at least two points");
    if (!spline) {
      BRepBuilderAPI_MakePolygon poly;
      for (int i = 0; i < pointCount; ++i) poly.Add(gp_Pnt(points[3 * i], points[3 * i + 1], points[3 * i + 2]));
      if (closed) poly.Close();
      return new chf_shape(poly.Wire());
    }
    Handle(TColgp_HArray1OfPnt) pts = new TColgp_HArray1OfPnt(1, pointCount);
    for (int i = 0; i < pointCount; ++i)
      pts->SetValue(i + 1, gp_Pnt(points[3 * i], points[3 * i + 1], points[3 * i + 2]));
    GeomAPI_Interpolate interp(pts, closed != 0, 1e-7);
    interp.Perform();
    if (!interp.IsDone()) throw std::runtime_error("spline interpolation failed");
    TopoDS_Edge e = BRepBuilderAPI_MakeEdge(interp.Curve()).Edge();
    return new chf_shape(BRepBuilderAPI_MakeWire(e).Wire());
  });
}

chf_shape* chf_make_helix(double pitch, double height, double radius, const double* axis9, int leftHanded) {
  return chf_guard<chf_shape*>(nullptr, [&]() -> chf_shape* {
    if (pitch <= 0 || height <= 0 || radius <= 0) throw std::runtime_error("helix needs positive pitch, height and radius");
    Frame f(axis9);
    Handle(Geom_CylindricalSurface) cyl = new Geom_CylindricalSurface(f.ax3(), radius);
    double turns = height / pitch;
    double sign = leftHanded ? -1.0 : 1.0;
    gp_Pnt2d p0(0, 0);
    gp_Dir2d dir(sign * 2 * kPi, pitch);
    Handle(Geom2d_Line) line = new Geom2d_Line(p0, dir);
    double len = turns * std::sqrt(4 * kPi * kPi + pitch * pitch);
    TopoDS_Edge e = BRepBuilderAPI_MakeEdge(line, cyl, 0, len).Edge();
    BRepLib::BuildCurves3d(e);
    return new chf_shape(BRepBuilderAPI_MakeWire(e).Wire());
  });
}

chf_shape* chf_make_compound(int count, chf_shape* const* shapes) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    BRep_Builder b;
    TopoDS_Compound c;
    b.MakeCompound(c);
    for (int i = 0; i < count; ++i)
      if (shapes[i] && !shapes[i]->shape.IsNull()) b.Add(c, shapes[i]->shape);
    return new chf_shape(c);
  });
}

chf_shape* chf_make_plane_face(const double* plane9, double size) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    Frame f(plane9);
    return new chf_shape(BRepBuilderAPI_MakeFace(gp_Pln(f.ax3()), -size, size, -size, size).Face());
  });
}

}  // extern "C"
