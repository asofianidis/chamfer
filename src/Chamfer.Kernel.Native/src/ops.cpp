// Modelling operations with history.
#include "internal.h"
#include <BRepAlgoAPI_Defeaturing.hxx>

#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepAlgoAPI_Section.hxx>
#include <BRepAlgoAPI_Splitter.hxx>
#include <BRepBuilderAPI_MakeSolid.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepLib.hxx>
#include <Precision.hxx>
#include <BRepOffsetAPI_MakeOffset.hxx>
#include <BRepBuilderAPI_NurbsConvert.hxx>
#include <BRep_Builder.hxx>
#include <Geom_BSplineSurface.hxx>
#include <Geom_BSplineCurve.hxx>
#include <ShapeFix_Shape.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <functional>
#include <Geom_CylindricalSurface.hxx>
#include <GCE2d_MakeSegment.hxx>
#include <ShapeFix_Face.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBndLib.hxx>
#include <BRepProj_Projection.hxx>
#include <ShapeAnalysis_FreeBounds.hxx>
#include <TopTools_HSequenceOfShape.hxx>
#include <BRepAdaptor_CompCurve.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepFill_TypeOfContact.hxx>
#include <GeomAPI_Interpolate.hxx>
#include <GCPnts_AbscissaPoint.hxx>
#include <Bnd_Box.hxx>
#include <cmath>
#include <Geom_BSplineCurve.hxx>
#include <TColgp_HArray1OfPnt.hxx>
#include <BRep_Tool.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRepFeat_MakeLinearForm.hxx>
#include <BRepFilletAPI_MakeChamfer.hxx>
#include <Geom_Plane.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <BRepOffsetAPI_DraftAngle.hxx>
#include <BRepOffsetAPI_MakeFilling.hxx>
#include <BRepOffsetAPI_MakeOffsetShape.hxx>
#include <BRepOffsetAPI_MakePipe.hxx>
#include <BRepOffsetAPI_MakePipeShell.hxx>
#include <BRepOffsetAPI_MakeThickSolid.hxx>
#include <BRepOffsetAPI_ThruSections.hxx>
#include <BRepOffset_MakeOffset.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepPrimAPI_MakeRevol.hxx>
#include <BRepTools.hxx>
#include <BRepTools_History.hxx>
#include <ShapeFix_Solid.hxx>
#include <ShapeUpgrade_UnifySameDomain.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_ListOfShape.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Shell.hxx>
#include <gp_Pln.hxx>
#include <gp_Trsf.hxx>

#include <algorithm>
#include <stdexcept>
#include <vector>

namespace {

gp_Pnt pnt(const double* p) { return gp_Pnt(p[0], p[1], p[2]); }
gp_Dir dir(const double* p) { return gp_Dir(p[0], p[1], p[2]); }

const TopoDS_Shape& sub(chf_shape* s, int kind, int index) {
  const auto& m = s->map(kind);
  if (index < 1 || index > m.Extent()) throw std::runtime_error("sub-shape index out of range");
  return m.FindKey(index);
}

void requireShape(const chf_shape* s) {
  if (!s || s->shape.IsNull()) throw std::runtime_error("null shape");
}

// Op whose history comes from a BRepTools_History.
chf_op* opFromHistory(const TopoDS_Shape& result, const Handle(BRepTools_History)& history) {
  auto op = chf_make_op_plain(result);
  if (!history.IsNull()) {
    Handle(BRepTools_History) h = history;
    op->modified = [h](const TopoDS_Shape& s) -> const TopTools_ListOfShape& { return h->Modified(s); };
    op->generated = [h](const TopoDS_Shape& s) -> const TopTools_ListOfShape& { return h->Generated(s); };
    op->isDeleted = [h](const TopoDS_Shape& s) { return h->IsRemoved(s); };
  }
  return op;
}

}  // namespace

