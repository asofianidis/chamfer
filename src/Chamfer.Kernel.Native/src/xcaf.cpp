// STEP with product structure, names and colours (OCCT's XDE / XCAF).
#include "internal.h"
#include <Quantity_Color.hxx>
#include <STEPCAFControl_Writer.hxx>
#include <STEPControl_StepModelType.hxx>
#include <TDataStd_Name.hxx>
#include <TDF_Label.hxx>
#include <TDocStd_Document.hxx>
#include <TCollection_ExtendedString.hxx>
#include <TopLoc_Location.hxx>
#include <XCAFApp_Application.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_ColorType.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ShapeTool.hxx>
#include <gp_Trsf.hxx>
#include <stdexcept>
#include <vector>

int chf_write_step_assembly(int partCount, chf_shape* const* parts, const char* const* partNames, const unsigned* partColors,
                            int instanceCount, const int* instancePart, const double* matrices12, const char* const* instanceNames,
                            const char* assemblyName, const char* path) {
  return chf_guard(0, [&] {
    if (partCount <= 0 || instanceCount <= 0 || !path) return 0;
    Handle(XCAFApp_Application) app = XCAFApp_Application::GetApplication();
    Handle(TDocStd_Document) doc;
    app->NewDocument("MDTV-XCAF", doc);
    Handle(XCAFDoc_ShapeTool) shapes = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
    Handle(XCAFDoc_ColorTool) colors = XCAFDoc_DocumentTool::ColorTool(doc->Main());

    // Each distinct part once, with its name and colour.
    std::vector<TDF_Label> labels;
    for (int i = 0; i < partCount; ++i) {
      if (!parts[i] || parts[i]->shape.IsNull()) throw std::runtime_error("empty part shape");
      TDF_Label label = shapes->AddShape(parts[i]->shape, Standard_False);
      TDataStd_Name::Set(label, TCollection_ExtendedString(partNames[i], Standard_True));
      unsigned c = partColors[i];
      Quantity_Color color(((c >> 16) & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, (c & 0xFF) / 255.0, Quantity_TOC_sRGB);
      colors->SetColor(label, color, XCAFDoc_ColorSurf);
      labels.push_back(label);
    }

    // The assembly: one named, placed component per instance.
    TDF_Label assembly = shapes->NewShape();
    TDataStd_Name::Set(assembly, TCollection_ExtendedString(assemblyName ? assemblyName : "Assembly", Standard_True));
    for (int i = 0; i < instanceCount; ++i) {
      int part = instancePart[i];
      if (part < 0 || part >= partCount) throw std::runtime_error("instance refers to a missing part");
      const double* m = matrices12 + 12 * i;
      gp_Trsf t;
      t.SetValues(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11]);
      TDF_Label component = shapes->AddComponent(assembly, labels[part], TopLoc_Location(t));
      TDataStd_Name::Set(component, TCollection_ExtendedString(instanceNames[i], Standard_True));
    }

    shapes->UpdateAssemblies();
    STEPCAFControl_Writer writer;
    writer.SetColorMode(Standard_True);
    writer.SetNameMode(Standard_True);
    if (!writer.Transfer(doc, STEPControl_AsIs)) throw std::runtime_error("STEP transfer failed");
    if (writer.Write(path) != IFSelect_RetDone) throw std::runtime_error("STEP write failed");
    app->Close(doc);
    return 1;
  });
}

// ---------------------------------------------------------------- reading

#include <STEPCAFControl_Reader.hxx>
#include <TDF_LabelSequence.hxx>
#include <map>
#include <string>

struct chf_xcaf {
  std::vector<TopoDS_Shape> parts;
  std::vector<std::string> partNames;
  std::vector<unsigned> partColors;  // 0xFFFFFFFF when none
  std::vector<int> instPart;
  std::vector<gp_Trsf> instTrsf;
  std::vector<std::string> instNames;
};

