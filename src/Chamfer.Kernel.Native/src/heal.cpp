// Import diagnostics and shape healing.
#include "internal.h"
#include <BRepBuilderAPI_MakeSolid.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <GCPnts_AbscissaPoint.hxx>
#include <BRepAdaptor_Curve.hxx>
#include <ShapeAnalysis_FreeBounds.hxx>
#include <ShapeFix_Shape.hxx>
#include <ShapeFix_Solid.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Compound.hxx>
#include <TopoDS_Shell.hxx>
#include <stdexcept>

namespace {

int countOf(const TopoDS_Shape& s, TopAbs_ShapeEnum kind) {
  TopTools_IndexedMapOfShape map;
  TopExp::MapShapes(s, kind, map);
  return map.Extent();
}

}  // namespace

int chf_diagnose(const chf_shape* s, double tolerance, int* out7) {
  return chf_guard(0, [&] {
    if (!s || s->shape.IsNull()) return 0;
    for (int i = 0; i < 7; ++i) out7[i] = 0;

    // Invalid faces and edges, as the checker sees them one by one.
    BRepCheck_Analyzer check(s->shape, Standard_True);
    TopTools_IndexedMapOfShape faces, edges;
    TopExp::MapShapes(s->shape, TopAbs_FACE, faces);
    TopExp::MapShapes(s->shape, TopAbs_EDGE, edges);
    for (int i = 1; i <= faces.Extent(); ++i)
      if (!check.IsValid(faces(i))) ++out7[0];
    for (int i = 1; i <= edges.Extent(); ++i)
      if (!check.IsValid(edges(i))) ++out7[1];

    // Free edges: on only one face (gaps and open boundaries).
    ShapeAnalysis_FreeBounds free(s->shape, tolerance, Standard_False, Standard_False);
    TopTools_IndexedMapOfShape freeEdges;
    TopExp::MapShapes(free.GetClosedWires(), TopAbs_EDGE, freeEdges);
    TopExp::MapShapes(free.GetOpenWires(), TopAbs_EDGE, freeEdges);
    out7[2] = freeEdges.Extent();

    // Tiny edges, which often break later modelling.
    for (int i = 1; i <= edges.Extent(); ++i) {
      const TopoDS_Edge& e = TopoDS::Edge(edges(i));
      if (BRep_Tool::Degenerated(e)) continue;
      BRepAdaptor_Curve c(e);
      if (GCPnts_AbscissaPoint::Length(c) < tolerance) ++out7[3];
    }

    out7[4] = countOf(s->shape, TopAbs_SOLID);
    out7[5] = countOf(s->shape, TopAbs_SHELL);
    out7[6] = faces.Extent();
    return 1;
  });
}

chf_shape* chf_heal(const chf_shape* s, double tolerance) {
  return chf_guard<chf_shape*>(nullptr, [&]() -> chf_shape* {
    if (!s || s->shape.IsNull()) throw std::runtime_error("empty shape");

    // Sew faces that should touch (closes small gaps), then fix wires, faces and shells.
    BRepBuilderAPI_Sewing sewing(tolerance);
    sewing.Add(s->shape);
    sewing.Perform();
    TopoDS_Shape sewn = sewing.SewedShape();
    if (sewn.IsNull()) sewn = s->shape;

    Handle(ShapeFix_Shape) fix = new ShapeFix_Shape(sewn);
    fix->SetPrecision(tolerance);
    fix->SetMaxTolerance(tolerance * 10);
    fix->Perform();
    TopoDS_Shape fixed = fix->Shape();

    // Closed shells that aren't solids yet become solids.
    if (countOf(fixed, TopAbs_SOLID) == 0) {
      BRep_Builder builder;
      TopoDS_Compound solids;
      builder.MakeCompound(solids);
      int made = 0;
      for (TopExp_Explorer ex(fixed, TopAbs_SHELL); ex.More(); ex.Next()) {
        const TopoDS_Shell& shell = TopoDS::Shell(ex.Current());
        if (!BRep_Tool::IsClosed(shell)) continue;
        BRepBuilderAPI_MakeSolid ms(shell);
        if (!ms.IsDone()) continue;
        ShapeFix_Solid fixSolid(ms.Solid());
        fixSolid.Perform();
        builder.Add(solids, fixSolid.Solid());
        ++made;
      }
      if (made == 1) {
        TopExp_Explorer one(solids, TopAbs_SOLID);
        fixed = one.Current();
      } else if (made > 1) {
        fixed = solids;
      }
    }

    return new chf_shape(fixed);
  });
}