extern "C" {

void chf_op_free(chf_op* op) { delete op; }

chf_shape* chf_op_shape(chf_op* op) {
  return chf_guard<chf_shape*>(nullptr, [&] { return op ? new chf_shape(op->result) : nullptr; });
}

int chf_op_history(chf_op* op, chf_shape* input, int kind, int index, int mode, int of, int* out, int cap) {
  return chf_guard(-2, [&] {
    requireShape(input);
    const TopoDS_Shape& s = sub(input, kind, index);
    if (!op->resultHandle) op->resultHandle = new chf_shape(op->result);
    const auto& resultMap = op->resultHandle->map(of);
    int n = 0;
    auto emit = [&](const TopoDS_Shape& r) {
      if (r.ShapeType() == of) {
        int i = resultMap.FindIndex(r);
        if (i > 0) {
          for (int k = 0; k < n && k < cap; ++k)
            if (out[k] == i) return;
          if (n < cap) out[n] = i;
          ++n;
        }
      } else if (r.ShapeType() < of) {
        for (TopExp_Explorer ex(r, static_cast<TopAbs_ShapeEnum>(of)); ex.More(); ex.Next()) {
          int i = resultMap.FindIndex(ex.Current());
          bool dup = false;
          for (int k = 0; k < n && k < cap; ++k) dup |= out[k] == i;
          if (i > 0 && !dup) {
            if (n < cap) out[n] = i;
            ++n;
          }
        }
      }
    };
    if (mode == 0) {
      const TopTools_ListOfShape& list = op->modified(s);
      for (const TopoDS_Shape& r : list) emit(r);
      if (n == 0) {
        // Unmodified sub-shapes are carried into the result as-is.
        int i = s.ShapeType() == of ? resultMap.FindIndex(s) : 0;
        if (i > 0) {
          if (cap > 0) out[0] = i;
          n = 1;
        } else if (op->isDeleted(s)) {
          return -1;
        }
      }
    } else {
      const TopTools_ListOfShape& list = op->generated(s);
      for (const TopoDS_Shape& r : list) emit(r);
    }
    return n;
  });
}

int chf_op_caps(chf_op* op, int which, int* out, int cap) {
  return chf_guard(-1, [&] {
    if (!op->resultHandle) op->resultHandle = new chf_shape(op->result);
    const auto& faces = op->resultHandle->map(CHF_FACE);
    const TopoDS_Shape& caps = which == 0 ? op->first : op->last;
    int n = 0;
    if (caps.IsNull()) return 0;
    for (TopExp_Explorer ex(caps, TopAbs_FACE); ex.More(); ex.Next()) {
      int i = faces.FindIndex(ex.Current());
      if (i > 0) {
        if (n < cap) out[n] = i;
        ++n;
      }
    }
    return n;
  });
}

chf_op* chf_extrude(chf_shape* profile, const double* d) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(profile);
    auto algo = std::make_unique<BRepPrimAPI_MakePrism>(profile->shape, gp_Vec(d[0], d[1], d[2]), false, true);
    TopoDS_Shape first = algo->FirstShape(), last = algo->LastShape();
    chf_op* op = chf_make_op(std::move(algo), "extrude");
    if (op) {
      op->first = first;
      op->last = last;
    }
    return op;
  });
}

chf_op* chf_revolve(chf_shape* profile, const double* axis6, double angle) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(profile);
    auto algo = std::make_unique<BRepPrimAPI_MakeRevol>(profile->shape, gp_Ax1(pnt(axis6), dir(axis6 + 3)), angle, false);
    algo->Build();
    TopoDS_Shape first = algo->FirstShape(), last = algo->LastShape();
    chf_op* op = chf_make_op(std::move(algo), "revolve");
    if (op) {
      op->first = first;
      op->last = last;
    }
    return op;
  });
}

chf_op* chf_boolean(chf_shape* a, chf_shape* b, int kind) {
  return chf_guard<chf_op*>(nullptr, [&]() -> chf_op* {
    requireShape(a);
    requireShape(b);
    std::unique_ptr<BRepAlgoAPI_BooleanOperation> algo;
    switch (kind) {
      case CHF_FUSE: algo = std::make_unique<BRepAlgoAPI_Fuse>(); break;
      case CHF_CUT: algo = std::make_unique<BRepAlgoAPI_Cut>(); break;
      case CHF_COMMON: algo = std::make_unique<BRepAlgoAPI_Common>(); break;
      default: throw std::runtime_error("unknown boolean kind");
    }
    TopTools_ListOfShape args, tools;
    args.Append(a->shape);
    tools.Append(b->shape);
    algo->SetArguments(args);
    algo->SetTools(tools);
    algo->SetRunParallel(false);
    algo->Build();
    if (algo->HasErrors()) throw std::runtime_error("boolean operation failed");
    algo->SimplifyResult(true, true);
    return chf_make_op(std::move(algo), "boolean");
  });
}

chf_op* chf_fillet(chf_shape* s, int count, const int* edges, const double* radii) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    auto algo = std::make_unique<BRepFilletAPI_MakeFillet>(s->shape);
    for (int i = 0; i < count; ++i) algo->Add(radii[i], TopoDS::Edge(sub(s, CHF_EDGE, edges[i])));
    algo->Build();
    return chf_make_op(std::move(algo), "fillet");
  });
}

chf_op* chf_fillet_variable(chf_shape* s, int count, const int* edges, const double* start, const double* end) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    auto algo = std::make_unique<BRepFilletAPI_MakeFillet>(s->shape);
    for (int i = 0; i < count; ++i) algo->Add(start[i], end[i], TopoDS::Edge(sub(s, CHF_EDGE, edges[i])));
    algo->Build();
    return chf_make_op(std::move(algo), "fillet");
  });
}

