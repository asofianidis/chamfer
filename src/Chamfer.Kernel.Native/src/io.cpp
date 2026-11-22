// File exchange: STEP, IGES, STL and OCCT's native BRep text format.
#include "internal.h"

#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepTools.hxx>
#include <BRep_Builder.hxx>
#include <IFSelect_ReturnStatus.hxx>
#include <IGESControl_Reader.hxx>
#include <IGESControl_Writer.hxx>
#include <Interface_Static.hxx>
#include <STEPControl_Reader.hxx>
#include <STEPControl_Writer.hxx>
#include <StlAPI_Writer.hxx>

#include <cstdlib>
#include <cstring>
#include <sstream>

namespace {
chf_shape* checkedRead(const TopoDS_Shape& s, const char* what) {
  if (s.IsNull()) throw std::runtime_error(std::string(what) + ": no shapes in file");
  return new chf_shape(s);
}
}  // namespace

extern "C" {

int chf_write_step(const chf_shape* s, const char* path) {
  return chf_guard(0, [&] {
    STEPControl_Writer writer;
    Interface_Static::SetCVal("write.step.unit", "MM");
    Interface_Static::SetCVal("write.step.schema", "AP214IS");
    if (writer.Transfer(s->shape, STEPControl_AsIs) != IFSelect_RetDone) throw std::runtime_error("STEP transfer failed");
    if (writer.Write(path) != IFSelect_RetDone) throw std::runtime_error("couldn't write STEP file");
    return 1;
  });
}

chf_shape* chf_read_step(const char* path) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    STEPControl_Reader reader;
    if (reader.ReadFile(path) != IFSelect_RetDone) throw std::runtime_error("couldn't read STEP file");
    reader.TransferRoots();
    return checkedRead(reader.OneShape(), "STEP");
  });
}

int chf_write_iges(const chf_shape* s, const char* path) {
  return chf_guard(0, [&] {
    IGESControl_Writer writer("MM", 1);
    if (!writer.AddShape(s->shape)) throw std::runtime_error("IGES transfer failed");
    writer.ComputeModel();
    if (!writer.Write(path)) throw std::runtime_error("couldn't write IGES file");
    return 1;
  });
}

chf_shape* chf_read_iges(const char* path) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    IGESControl_Reader reader;
    if (reader.ReadFile(path) != IFSelect_RetDone) throw std::runtime_error("couldn't read IGES file");
    reader.TransferRoots();
    return checkedRead(reader.OneShape(), "IGES");
  });
}

int chf_write_stl(const chf_shape* s, const char* path, double deflection, int ascii) {
  return chf_guard(0, [&] {
    BRepMesh_IncrementalMesh mesher(s->shape, deflection, false, 0.35, false);
    StlAPI_Writer writer;
    writer.ASCIIMode() = ascii != 0;
    if (!writer.Write(s->shape, path)) throw std::runtime_error("couldn't write STL file");
    return 1;
  });
}

int chf_write_brep(const chf_shape* s, const char* path) {
  return chf_guard(0, [&] { return BRepTools::Write(s->shape, path) ? 1 : 0; });
}

chf_shape* chf_read_brep(const char* path) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    TopoDS_Shape shape;
    BRep_Builder b;
    if (!BRepTools::Read(shape, path, b)) throw std::runtime_error("couldn't read BRep file");
    return checkedRead(shape, "BRep");
  });
}

char* chf_to_brep(const chf_shape* s) {
  return chf_guard<char*>(nullptr, [&] {
    std::ostringstream os;
    BRepTools::Write(s->shape, os);
    std::string text = os.str();
    char* out = static_cast<char*>(std::malloc(text.size() + 1));
    std::memcpy(out, text.c_str(), text.size() + 1);
    return out;
  });
}

chf_shape* chf_from_brep(const char* text) {
  return chf_guard<chf_shape*>(nullptr, [&] {
    std::istringstream is(text);
    TopoDS_Shape shape;
    BRep_Builder b;
    BRepTools::Read(shape, is, b);
    return checkedRead(shape, "BRep");
  });
}

}  // extern "C"
