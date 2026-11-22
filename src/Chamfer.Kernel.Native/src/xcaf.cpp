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