chf_op* chf_chamfer(chf_shape* s, int count, const int* edges, const int* faces, const double* d1, const double* d2) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    TopTools_IndexedDataMapOfShapeListOfShape edgeFaces;
    TopExp::MapShapesAndAncestors(s->shape, TopAbs_EDGE, TopAbs_FACE, edgeFaces);
    auto algo = std::make_unique<BRepFilletAPI_MakeChamfer>(s->shape);
    for (int i = 0; i < count; ++i) {
      TopoDS_Edge e = TopoDS::Edge(sub(s, CHF_EDGE, edges[i]));
      TopoDS_Face f;
      if (faces && faces[i] > 0) {
        f = TopoDS::Face(sub(s, CHF_FACE, faces[i]));
      } else {
        const TopTools_ListOfShape& adj = edgeFaces.FindFromKey(e);
        if (adj.IsEmpty()) throw std::runtime_error("chamfer edge has no adjacent face");
        f = TopoDS::Face(adj.First());
      }
      algo->Add(d1[i], d2[i], e, f);
    }
    algo->Build();
    return chf_make_op(std::move(algo), "chamfer");
  });
}

chf_op* chf_shell(chf_shape* s, int count, const int* faces, double thickness) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    TopTools_ListOfShape remove;
    for (int i = 0; i < count; ++i) remove.Append(sub(s, CHF_FACE, faces[i]));
    auto algo = std::make_unique<BRepOffsetAPI_MakeThickSolid>();
    algo->MakeThickSolidByJoin(s->shape, remove, -thickness, 1e-3, BRepOffset_Skin, false, false, GeomAbs_Intersection);
    return chf_make_op(std::move(algo), "shell");
  });
}

chf_op* chf_draft(chf_shape* s, int count, const int* faces, const double* neutral6, double angle) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    auto algo = std::make_unique<BRepOffsetAPI_DraftAngle>(s->shape);
    gp_Pln neutral(pnt(neutral6), dir(neutral6 + 3));
    for (int i = 0; i < count; ++i) {
      algo->Add(TopoDS::Face(sub(s, CHF_FACE, faces[i])), dir(neutral6 + 3), angle, neutral);
      if (!algo->AddDone()) throw std::runtime_error("draft can't be applied to one of the faces");
    }
    algo->Build();
    return chf_make_op(std::move(algo), "draft");
  });
}

chf_op* chf_defeature(chf_shape* s, int count, const int* faces) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    auto algo = std::make_unique<BRepAlgoAPI_Defeaturing>();
    algo->SetShape(s->shape);
    for (int i = 0; i < count; ++i) algo->AddFaceToRemove(sub(s, CHF_FACE, faces[i]));
    algo->SetToFillHistory(Standard_True);
    algo->Build();
    if (!algo->IsDone() || algo->HasErrors()) throw std::runtime_error("these faces can't be removed");
    return chf_make_op(std::move(algo), "defeature");
  });
}

chf_op* chf_transform(chf_shape* s, const double* m, int copy) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    gp_Trsf t;
    t.SetValues(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11]);
    auto algo = std::make_unique<BRepBuilderAPI_Transform>(s->shape, t, copy != 0);
    return chf_make_op(std::move(algo), "transform");
  });
}

chf_op* chf_mirror(chf_shape* s, const double* plane6, int copy) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    gp_Trsf t;
    t.SetMirror(gp_Ax2(pnt(plane6), dir(plane6 + 3)));
    auto algo = std::make_unique<BRepBuilderAPI_Transform>(s->shape, t, copy != 0);
    return chf_make_op(std::move(algo), "mirror");
  });
}

// A curve winding around the spine: as the profile's orientation follows it, the profile
// turns by `twist` radians from start to end. Frames are carried along the spine by parallel
// transport, so the turning is even and doesn't depend on the spine's curvature.
static TopoDS_Wire twistSpine(const TopoDS_Wire& spine, double twist) {
  BRepAdaptor_CompCurve curve(spine);
  const double f = curve.FirstParameter(), l = curve.LastParameter();
  const int n = 96;
  gp_Pnt p0;
  gp_Vec t0;
  curve.D1(f, p0, t0);
  gp_Dir t(t0);
  gp_Dir u = std::abs(t.Z()) < 0.9 ? gp_Dir(t.Crossed(gp_Dir(0, 0, 1))) : gp_Dir(t.Crossed(gp_Dir(1, 0, 0)));
  double radius = std::max(1.0, GCPnts_AbscissaPoint::Length(curve) / 10);
  Handle(TColgp_HArray1OfPnt) pts = new TColgp_HArray1OfPnt(1, n + 1);
  for (int i = 0; i <= n; ++i) {
    gp_Pnt p;
    gp_Vec d;
    curve.D1(f + (l - f) * i / n, p, d);
    if (d.Magnitude() > 1e-12) {
      gp_Dir ti(d);
      gp_Vec uv = gp_Vec(u) - gp_Vec(ti) * gp_Vec(u).Dot(gp_Vec(ti));
      if (uv.Magnitude() > 1e-9) u = gp_Dir(uv);
      t = ti;
    }
    gp_Dir v = t.Crossed(u);
    double a = twist * i / n;
    gp_Vec offset = (gp_Vec(u) * std::cos(a) + gp_Vec(v) * std::sin(a)) * radius;
    pts->SetValue(i + 1, p.Translated(offset));
  }
  GeomAPI_Interpolate interp(pts, Standard_False, 1e-7);
  interp.Perform();
  if (!interp.IsDone()) throw std::runtime_error("couldn't build the twist");
  return BRepBuilderAPI_MakeWire(BRepBuilderAPI_MakeEdge(interp.Curve()).Edge()).Wire();
}

