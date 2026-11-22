#!/usr/bin/env bash
# Builds the subset of Open CASCADE Technology that Chamfer's native shim links
# against, and installs it into a prefix. Used locally and in CI.
#
#   build/occt/build-occt.sh <source-dir> <build-dir> <install-prefix>
#
# CMAKE overrides the cmake executable. OCCT_ARCH ("arm64" or "x86_64") sets
# CMAKE_OSX_ARCHITECTURES on macOS.
set -euo pipefail

src="$1"; bld="$2"; prefix="$3"
cmake="${CMAKE:-cmake}"

extra=()
if [[ "$(uname -s)" == "Darwin" ]]; then
  extra+=(-G "Unix Makefiles" -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0)
  if [[ -n "${OCCT_ARCH:-}" ]]; then extra+=(-DCMAKE_OSX_ARCHITECTURES="$OCCT_ARCH"); fi
fi

"$cmake" -S "$src" -B "$bld" "${extra[@]}" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$prefix" \
  -DINSTALL_DIR="$prefix" \
  -DBUILD_LIBRARY_TYPE=Shared \
  -DBUILD_MODULE_Draw=OFF \
  -DBUILD_MODULE_DETools=OFF \
  -DBUILD_DOC_Overview=OFF \
  -DBUILD_SAMPLES_QT=OFF \
  -DUSE_FREETYPE=OFF \
  -DUSE_TK=OFF \
  -DUSE_OPENGL=OFF \
  -DUSE_GLES2=OFF \
  -DUSE_D3D=OFF \
  -DUSE_VTK=OFF \
  -DUSE_TBB=OFF \
  -DUSE_FREEIMAGE=OFF \
  -DUSE_RAPIDJSON=OFF

jobs="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
"$cmake" --build "$bld" --config Release --parallel "$jobs"
"$cmake" --install "$bld" --config Release
