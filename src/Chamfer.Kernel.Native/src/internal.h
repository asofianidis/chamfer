// Internal types shared by the shim's translation units.
#pragma once

#include "chamfer_kernel.h"

#include <BRepBuilderAPI_MakeShape.hxx>
#include <Standard_Failure.hxx>
#include <TopTools_IndexedDataMapOfShapeListOfShape.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Vertex.hxx>
#include <TopoDS_Wire.hxx>

#include <exception>
#include <functional>
#include <map>
#include <memory>
#include <string>

struct chf_shape {
  TopoDS_Shape shape;
  // Lazily built sub-shape maps, keyed by TopAbs_ShapeEnum.
  std::map<int, TopTools_IndexedMapOfShape> maps;

  explicit chf_shape(const TopoDS_Shape& s) : shape(s) {}
  const TopTools_IndexedMapOfShape& map(int kind);
};

// History-carrying operation result. Algorithms that are not
// BRepBuilderAPI_MakeShape subclasses supply custom history callbacks.
struct chf_op {
  TopoDS_Shape result;
  std::unique_ptr<BRepBuilderAPI_MakeShape> algo;  // may be null
  std::function<const TopTools_ListOfShape&(const TopoDS_Shape&)> modified;
  std::function<const TopTools_ListOfShape&(const TopoDS_Shape&)> generated;
  std::function<bool(const TopoDS_Shape&)> isDeleted;
  TopoDS_Shape first, last;  // caps of sweeps (extrude, revolve)
  chf_shape* resultHandle = nullptr;  // cached handle for index lookups, owned
  ~chf_op() { delete resultHandle; }
};

void chf_set_error(const std::string& message);
void chf_clear_error();

// Runs f, converting OCCT and C++ exceptions into chf_last_error.
template <typename T, typename F>
T chf_guard(T failValue, F&& f) {
  chf_clear_error();
  try {
    return f();
  } catch (const Standard_Failure& e) {
    std::string msg = e.GetMessageString() ? e.GetMessageString() : "";
    chf_set_error(std::string(e.DynamicType()->Name()) + (msg.empty() ? "" : ": " + msg));
  } catch (const std::exception& e) {
    chf_set_error(e.what());
  } catch (...) {
    chf_set_error("unknown native error");
  }
  return failValue;
}

// Wraps a finished algorithm into an op, or records why it failed.
chf_op* chf_make_op(std::unique_ptr<BRepBuilderAPI_MakeShape> algo, const char* what);
chf_op* chf_make_op_plain(const TopoDS_Shape& result);