chf_op* chf_sweep(chf_shape* profile, chf_shape* path, int mode) { return chf_sweep_ex(profile, path, nullptr, mode, 0); }

chf_op* chf_sweep_ex(chf_shape* profile, chf_shape* path, chf_shape* guide, int mode, double twist) {
  return chf_guard<chf_op*>(nullptr, [&]() -> chf_op* {
    requireShape(profile);
    requireShape(path);
    if (path->shape.ShapeType() != TopAbs_WIRE) throw std::runtime_error("sweep path must be a wire");
    TopoDS_Wire spine = TopoDS::Wire(path->shape);
    TopoDS_Wire aux;
    BRepFill_TypeOfContact contact = BRepFill_NoContact;
    if (guide) {
      requireShape(guide);
      if (guide->shape.ShapeType() == TopAbs_WIRE) {
        aux = TopoDS::Wire(guide->shape);
      } else if (guide->shape.ShapeType() == TopAbs_EDGE) {
        aux = BRepBuilderAPI_MakeWire(TopoDS::Edge(guide->shape)).Wire();
      } else {
        throw std::runtime_error("the guide must be a curve");
      }
      contact = BRepFill_ContactOnBorder; // the guide becomes an edge of the sweep; the profile's size follows it
    } else if (std::abs(twist) > 1e-12) {
      aux = twistSpine(spine, twist);
    }

    // Pipe shells handle paths with sharp corners (right-corner transitions).
    auto pipe = [&](const TopoDS_Wire& w, bool solid) {
      auto algo = std::make_unique<BRepOffsetAPI_MakePipeShell>(spine);
      if (!aux.IsNull()) {
        // Points on the guide or twist spine are matched to the path by length along it.
        algo->SetMode(aux, Standard_True, contact);
      } else {
        algo->SetMode(mode == 0);
      }
      algo->SetTransitionMode(BRepBuilderAPI_RightCorner);
      algo->Add(w, false, false);
      algo->Build();
      if (!algo->IsDone()) throw std::runtime_error("the profile can't be swept along this path");
      if (solid && !algo->MakeSolid()) throw std::runtime_error("the swept shape isn't closed");
      return algo;
    };

    const TopoDS_Shape& p = profile->shape;
    if (p.ShapeType() == TopAbs_WIRE) {
      auto algo = pipe(TopoDS::Wire(p), false);
      return chf_make_op(std::move(algo), "sweep");
    }
    if (p.ShapeType() != TopAbs_FACE) throw std::runtime_error("sweep profile must be a face or a wire");

    TopoDS_Face face = TopoDS::Face(p);
    TopoDS_Wire outer = BRepTools::OuterWire(face);
    auto algo = pipe(outer, true);
    TopoDS_Shape first = algo->FirstShape(), last = algo->LastShape();
    std::vector<TopoDS_Shape> holes;
    for (TopExp_Explorer ex(face, TopAbs_WIRE); ex.More(); ex.Next()) {
      if (!ex.Current().IsSame(outer)) holes.push_back(pipe(TopoDS::Wire(ex.Current()), true)->Shape());
    }
    if (holes.empty()) {
      chf_op* op = chf_make_op(std::move(algo), "sweep");
      if (op) {
        op->first = first;
        op->last = last;
      }
      return op;
    }

    // Profiles with holes: sweep each loop and subtract the inner tubes.
    TopoDS_Shape result = algo->Shape();
    for (const auto& h : holes) {
      BRepAlgoAPI_Cut cut(result, h);
      if (!cut.IsDone()) throw std::runtime_error("couldn't subtract a hole from the sweep");
      result = cut.Shape();
    }
    return chf_make_op_plain(result);
  });
}

chf_op* chf_loft(int count, chf_shape* const* sections, int solid, int ruled) {
  return chf_guard<chf_op*>(nullptr, [&] {
    if (count < 2) throw std::runtime_error("loft needs at least two sections");
    auto algo = std::make_unique<BRepOffsetAPI_ThruSections>(solid != 0, ruled != 0, 1e-6);
    for (int i = 0; i < count; ++i) {
      requireShape(sections[i]);
      const TopoDS_Shape& sec = sections[i]->shape;
      if (sec.ShapeType() == TopAbs_FACE) {
        algo->AddWire(BRepTools::OuterWire(TopoDS::Face(sec)));
      } else if (sec.ShapeType() == TopAbs_WIRE) {
        algo->AddWire(TopoDS::Wire(sec));
      } else if (sec.ShapeType() == TopAbs_VERTEX) {
        algo->AddVertex(TopoDS::Vertex(sec));
      } else {
        throw std::runtime_error("loft sections must be faces, wires or points");
      }
    }
    algo->Build();
    TopoDS_Shape first = algo->IsDone() ? algo->FirstShape() : TopoDS_Shape();
    TopoDS_Shape last = algo->IsDone() ? algo->LastShape() : TopoDS_Shape();
    chf_op* op = chf_make_op(std::move(algo), "loft");
    if (op) {
      op->first = first;
      op->last = last;
    }
    return op;
  });
}

