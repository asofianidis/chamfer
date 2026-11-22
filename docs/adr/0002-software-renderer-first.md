# 2. Software rendering first, GPU later

- Status: accepted
- Date: 2026-11-22

## Context

The 3D view must work on Windows and macOS, inside Avalonia, and in headless
tests. OpenGL inside Avalonia means ANGLE (GLES) on Windows and a deprecated
desktop GL on macOS; Metal and Vulkan need separate backends. None of these run
in the headless test platform, so the view couldn't be tested or screenshotted
in CI.

## Decision

`Chamfer.Viewport` describes what to draw as a backend-neutral `Scene`
(meshes with per-face pick ids, polylines, points and translucent quads) and
ships a **multithreaded software rasterizer** as the first backend. It renders
into a BGRA buffer with a depth buffer and two ID buffers: faces, and overlays
(edges, sketch curves, points) drawn a few pixels wide so they're easy to hit.
Picking reads the ID buffers, so it is pixel-exact and needs no ray casting.

## Consequences

- The view works everywhere Avalonia does, including headless, and tests
  check picks and pixels directly.
- Frame time scales with pixels × triangles. Typical parts render in a few
  milliseconds; very large models and assemblies will need the GPU backend,
  which will consume the same `Scene` and implement the same picking contract.
