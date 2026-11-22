cmake_minimum_required(VERSION 3.24)
project(chamfer_kernel LANGUAGES CXX)

# Flat C ABI over Open CASCADE Technology, consumed by Chamfer.Kernel.Occt via
# P/Invoke. Point OpenCASCADE_DIR at an OCCT install's cmake folder
# (build/native/build-native.sh does this).

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)

find_package(OpenCASCADE REQUIRED CONFIG)

add_library(chamfer_kernel SHARED
  src/common.cpp
  src/shape.cpp
  src/build.cpp
  src/ops.cpp
  src/mesh.cpp
  src/io.cpp
  src/hlr.cpp
  src/heal.cpp
  src/xcaf.cpp
)

target_include_directories(chamfer_kernel
  PUBLIC include
  PRIVATE ${OpenCASCADE_INCLUDE_DIR})
target_compile_definitions(chamfer_kernel PRIVATE CHF_BUILDING)

target_link_libraries(chamfer_kernel PRIVATE
  TKernel TKMath TKG2d TKG3d TKGeomBase TKBRep TKGeomAlgo TKTopAlgo TKPrim
  TKBO TKBool TKFillet TKOffset TKFeat TKMesh TKShHealing TKHLR
  TKXSBase TKDE TKDESTEP TKDEIGES TKDESTL
  TKXCAF TKLCAF TKCAF TKCDF)

if(MSVC)
  target_compile_options(chamfer_kernel PRIVATE /W3 /EHa /utf-8 /permissive-)
endif()

if(APPLE)
  set_target_properties(chamfer_kernel PROPERTIES
    INSTALL_RPATH "@loader_path"
    BUILD_WITH_INSTALL_RPATH ON
    MACOSX_RPATH ON)
elseif(UNIX)
  set_target_properties(chamfer_kernel PROPERTIES
    INSTALL_RPATH "$ORIGIN"
    BUILD_WITH_INSTALL_RPATH ON)
endif()

install(TARGETS chamfer_kernel RUNTIME DESTINATION . LIBRARY DESTINATION .)