chf_op* chf_rib(chf_shape* base, chf_shape* wire, const double* plane6, double thickness1, double thickness2) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(base);
    requireShape(wire);
    if (wire->shape.ShapeType() != TopAbs_WIRE) throw std::runtime_error("rib profile must be a wire");
    gp_Dir n = dir(plane6 + 3);
    Handle(Geom_Plane) plane = new Geom_Plane(pnt(plane6), n);
    auto algo = std::make_unique<BRepFeat_MakeLinearForm>(base->shape, TopoDS::Wire(wire->shape), plane,
                                                          gp_Vec(n) * thickness1, gp_Vec(n) * -thickness2, 1, true);
    algo->Perform();
    if (!algo->IsDone() || algo->CurrentStatusError() != BRepFeat_OK) throw std::runtime_error("rib failed");
    return chf_make_op(std::move(algo), "rib");
  });
}

chf_op* chf_thicken(chf_shape* s, double thickness) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    BRepOffset_MakeOffset offset;
    offset.Initialize(s->shape, thickness, 1e-4, BRepOffset_Skin, false, false, GeomAbs_Intersection, true);
    offset.MakeOffsetShape();
    if (!offset.IsDone()) throw std::runtime_error("thicken failed");
    return chf_make_op_plain(offset.Shape());
  });
}

chf_op* chf_offset_shape(chf_shape* s, double offset) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    auto algo = std::make_unique<BRepOffsetAPI_MakeOffsetShape>();
    algo->PerformByJoin(s->shape, offset, 1e-4, BRepOffset_Skin, false, false, GeomAbs_Intersection);
    return chf_make_op(std::move(algo), "offset");
  });
}

chf_op* chf_freeform(chf_shape* s, int face, int count, const double* points) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    TopoDS_Face target = TopoDS::Face(sub(s, CHF_FACE, face));
    // A smooth patch on the face's own boundary, pulled through the given points.
    BRepOffsetAPI_MakeFilling fill(3, 15, 2, Standard_False, 1e-5, 1e-4, 1e-2, 0.1, 9, 12);
    for (TopExp_Explorer ex(target, TopAbs_EDGE); ex.More(); ex.Next()) {
      fill.Add(TopoDS::Edge(ex.Current()), GeomAbs_C0);
    }
    for (int i = 0; i < count; ++i) {
      fill.Add(gp_Pnt(points[3 * i], points[3 * i + 1], points[3 * i + 2]));
    }
    fill.Build();
    if (!fill.IsDone()) throw std::runtime_error("couldn't shape the face through these points");

    // Every other face, plus the new one, sewn back into a closed solid.
    BRepBuilderAPI_Sewing sew(1e-3);
    for (TopExp_Explorer ex(s->shape, TopAbs_FACE); ex.More(); ex.Next()) {
      if (!ex.Current().IsSame(target)) sew.Add(ex.Current());
    }
    sew.Add(fill.Shape());
    sew.Perform();
    TopoDS_Shape sewn = sew.SewedShape();
    TopoDS_Shell shell;
    for (TopExp_Explorer ex(sewn, TopAbs_SHELL); ex.More(); ex.Next()) {
      shell = TopoDS::Shell(ex.Current());
      break;
    }
    if (shell.IsNull() || !BRep_Tool::IsClosed(shell)) throw std::runtime_error("the reshaped face doesn't close up with its neighbours");
    BRepBuilderAPI_MakeSolid ms(shell);
    if (!ms.IsDone()) throw std::runtime_error("couldn't make a solid from the reshaped faces");
    TopoDS_Solid solid = ms.Solid();
    BRepLib::OrientClosedSolid(solid);
    ShapeFix_Solid fix(solid);
    fix.Perform();
    return chf_make_op_plain(fix.Solid());
  });
}

chf_op* chf_split_line(chf_shape* s, chf_shape* tool) {
  return chf_guard<chf_op*>(nullptr, [&]() -> chf_op* {
    requireShape(s);
    requireShape(tool);
    // Split the body's shells (its faces), not the solid: the faces the tool crosses are divided
    // along the crossing, and no inner faces are made. Then make the solid again from them.
    BRepAlgoAPI_Splitter splitter;
    TopTools_ListOfShape args, tools;
    for (TopExp_Explorer ex(s->shape, TopAbs_SHELL); ex.More(); ex.Next()) args.Append(ex.Current());
    if (args.IsEmpty()) throw std::runtime_error("split line needs a solid");
    tools.Append(tool->shape);
    splitter.SetArguments(args);
    splitter.SetTools(tools);
    splitter.Build();
    if (!splitter.IsDone() || splitter.HasErrors()) throw std::runtime_error("the sketch can't split these faces");

    BRepBuilderAPI_MakeSolid maker;
    for (TopExp_Explorer ex(splitter.Shape(), TopAbs_SHELL); ex.More(); ex.Next()) maker.Add(TopoDS::Shell(ex.Current()));
    if (!maker.IsDone()) throw std::runtime_error("couldn't rebuild the solid from the split faces");
    TopoDS_Solid solid = maker.Solid();
    BRepLib::OrientClosedSolid(solid);
    return opFromHistory(solid, splitter.History());
  });
}

