#!/usr/bin/env bash
# Builds the chamfer_kernel shim against an OCCT install and stages it, plus
# the OCCT shared libraries it needs, into artifacts/native/<rid>/ where
# Chamfer.Kernel.Occt picks them up.
#
#   build/native/build-native.sh <rid> <occt-install-prefix>
#
# CMAKE overrides the cmake executable. If the OCCT prefix doesn't exist yet,
# OCCT_VERSION (default V7_9_3) is cloned and built there first.
set -euo pipefail

rid="$1"
occt="$2"
root="$(cd "$(dirname "$0")/../.." && pwd)"
cmake="${CMAKE:-cmake}"
out="$root/artifacts/native/$rid"
bld="$root/artifacts/obj/native/$rid"
occt_version="${OCCT_VERSION:-V7_9_3}"
case "$occt" in
  /*|?:*) ;;
  *) occt="$root/$occt" ;;
esac

arch=""
case "$rid" in
  osx-arm64) arch="arm64" ;;
  osx-x64) arch="x86_64" ;;
esac

if [[ ! -d "$occt" ]]; then
  src="$root/artifacts/obj/occt-src"
  if [[ ! -d "$src" ]]; then
    git clone --depth 1 --branch "$occt_version" https://github.com/Open-Cascade-SAS/OCCT.git "$src"
  fi
  OCCT_ARCH="$arch" CMAKE="$cmake" "$root/build/occt/build-occt.sh" "$src" "$root/artifacts/obj/occt-build/$rid" "$occt"
fi

config_dir="$(dirname "$(find "$occt" -name OpenCASCADEConfig.cmake | head -n 1)")"
if [[ -z "$config_dir" || "$config_dir" == "." ]]; then
  echo "OpenCASCADEConfig.cmake not found under $occt" >&2
  exit 1
fi

extra=()
if [[ "$(uname -s)" == "Darwin" ]]; then
  extra+=(-G "Unix Makefiles" -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0)
  if [[ -n "$arch" ]]; then extra+=(-DCMAKE_OSX_ARCHITECTURES="$arch"); fi
fi

"$cmake" -S "$root/src/Chamfer.Kernel.Native" -B "$bld" "${extra[@]}" \
  -DCMAKE_BUILD_TYPE=Release -DOpenCASCADE_DIR="$config_dir" -DCMAKE_INSTALL_PREFIX="$out"
"$cmake" --build "$bld" --config Release --parallel
mkdir -p "$out"
"$cmake" --install "$bld" --config Release

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    find "$occt" -path "*/bin/TK*.dll" -exec cp -f {} "$out/" \;
    ;;
  Darwin)
    libdir="$(dirname "$(find "$occt" -name 'libTKernel*.dylib' | head -n 1)")"
    cp -a "$libdir"/libTK*.dylib "$out/"
    # Make every OCCT library find its siblings next to itself.
    for f in "$out"/libTK*.dylib "$out"/libchamfer_kernel.dylib; do
      [[ -L "$f" ]] && continue
      chmod u+w "$f"
      if [[ "$(basename "$f")" == libTK* ]]; then
        install_name_tool -id "@rpath/$(basename "$(otool -D "$f" | tail -n 1)")" "$f"
      fi
      otool -L "$f" | tail -n +2 | awk '{print $1}' | grep 'libTK' | while read -r dep; do
        install_name_tool -change "$dep" "@rpath/$(basename "$dep")" "$f"
      done
      install_name_tool -add_rpath "@loader_path" "$f" 2>/dev/null || true
      codesign --force --sign - "$f"
    done
    ;;
  *)
    libdir="$(dirname "$(find "$occt" -name 'libTKernel.so*' | head -n 1)")"
    cp -a "$libdir"/libTK*.so* "$out/"
    ;;
esac

echo "Native kernel staged in $out"
