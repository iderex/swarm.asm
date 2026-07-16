# swarm.asm — masterplan

Product, architecture, and quality plan. **Status: in design** — the open
decisions below are settled through the architecture design issue before the
first kernel line is written; each decision is recorded here with its
rationale. This document is the single design authority; the milestones and
their issues carry the build order.

## Vision

A Particle Life engine whose entire simulation kernel is hand-written x64
assembly. Headline: **1,000,000 interacting particles at 60 fps, CPU-only, in
one small zero-dependency Windows executable.** The engine must be mesmerizing
to watch (live, interactive attraction matrix) and honest to measure (a
reproducible benchmark suite against existing CPU ports).

## Non-goals

- No GPU compute of any kind — the point is what a CPU can do.
- No scripting, plugins, or embedded runtimes — the trust boundary is the exe.
- No cross-platform abstraction layer; Windows x64 is the target. A port would
  be a separate platform layer, never an abstraction tax on the kernel.
- No networking, telemetry, or auto-update. The program touches the network
  never.

## Hard constraints (enforced by conformance tests)

| Constraint      | Rule                                                                 |
| --------------- | -------------------------------------------------------------------- |
| Imports         | `swarm.exe` imports only `kernel32.dll`, `user32.dll`, `gdi32.dll`   |
| No CRT          | no `msvcrt`/`vcruntime`/`ucrtbase` — startup is `start:`, not `main` |
| Kernel purity   | `src/kernel/` contains no API calls, no I/O, no hidden global state  |
| Contracts       | every routine has a truthful register-contract header                |
| Determinism     | same seed → same state, bit-exact per code path                      |
| Size budget     | `swarm.exe` ≤ 64 KB (revisited only by a documented decision)        |
| Fail-closed I/O | presets/config that do not validate are rejected, never half-applied |

## Force model (to be specified precisely in the design issue)

The classic Particle Life kernel: for each pair (i, j) within `r_max`, a force
along the connecting axis — strong distance-scaled repulsion inside `r_min`
(universal, matrix-independent), and between `r_min` and `r_max` a
attraction/repulsion with magnitude peaked mid-range and sign/strength taken
from `matrix[species_i][species_j]`. Semi-implicit Euler integration with
velocity damping. The design issue fixes: the exact piecewise force function,
units and ranges, damping constant, integration step, boundary behavior
(wrap vs. bounce), and the reference pseudocode the C# oracle implements.

## Open architecture decisions (settled via the design issue)

1. **World representation** — SoA layout (`x[]`, `y[]`, `vx[]`, `vy[]`,
   `species[]`): alignment, padding, max N, buffer ownership.
2. **Precision** — f32 throughout vs. f32 positions/f64 accumulation;
   FTZ/DAZ policy (determinism interacts here).
3. **Neighborhood structure** — uniform grid (cell size = `r_max`) layout,
   build strategy (counting sort per frame?), SIMD-friendly traversal order.
4. **Internal ABI** — register conventions for kernel-internal routines
   (custom, documented) vs. Win64 ABI at the platform/P-Invoke seams;
   callee-saved policy; vzeroupper discipline.
5. **Module structure** — `.inc` decomposition of kernel vs. platform;
   what assembles into the exe vs. the test DLL.
6. **Threading model** (M3) — tile ownership, phase barriers, worker pool via
   raw `CreateThread`, determinism strategy under parallel accumulation.
7. **AVX-512 dispatch** (M3) — CPUID gating, code-path selection, testing
   strategy on non-AVX-512 hardware.
8. **RNG** — owned deterministic generator (e.g. a small xorshift family),
   seeding, distribution mapping for initial placement.
9. **Rendering path** — DIB section + `StretchDIBits` vs. alternatives;
   particle → pixel mapping at 1M; color scheme per species.
10. **Preset format** — minimal text format for matrix/seed/parameters;
    fail-closed parser; versioning.
11. **Frame pacing** — fixed timestep + measured render, vsync interaction,
    what "60 fps" precisely means for the headline claim.
12. **Benchmark methodology** (M4) — competitors, scenes, measurement
    protocol, hardware disclosure.

## Quality gates (fixed)

- CI on every PR: assemble, smoke-run, `dotnet test` (reference equivalence,
  determinism goldens, conformance fitness tests), Prettier for docs.
- The adversarial lens gate on kernel/ABI/platform/parsing/build changes.
- CodeRabbit on every PR — every finding dispositioned before merge.
- `/bench` before and after every kernel change once the suite exists; the
  baseline lives in docs/BENCHMARKS.md.

## Milestones

| Milestone        | Acceptance criteria                                                                                         |
| ---------------- | ----------------------------------------------------------------------------------------------------------- |
| M0 — Foundation  | Masterplan decisions recorded; toolchain + CI green; harness runs a walking-skeleton kernel call end to end |
| M1 — First light | 50k particles, brute-force AVX2 kernel, live window, interactive matrix, ≥60 fps                            |
| M2 — Scale       | Uniform grid, 500k particles ≥60 fps, reference equivalence holds                                           |
| M3 — One million | Worker threads + AVX-512 path, 1M particles ≥60 fps on the reference machine                                |
| M4 — Launch      | Benchmark suite + recorded baselines, presets, README/write-up, v1.0                                        |

## Prior art (measured against, not copied)

- tom-mohr/particle-life-app (Java/OpenGL) — the flagship desktop app.
- hunar4321/particle-life (C++/JS) — the viral original.
- Existing SIMD particle work is C++ intrinsics; no pure-assembly engine
  exists — that implementation niche is the reason this project is worth
  building.