chf_shape* chf_project_curve(chf_shape* curve, chf_shape* target, const double* dir3) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    requireShape(curve);
    requireShape(target);
    BRepProj_Projection proj(curve->shape, target->shape, gp_Dir(dir3[0], dir3[1], dir3[2]));
    if (!proj.IsDone()) throw std::runtime_error("the curve doesn't project onto the face");
    // The projection lands on both sides of a closed body; keep the wire nearest the curve.
    TopoDS_Shape best;
    double bestDistance = 1e300;
    Bnd_Box from;
    BRepBndLib::Add(curve->shape, from);
    gp_Pnt c = from.CornerMin().XYZ() * 0.5 + from.CornerMax().XYZ() * 0.5;
    for (; proj.More(); proj.Next()) {
      Bnd_Box b;
      BRepBndLib::Add(proj.Current(), b);
      gp_Pnt m = b.CornerMin().XYZ() * 0.5 + b.CornerMax().XYZ() * 0.5;
      if (m.Distance(c) < bestDistance) {
        bestDistance = m.Distance(c);
        best = proj.Current();
      }
    }
    if (best.IsNull()) throw std::runtime_error("the curve doesn't project onto the face");
    return new chf_shape(best);
  });
}

chf_shape* chf_join_curves(int count, chf_shape* const* curves) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    Handle(TopTools_HSequenceOfShape) edges = new TopTools_HSequenceOfShape();
    for (int i = 0; i < count; ++i) {
      requireShape(curves[i]);
      for (TopExp_Explorer ex(curves[i]->shape, TopAbs_EDGE); ex.More(); ex.Next()) edges->Append(ex.Current());
    }
    Handle(TopTools_HSequenceOfShape) wires;
    ShapeAnalysis_FreeBounds::ConnectEdgesToWires(edges, 1e-4, Standard_False, wires);
    if (wires.IsNull() || wires->Length() != 1) throw std::runtime_error("the curves don't join end to end into one chain");
    return new chf_shape(wires->Value(1));
  });
}

chf_shape* chf_wrap_face(const double* frame9, double radius, int loopCount, const int* loopSizes, const double* angleHeight) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    if (radius <= 0 || loopCount < 1) throw std::runtime_error("nothing to wrap");
    gp_Ax3 ax(gp_Pnt(frame9[0], frame9[1], frame9[2]), gp_Dir(frame9[3], frame9[4], frame9[5]), gp_Dir(frame9[6], frame9[7], frame9[8]));
    Handle(Geom_CylindricalSurface) surface = new Geom_CylindricalSurface(ax, radius);
    // Each loop is a polygon in the surface's (angle, height) parameters: its sides are helical
    // segments on the cylinder.
    std::vector<TopoDS_Wire> wires;
    int at = 0;
    for (int l = 0; l < loopCount; ++l) {
      BRepBuilderAPI_MakeWire wire;
      const int n = loopSizes[l];
      for (int i = 0; i < n; ++i) {
        gp_Pnt2d a(angleHeight[2 * (at + i)], angleHeight[2 * (at + i) + 1]);
        gp_Pnt2d b(angleHeight[2 * (at + (i + 1) % n)], angleHeight[2 * (at + (i + 1) % n) + 1]);
        if (a.Distance(b) < 1e-12) continue;
        Handle(Geom2d_TrimmedCurve) seg = GCE2d_MakeSegment(a, b);
        TopoDS_Edge edge = BRepBuilderAPI_MakeEdge(seg, surface);
        BRepLib::BuildCurves3d(edge);
        wire.Add(edge);
      }
      at += n;
      if (!wire.IsDone()) throw std::runtime_error("a wrapped outline doesn't close");
      wires.push_back(wire.Wire());
    }
    BRepBuilderAPI_MakeFace face(surface, wires[0], Standard_True);
    for (size_t i = 1; i < wires.size(); ++i) face.Add(wires[i]);
    if (!face.IsDone()) throw std::runtime_error("couldn't make the wrapped face");
    ShapeFix_Face fix(face.Face());
    fix.Perform();
    return new chf_shape(fix.Face());
  });
}

