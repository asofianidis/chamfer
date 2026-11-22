// Hidden-line removal for drawing views.
#include "internal.h"

#include <BRepAdaptor_Curve.hxx>
#include <BRep_Tool.hxx>
#include <GCPnts_TangentialDeflection.hxx>
#include <HLRAlgo_Projector.hxx>
#include <HLRBRep_Algo.hxx>
#include <HLRBRep_HLRToShape.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>

#include <vector>

struct chf_lines {
  std::vector<double> points;  // x y per point, in view coordinates
  std::vector<int> offsets{0};
  std::vector<int> kinds;
};

extern "C" {

chf_lines* chf_hlr(chf_shape* s, const double* axis9, double deflection) {
  return chf_guard<chf_lines*>(nullptr, [&]() -> chf_lines* {
    if (!s || s->shape.IsNull()) throw std::runtime_error("null shape");
    gp_Ax2 ax(gp_Pnt(axis9[0], axis9[1], axis9[2]), gp_Dir(axis9[3], axis9[4], axis9[5]), gp_Dir(axis9[6], axis9[7], axis9[8]));
    Handle(HLRBRep_Algo) algo = new HLRBRep_Algo();
    algo->Add(s->shape);
    algo->Projector(HLRAlgo_Projector(ax));
    algo->Update();
    algo->Hide();
    HLRBRep_HLRToShape h(algo);
    TopoDS_Shape parts[5] = {h.VCompound(), h.Rg1LineVCompound(), h.OutLineVCompound(), h.HCompound(), h.OutLineHCompound()};

    auto lines = new chf_lines();
    for (int kind = 0; kind < 5; ++kind) {
      if (parts[kind].IsNull()) continue;
      for (TopExp_Explorer ex(parts[kind], TopAbs_EDGE); ex.More(); ex.Next()) {
        TopoDS_Edge e = TopoDS::Edge(ex.Current());
        if (BRep_Tool::Degenerated(e)) continue;
        BRepAdaptor_Curve curve(e);
        GCPnts_TangentialDeflection pts(curve, 0.2, deflection, 2);
        if (pts.NbPoints() < 2) continue;
        for (int i = 1; i <= pts.NbPoints(); ++i) {
          gp_Pnt p = pts.Value(i);
          lines->points.push_back(p.X());
          lines->points.push_back(p.Y());
        }
        lines->offsets.push_back(static_cast<int>(lines->points.size() / 2));
        lines->kinds.push_back(kind);
      }
    }
    return lines;
  });
}

void chf_lines_free(chf_lines* l) { delete l; }

void chf_lines_counts(const chf_lines* l, int* out2) {
  out2[0] = static_cast<int>(l->points.size() / 2);
  out2[1] = static_cast<int>(l->kinds.size());
}

void chf_lines_copy(const chf_lines* l, double* points, int* offsets, int* kinds) {
  std::copy(l->points.begin(), l->points.end(), points);
  std::copy(l->offsets.begin(), l->offsets.end(), offsets);
  std::copy(l->kinds.begin(), l->kinds.end(), kinds);
}

}  // extern "C"
