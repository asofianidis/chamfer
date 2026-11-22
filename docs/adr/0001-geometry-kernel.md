# 1. Open CASCADE behind a flat C shim

- Status: accepted
- Date: 2026-11-22

## Context

Parametric solid modelling needs a B-rep kernel: booleans, fillets, shells,
offsets, sweeps, lofts, tessellation and STEP/IGES exchange. Writing one is a
multi-decade effort. Open CASCADE Technology (OCCT) is the only mature,
open-source option, and its LGPL-2.1 licence (with the OCCT exception) allows
dynamic linking from an MIT-licensed application.

OCCT is a large C++ library. C# can't call C++ directly, and binding generators
(SWIG and friends) expose thousands of classes we don't need and tie every
caller to OCCT's API.

## Decision

- `Chamfer.Kernel.Native` is a small C++ library exposing a **flat C ABI**
  (`chamfer_kernel.h`): opaque handles for shapes, operations and meshes,
  1-based sub-shape indices, and thread-local error messages. Every entry point
  catches OCCT and C++ exceptions.
- Operations return an **operation handle that carries modelling history**
  (`Modified`, `Generated`, `IsDeleted`, and start/end caps for sweeps).
  Persistent naming is built on it (see ADR 3).
- `Chamfer.Kernel.Occt` wraps the ABI with `LibraryImport` and `SafeHandle`s.
  Nothing above it sees OCCT types, so the kernel could be swapped by
  reimplementing the C ABI.
- OCCT is built from source by `build/occt/build-occt.sh` (shared libraries,
  modelling and data-exchange modules, no Draw, no OpenGL, no FreeType) and
  cached in CI per version and platform. `build/native/build-native.sh` builds
  the shim and stages it with the OCCT libraries in `artifacts/native/<rid>/`,
  where the managed project picks them up. On macOS the script rewrites install
  names to `@rpath` with `@loader_path` so the libraries load from the app
  folder.

## Consequences

- Building from a clean checkout needs CMake and a C++ toolchain once.
- The C ABI grows with features, but stays small: each kernel capability is one
  function, not a mirror of OCCT's class hierarchy.
- OCCT's robustness limits (fillet and shell failures on hard geometry) surface
  as `KernelException`s that features turn into readable errors.