// Moves every control point of a NURBS copy of the shape through a deformation (after refining
// the knots so there are enough of them), then fixes tolerances: twist, taper and stretch along an axis.
chf_shape* chf_flex(chf_shape* s, const double* frame9, int mode, double amount, double length) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    requireShape(s);
    if (length <= 0) throw std::runtime_error("the flex length must be positive");
    gp_Pnt o(frame9[0], frame9[1], frame9[2]);
    gp_Dir z(frame9[3], frame9[4], frame9[5]);
    gp_Dir x(frame9[6], frame9[7], frame9[8]);
    gp_Dir y = z.Crossed(x);
    std::function<gp_Pnt(const gp_Pnt&)> deform = [&](const gp_Pnt& p) {
      if (mode == 3) {
        // Point deform: push along the axis by amount at the origin, fading to nothing at `length`.
        double r = p.Distance(o) / length;
        if (r >= 1) return p;
        double w = (1 - r * r) * (1 - r * r);
        return p.Translated(gp_Vec(z) * (amount * w));
      }

      gp_Vec d(o, p);
      double lx = d.Dot(gp_Vec(x)), ly = d.Dot(gp_Vec(y)), lz = d.Dot(gp_Vec(z));
      double t = std::clamp(lz / length, 0.0, 1.0);
      switch (mode) {
        case 0: {  // twist: turn about the axis, amount radians over the length
          double a = amount * t, c = std::cos(a), sn = std::sin(a);
          double nx = lx * c - ly * sn, ny = lx * sn + ly * c;
          lx = nx;
          ly = ny;
          break;
        }
        case 1: {  // taper: scale across the axis to (1 + amount) at the far end
          double k = 1 + amount * t;
          lx *= k;
          ly *= k;
          break;
        }
        default:  // stretch along the axis by (1 + amount)
          lz *= 1 + amount;
          break;
      }
      return o.Translated(gp_Vec(x) * lx + gp_Vec(y) * ly + gp_Vec(z) * lz);
    };

    BRepBuilderAPI_NurbsConvert convert(s->shape, Standard_True);
    TopoDS_Shape shape = convert.Shape();
    // Enough control points to follow the change: a point deform is local, so it needs a fine grid.
    const int spans = mode == 2 ? 1 : mode == 3 ? 24 : 8;
    auto refine = [&](double a, double b, const std::function<void(double)>& insert) {
      for (int k = 1; k < spans; ++k) insert(a + (b - a) * k / spans);
    };

    TopTools_IndexedMapOfShape faces, edges, vertices;
    TopExp::MapShapes(shape, TopAbs_FACE, faces);
    TopExp::MapShapes(shape, TopAbs_EDGE, edges);
    TopExp::MapShapes(shape, TopAbs_VERTEX, vertices);
    for (int i = 1; i <= faces.Extent(); ++i) {
      TopLoc_Location loc;
      Handle(Geom_BSplineSurface) bs = Handle(Geom_BSplineSurface)::DownCast(BRep_Tool::Surface(TopoDS::Face(faces(i)), loc));
      if (bs.IsNull()) continue;
      double u0, u1, v0, v1;
      bs->Bounds(u0, u1, v0, v1);
      if (bs->UDegree() < 2) bs->IncreaseDegree(2, bs->VDegree());
      if (bs->VDegree() < 2) bs->IncreaseDegree(bs->UDegree(), 2);
      refine(u0, u1, [&](double u) { bs->InsertUKnot(u, 1, 1e-9); });
      refine(v0, v1, [&](double v) { bs->InsertVKnot(v, 1, 1e-9); });
      for (int a = 1; a <= bs->NbUPoles(); ++a)
        for (int b = 1; b <= bs->NbVPoles(); ++b) bs->SetPole(a, b, deform(bs->Pole(a, b).Transformed(loc.Transformation())).Transformed(loc.Transformation().Inverted()));
    }
    for (int i = 1; i <= edges.Extent(); ++i) {
      TopLoc_Location loc;
      double f, l;
      Handle(Geom_BSplineCurve) bc = Handle(Geom_BSplineCurve)::DownCast(BRep_Tool::Curve(TopoDS::Edge(edges(i)), loc, f, l));
      if (bc.IsNull()) continue;
      if (bc->Degree() < 2) bc->IncreaseDegree(2);
      refine(bc->FirstParameter(), bc->LastParameter(), [&](double u) { bc->InsertKnot(u, 1, 1e-9); });
      for (int a = 1; a <= bc->NbPoles(); ++a) bc->SetPole(a, deform(bc->Pole(a).Transformed(loc.Transformation())).Transformed(loc.Transformation().Inverted()));
    }
    BRep_Builder builder;
    for (int i = 1; i <= vertices.Extent(); ++i) {
      TopoDS_Vertex v = TopoDS::Vertex(vertices(i));
      builder.UpdateVertex(v, deform(BRep_Tool::Pnt(v)), BRep_Tool::Tolerance(v));
    }

    BRepLib::SameParameter(shape, 1e-4, Standard_True);
    BRepLib::UpdateTolerances(shape);
    Handle(ShapeFix_Shape) fix = new ShapeFix_Shape(shape);
    fix->Perform();
    return new chf_shape(fix->Shape());
  });
}

