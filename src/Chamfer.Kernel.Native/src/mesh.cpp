// Tessellation into render-ready triangle and edge buffers.
#include "internal.h"

#include <BRepAdaptor_Curve.hxx>
#include <BRepLib_ToolTriangulatedShape.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRep_Tool.hxx>
#include <GCPnts_TangentialDeflection.hxx>
#include <Poly_Triangulation.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Face.hxx>

#include <vector>

struct chf_mesh {
  std::vector<float> positions, normals, edgePoints;
  std::vector<int> triangles, triangleFaces, edgeOffsets;
};

extern "C" {

chf_mesh* chf_tessellate(chf_shape* s, double linearDeflection, double angularDeflection) {
  return chf_guard<chf_mesh*>(nullptr, [&]() -> chf_mesh* {
    if (!s || s->shape.IsNull()) throw std::runtime_error("null shape");
    BRepMesh_IncrementalMesh mesher(s->shape, linearDeflection, false, angularDeflection, false);
    auto m = new chf_mesh();
    const auto& faces = s->map(CHF_FACE);
    for (int fi = 1; fi <= faces.Extent(); ++fi) {
      TopoDS_Face face = TopoDS::Face(faces.FindKey(fi));
      TopLoc_Location loc;
      Handle(Poly_Triangulation) tri = BRep_Tool::Triangulation(face, loc);
      if (tri.IsNull()) continue;
      if (!tri->HasNormals()) BRepLib_ToolTriangulatedShape::ComputeNormals(face, tri);
      const gp_Trsf& t = loc.Transformation();
      bool reversed = face.Orientation() == TopAbs_REVERSED;
      int base = static_cast<int>(m->positions.size() / 3);
      for (int i = 1; i <= tri->NbNodes(); ++i) {
        gp_Pnt p = tri->Node(i).Transformed(t);
        gp_Dir n = tri->HasNormals() ? tri->Normal(i) : gp_Dir(0, 0, 1);
        n.Transform(t);
        if (reversed) n.Reverse();
        m->positions.insert(m->positions.end(), {(float)p.X(), (float)p.Y(), (float)p.Z()});
        m->normals.insert(m->normals.end(), {(float)n.X(), (float)n.Y(), (float)n.Z()});
      }
      for (int i = 1; i <= tri->NbTriangles(); ++i) {
        int a, b, c;
        tri->Triangle(i).Get(a, b, c);
        if (reversed) std::swap(b, c);
        m->triangles.insert(m->triangles.end(), {base + a - 1, base + b - 1, base + c - 1});
        m->triangleFaces.push_back(fi);
      }
    }
    const auto& edges = s->map(CHF_EDGE);
    m->edgeOffsets.push_back(0);
    for (int ei = 1; ei <= edges.Extent(); ++ei) {
      TopoDS_Edge edge = TopoDS::Edge(edges.FindKey(ei));
      if (!BRep_Tool::Degenerated(edge)) {
        BRepAdaptor_Curve curve(edge);
        GCPnts_TangentialDeflection pts(curve, angularDeflection, linearDeflection, 2);
        for (int i = 1; i <= pts.NbPoints(); ++i) {
          gp_Pnt p = pts.Value(i);
          m->edgePoints.insert(m->edgePoints.end(), {(float)p.X(), (float)p.Y(), (float)p.Z()});
        }
      }
      m->edgeOffsets.push_back(static_cast<int>(m->edgePoints.size() / 3));
    }
    return m;
  });
}

void chf_mesh_free(chf_mesh* m) { delete m; }

void chf_mesh_counts(const chf_mesh* m, int* out4) {
  out4[0] = static_cast<int>(m->positions.size() / 3);
  out4[1] = static_cast<int>(m->triangles.size() / 3);
  out4[2] = static_cast<int>(m->edgePoints.size() / 3);
  out4[3] = static_cast<int>(m->edgeOffsets.size()) - 1;
}

void chf_mesh_copy(const chf_mesh* m, float* positions, float* normals, int* triangles, int* triangleFaces,
                   float* edgePoints, int* edgeOffsets) {
  std::copy(m->positions.begin(), m->positions.end(), positions);
  std::copy(m->normals.begin(), m->normals.end(), normals);
  std::copy(m->triangles.begin(), m->triangles.end(), triangles);
  std::copy(m->triangleFaces.begin(), m->triangleFaces.end(), triangleFaces);
  std::copy(m->edgePoints.begin(), m->edgePoints.end(), edgePoints);
  std::copy(m->edgeOffsets.begin(), m->edgeOffsets.end(), edgeOffsets);
}

}  // extern "C"