namespace {

std::string nameOf(const TDF_Label& label) {
  Handle(TDataStd_Name) n;
  if (!label.FindAttribute(TDataStd_Name::GetID(), n)) return std::string();
  const TCollection_ExtendedString& e = n->Get();
  std::string out;
  for (int i = 1; i <= e.Length(); ++i) {
    auto c = static_cast<unsigned>(e.Value(i));
    if (c < 0x80) out += static_cast<char>(c);
    else if (c < 0x800) { out += static_cast<char>(0xC0 | (c >> 6)); out += static_cast<char>(0x80 | (c & 0x3F)); }
    else { out += static_cast<char>(0xE0 | (c >> 12)); out += static_cast<char>(0x80 | ((c >> 6) & 0x3F)); out += static_cast<char>(0x80 | (c & 0x3F)); }
  }
  return out;
}

unsigned colorOf(const Handle(XCAFDoc_ColorTool)& colors, const TDF_Label& label, const TopoDS_Shape& shape) {
  Quantity_Color c;
  if (colors->GetColor(label, XCAFDoc_ColorSurf, c) || colors->GetColor(label, XCAFDoc_ColorGen, c) ||
      colors->GetColor(shape, XCAFDoc_ColorSurf, c) || colors->GetColor(shape, XCAFDoc_ColorGen, c)) {
    Standard_Real r, g, b;
    c.Values(r, g, b, Quantity_TOC_sRGB);
    return (static_cast<unsigned>(r * 255 + 0.5) << 16) | (static_cast<unsigned>(g * 255 + 0.5) << 8) | static_cast<unsigned>(b * 255 + 0.5);
  }
  return 0xFFFFFFFFu;
}

void visit(chf_xcaf& out, std::map<int, int>& partIndex, const Handle(XCAFDoc_ShapeTool)& shapes, const Handle(XCAFDoc_ColorTool)& colors,
           const TDF_Label& label, const TopLoc_Location& loc, const std::string& instName, int depth) {
  if (depth > 32) throw std::runtime_error("assembly nesting too deep");
  if (XCAFDoc_ShapeTool::IsAssembly(label)) {
    TDF_LabelSequence components;
    XCAFDoc_ShapeTool::GetComponents(label, components);
    for (int i = 1; i <= components.Length(); ++i) {
      TDF_Label comp = components.Value(i), ref;
      if (!XCAFDoc_ShapeTool::GetReferredShape(comp, ref)) continue;
      std::string n = nameOf(comp);
      visit(out, partIndex, shapes, colors, ref, loc * XCAFDoc_ShapeTool::GetLocation(comp), n.empty() ? nameOf(ref) : n, depth + 1);
    }
    return;
  }

  TopoDS_Shape shape = XCAFDoc_ShapeTool::GetShape(label);
  if (shape.IsNull()) return;
  int key = label.Tag() * 100003 + label.Father().Tag();
  auto found = partIndex.find(key);
  int index;
  if (found == partIndex.end()) {
    index = static_cast<int>(out.parts.size());
    partIndex[key] = index;
    out.parts.push_back(shape.Located(TopLoc_Location()));
    std::string n = nameOf(label);
    out.partNames.push_back(n.empty() ? "Part" + std::to_string(index + 1) : n);
    out.partColors.push_back(colorOf(colors, label, shape));
  } else {
    index = found->second;
  }

  out.instPart.push_back(index);
  out.instTrsf.push_back((loc * shape.Location()).Transformation());
  out.instNames.push_back(instName.empty() ? out.partNames[index] : instName);
}

}  // namespace

chf_xcaf* chf_xcaf_read_step(const char* path) {
  return chf_guard<chf_xcaf*>(nullptr, [&]() -> chf_xcaf* {
    Handle(XCAFApp_Application) app = XCAFApp_Application::GetApplication();
    Handle(TDocStd_Document) doc;
    app->NewDocument("MDTV-XCAF", doc);
    STEPCAFControl_Reader reader;
    reader.SetColorMode(Standard_True);
    reader.SetNameMode(Standard_True);
    if (reader.ReadFile(path) != IFSelect_RetDone) throw std::runtime_error("couldn't read the STEP file");
    if (!reader.Transfer(doc)) throw std::runtime_error("STEP transfer failed");
    Handle(XCAFDoc_ShapeTool) shapes = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
    Handle(XCAFDoc_ColorTool) colors = XCAFDoc_DocumentTool::ColorTool(doc->Main());
    auto out = std::make_unique<chf_xcaf>();
    std::map<int, int> partIndex;
    TDF_LabelSequence roots;
    shapes->GetFreeShapes(roots);
    for (int i = 1; i <= roots.Length(); ++i) {
      visit(*out, partIndex, shapes, colors, roots.Value(i), TopLoc_Location(), nameOf(roots.Value(i)), 0);
    }
    app->Close(doc);
    if (out->parts.empty()) throw std::runtime_error("the STEP file has no shapes");
    return out.release();
  });
}

void chf_xcaf_free(chf_xcaf* x) { delete x; }

void chf_xcaf_counts(const chf_xcaf* x, int* out2) {
  out2[0] = static_cast<int>(x->parts.size());
  out2[1] = static_cast<int>(x->instPart.size());
}

chf_shape* chf_xcaf_part(const chf_xcaf* x, int i, unsigned* color) {
  *color = x->partColors[i];
  return new chf_shape(x->parts[i]);
}

const char* chf_xcaf_part_name(const chf_xcaf* x, int i) { return x->partNames[i].c_str(); }

int chf_xcaf_instance(const chf_xcaf* x, int i, double* m12) {
  const gp_Trsf& t = x->instTrsf[i];
  for (int r = 1; r <= 3; ++r)
    for (int c = 1; c <= 4; ++c) m12[(r - 1) * 4 + (c - 1)] = t.Value(r, c);
  return x->instPart[i];
}

const char* chf_xcaf_instance_name(const chf_xcaf* x, int i) { return x->instNames[i].c_str(); }