// Untrim: the face rebuilt from its outer boundary alone (holes filled in); faces on bounded
// surfaces other than planes go back to the surface's own bounds when `natural` is set.
chf_shape* chf_face_untrim(chf_shape* face, int natural) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    requireShape(face);
    if (face->shape.ShapeType() != TopAbs_FACE) throw std::runtime_error("untrim needs a face");
    TopoDS_Face f = TopoDS::Face(face->shape);
    Handle(Geom_Surface) surface = BRep_Tool::Surface(f);
    if (natural && !surface->IsKind(STANDARD_TYPE(Geom_Plane))) {
      double u0, u1, v0, v1;
      surface->Bounds(u0, u1, v0, v1);
      if (!Precision::IsInfinite(u0) && !Precision::IsInfinite(u1) && !Precision::IsInfinite(v0) && !Precision::IsInfinite(v1)) {
        BRepBuilderAPI_MakeFace whole(surface, u0, u1, v0, v1, 1e-6);
        if (whole.IsDone()) return new chf_shape(whole.Face());
      }
    }
    BRepBuilderAPI_MakeFace outer(surface, BRepTools::OuterWire(f), Standard_True);
    if (!outer.IsDone()) throw std::runtime_error("couldn't rebuild the face");
    ShapeFix_Face fix(outer.Face());
    fix.Perform();
    return new chf_shape(fix.Face());
  });
}

// Extend a flat face outward by a distance all round (its outline offset in its plane).
chf_shape* chf_face_extend(chf_shape* face, double distance) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    requireShape(face);
    if (face->shape.ShapeType() != TopAbs_FACE) throw std::runtime_error("extend needs a face");
    TopoDS_Face f = TopoDS::Face(face->shape);
    if (!BRep_Tool::Surface(f)->IsKind(STANDARD_TYPE(Geom_Plane))) throw std::runtime_error("only flat faces can be extended");
    BRepOffsetAPI_MakeOffset offset(BRepBuilderAPI_MakeFace(BRep_Tool::Surface(f), BRepTools::OuterWire(f), Standard_True).Face(), GeomAbs_Arc);
    offset.Perform(distance);
    if (!offset.IsDone()) throw std::runtime_error("couldn't offset the outline");
    TopoDS_Wire wire;
    for (TopExp_Explorer ex(offset.Shape(), TopAbs_WIRE); ex.More(); ex.Next()) {
      wire = TopoDS::Wire(ex.Current());
      break;
    }
    if (wire.IsNull()) throw std::runtime_error("the offset outline vanished");
    BRepBuilderAPI_MakeFace grown(BRep_Tool::Surface(f), wire, Standard_True);
    if (!grown.IsDone()) throw std::runtime_error("couldn't make the extended face");
    // Holes stay as they are.
    TopoDS_Wire outer = BRepTools::OuterWire(f);
    for (TopExp_Explorer ex(f, TopAbs_WIRE); ex.More(); ex.Next()) {
      if (!ex.Current().IsSame(outer)) grown.Add(TopoDS::Wire(ex.Current()));
    }
    ShapeFix_Face fix(grown.Face());
    fix.Perform();
    return new chf_shape(fix.Face());
  });
}

chf_shape* chf_fill(chf_shape* wire) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    requireShape(wire);
    BRepOffsetAPI_MakeFilling fill;
    for (TopExp_Explorer ex(wire->shape, TopAbs_EDGE); ex.More(); ex.Next()) {
      fill.Add(TopoDS::Edge(ex.Current()), GeomAbs_C0);
    }
    fill.Build();
    if (!fill.IsDone()) throw std::runtime_error("couldn't fill the boundary");
    return new chf_shape(fill.Shape());
  });
}

chf_op* chf_sew(int count, chf_shape* const* shapes, double tolerance) {
  return chf_guard<chf_op*>(nullptr, [&] {
    BRepBuilderAPI_Sewing sew(tolerance);
    for (int i = 0; i < count; ++i) {
      requireShape(shapes[i]);
      sew.Add(shapes[i]->shape);
    }
    sew.Perform();
    TopoDS_Shape result = sew.SewedShape();
    if (result.ShapeType() == TopAbs_SHELL && result.Closed()) {
      BRepBuilderAPI_MakeSolid ms(TopoDS::Shell(result));
      if (ms.IsDone()) {
        ShapeFix_Solid fix(ms.Solid());
        fix.Perform();
        result = fix.Solid();
      }
    }
    return chf_make_op_plain(result);
  });
}

chf_op* chf_split(chf_shape* s, chf_shape* tool) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    requireShape(tool);
    auto algo = std::make_unique<BRepAlgoAPI_Splitter>();
    TopTools_ListOfShape args, tools;
    args.Append(s->shape);
    tools.Append(tool->shape);
    algo->SetArguments(args);
    algo->SetTools(tools);
    algo->Build();
    if (algo->HasErrors()) throw std::runtime_error("split failed");
    return chf_make_op(std::move(algo), "split");
  });
}

chf_shape* chf_section(chf_shape* s, const double* plane6) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    requireShape(s);
    BRepAlgoAPI_Section section(s->shape, gp_Pln(pnt(plane6), dir(plane6 + 3)), false);
    section.Approximation(true);
    section.Build();
    if (!section.IsDone()) throw std::runtime_error("section failed");
    return new chf_shape(section.Shape());
  });
}

chf_op* chf_unify(chf_shape* s) {
  return chf_guard<chf_op*>(nullptr, [&] {
    requireShape(s);
    ShapeUpgrade_UnifySameDomain unify(s->shape, true, true, false);
    unify.Build();
    return opFromHistory(unify.Shape(), unify.History());
  });
}

}  // extern "C"
