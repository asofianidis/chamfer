#include "internal.h"

#include <Message.hxx>
#include <Message_Messenger.hxx>
#include <Message_PrinterOStream.hxx>
#include <Standard_Version.hxx>
#include <TopExp.hxx>

namespace {
thread_local std::string g_lastError;

// OCCT prints progress and statistics to stdout by default; a library
// shouldn't, so drop the default printers once at load time.
struct QuietMessenger {
  QuietMessenger() {
    Message::DefaultMessenger()->RemovePrinters(STANDARD_TYPE(Message_PrinterOStream));
  }
} g_quiet;
}

void chf_set_error(const std::string& message) { g_lastError = message; }
void chf_clear_error() { g_lastError.clear(); }

const TopTools_IndexedMapOfShape& chf_shape::map(int kind) {
  auto it = maps.find(kind);
  if (it == maps.end()) {
    it = maps.emplace(kind, TopTools_IndexedMapOfShape()).first;
    TopExp::MapShapes(shape, static_cast<TopAbs_ShapeEnum>(kind), it->second);
  }
  return it->second;
}

chf_op* chf_make_op(std::unique_ptr<BRepBuilderAPI_MakeShape> algo, const char* what) {
  if (!algo->IsDone()) {
    chf_set_error(std::string(what) + " failed");
    return nullptr;
  }
  TopoDS_Shape result = algo->Shape();
  if (result.IsNull()) {
    chf_set_error(std::string(what) + " produced no shape");
    return nullptr;
  }
  auto op = new chf_op();
  op->result = result;
  BRepBuilderAPI_MakeShape* raw = algo.get();
  op->modified = [raw](const TopoDS_Shape& s) -> const TopTools_ListOfShape& { return raw->Modified(s); };
  op->generated = [raw](const TopoDS_Shape& s) -> const TopTools_ListOfShape& { return raw->Generated(s); };
  op->isDeleted = [raw](const TopoDS_Shape& s) { return raw->IsDeleted(s); };
  op->algo = std::move(algo);
  return op;
}

chf_op* chf_make_op_plain(const TopoDS_Shape& result) {
  auto op = new chf_op();
  op->result = result;
  static const TopTools_ListOfShape empty;
  op->modified = [](const TopoDS_Shape&) -> const TopTools_ListOfShape& { return empty; };
  op->generated = [](const TopoDS_Shape&) -> const TopTools_ListOfShape& { return empty; };
  op->isDeleted = [](const TopoDS_Shape&) { return false; };
  return op;
}

extern "C" {

const char* chf_last_error(void) { return g_lastError.c_str(); }

const char* chf_version(void) { return "chamfer_kernel 0.1 / OCCT " OCC_VERSION_COMPLETE; }

void chf_free_string(char* str) { free(str); }

}  // extern "C"
