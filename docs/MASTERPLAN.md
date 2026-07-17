# swarm.asm — masterplan

Product, architecture, and quality plan. **Status: decided** — the twelve
architecture decisions below were settled through the design pass on the
architecture issue before the first kernel line; each carries its rationale
and the rejected alternatives. This document is the single design authority;
the milestones and their issues carry the build order.

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

## Force model (pinned)

World: unit torus [0,1)², wrap only in v1 (a `flags` field is reserved for a
future bounce mode, which would need its own pinned reflection spec). Sign
convention: positive f attracts i toward j.

Parameters (all validated fail-closed at parse):

| name           | range        | default | notes                                                                                                                    |
| -------------- | ------------ | ------- | ------------------------------------------------------------------------------------------------------------------------ |
| `n`            | [1, 2^20]    | —       | padded internally to a multiple of 16, plus a 16-element tail                                                            |
| `species_n`    | [1, 8]       | —       | hard cap; 16 revisited only with the AVX-512 path                                                                        |
| `seed`         | u64          | —       | decimal or 0x-hex                                                                                                        |
| `rmax`         | (0, 0.25]    | 0.05    | interaction radius                                                                                                       |
| `beta`         | [0.05, 0.95] | 0.3     | repulsion/attraction knee                                                                                                |
| `dt`           | (0, 0.1]     | 0.02    | fixed timestep, one step per frame                                                                                       |
| `friction`     | [0, 1]       | 0.71    | canonical per-step velocity factor; a half-life is UI display sugar only — no pow/exp exists in the binary or the oracle |
| `force_scale`  | (0, 100]     | 10      | folded at init into the matrix rows and the repulsion constants                                                          |
| `matrix[i][j]` | [-1, 1]      | —       | species_n × species_n, asymmetric allowed                                                                                |

Derived: `rmax2 = rmax*rmax`, `vmax = rmax/dt` (per-axis speed clamp; it
guarantees per-step displacement ≤ rmax ≤ 0.25 < 0.5, so one wrap correction
and the minimum-image convention are always valid).

Pinned semantic rules:

- Self and coincident distinct particles contribute **zero** force; both are
  excluded by the single test `r2 > 0` (self gives dx = dy = 0 exactly, so
  r2 = +0). No lane-index or id comparison exists in the kernel.
- Minimum image on the unit torus: `d -= round_half_even(d)` — exact for all
  d in (-1,1); two instructions per axis (`vroundps` mode 0).
- Wrap after integration: `p = p - floor(p); if p >= 1.0: p = 0.0`. The second
  clause is load-bearing: in f32, `p - floor(p)` returns exactly 1.0 for
  p in (-2^-25, 0), which would corrupt cell binning and write past the
  framebuffer if left unpinned. 1.0 is torus-identical to 0. Belts remain at
  binning (AND with g-1) and at plot (min against w-1, h-1), but the
  canonicalization is the primary fix.
- Kernel lane hygiene (pinned order; NaN structurally impossible):
  `valid = (r2 < rmax2) & (r2 > 0) & tailmask`; then
  `r2' = blend(valid, r2, 1.0)` **before** the sqrt; then `r = sqrt(r2')`,
  `invr = 1/r` (always finite); then f is computed and masked (`f &= valid`);
  then `acc += (f*invr)*dx` with dx always finite because all padding holds
  pinned finite values (0 × finite = 0). Masking after the divide instead
  would poison every accumulator on frame 1; NaN padding would poison through
  the accumulate FMA.
- The asm kernel's j-iteration order is the cell-sorted run order (brute
  force: array order) with 8-lane partial sums and one pinned shuffle-reduce;
  the oracle's O(n²) minimum-image loop below is the semantic definition.
  Order differences are absorbed by the epsilon tier.

Equivalence tiers (the test contract):

- **(a) oracle epsilon** — particles matched **by id**, per-component
  minimum-image absolute tolerance 1e-5 for positions / 1e-4 for velocities,
  horizons S ∈ {1, 4, 8} steps (longer horizons rejected: chaotic ulp
  amplification makes them a flaky-test hazard).
- **(b) exact goldens** — asm vs asm per code path at steps {1, 60, 600},
  FNV-1a64 over id-ordered little-endian state bytes; the full-state .bin is
  retained for first-divergence localization.
- **(c) invariants** — NaN-free and |v| ≤ vmax over 1000 steps.

Reference pseudocode (the C# oracle implements this verbatim):

```
state: x[i], y[i], vx[i], vy[i] : f32 ; s[i] : int      # i = original id, 0..n-1
const: rmax2 = rmax*rmax ; vmax = rmax/dt               # f32
init:  g = splitmix64(seed)                             # u01(g) = (next(g) >> 40) * 2^-24
       for i in 0..n-1:
           x[i] = u01(g) ; y[i] = u01(g)                # 3 draws per particle, this order
           s[i] = ((next(g) >> 32) * species_n) >> 32
           vx[i] = 0 ; vy[i] = 0

step:                                                   # all arithmetic f32; oracle uses
  for i in 0..n-1:                                      # plain unfused ops, no FMA required
      fx = 0 ; fy = 0                                   # forces read the frozen pre-step state
      for j in 0..n-1:
          dx = x[j] - x[i] ; dx = dx - round_half_even(dx)   # unit-torus minimum image
          dy = y[j] - y[i] ; dy = dy - round_half_even(dy)
          r2 = dx*dx + dy*dy
          if r2 <= 0 or r2 >= rmax2: continue           # excludes self (r2 == +0), coincident,
          r  = sqrt(r2)                                 #   and out-of-range; strict bounds
          xn = r * (1.0/rmax)                           # in (0,1)
          if xn < beta: f = xn * (1.0/beta) - 1.0       # universal repulsion, in (-1,0)
          else:         f = a[s[i]][s[j]] * (1.0 - abs(2.0*xn - 1.0 - beta) * (1.0/(1.0-beta)))
          q = force_scale * f / r
          fx = fx + q*dx ; fy = fy + q*dy
      ax[i] = fx ; ay[i] = fy
  for i in 0..n-1:                                      # semi-implicit Euler, pinned order
      vx[i] = clamp(vx[i]*friction + ax[i]*dt, -vmax, vmax)
      vy[i] = clamp(vy[i]*friction + ay[i]*dt, -vmax, vmax)
      x[i] = wrap(x[i] + vx[i]*dt) ; y[i] = wrap(y[i] + vy[i]*dt)

wrap(p): p = p - floor(p)
         if p >= 1.0: p = 0.0                           # 1.0 reachable in f32; pinned to 0
         return p
```

## Architecture decisions

### 1. World representation

**Decision:** SoA f32 on the unit torus. Six per-particle arrays — `x, y, vx,
vy : f32`, `species : u32`, `id : u32` — in two **fixed-role banks**: bank IN
(cell-sorted, read by the step pass) and bank OUT (written by the fused
force+integrate pass; input to the next build). Roles never swap. All arrays
64-byte aligned; n padded to a multiple of 16 **plus an explicit 16-element
tail** per array; pad elements hold pinned finite values (x=y=vx=vy=0,
species=0, id=n..) and are excluded by count-derived masks — never by sentinel
values. `id` carries original particle identity through every sort permutation
(the oracle-matching and divergence-localization key). Species is u32 so it
loads directly as a `vpermps` index. Max N = 2^20. One arena, owned by the
caller (exe: one `VirtualAlloc`; harness: `NativeMemory.AlignedAlloc(..., 64)`);
`swarm_layout_bytes(params)` is a pure size function; a **512-byte arena
header** (64-aligned; 256 was the original estimate, but the validated 304-byte
`SwarmParams` copy — which the step pass reads the matrix and constants from —
does not fit in 256) holds magic, ABI version, the validated params copy, RNG
state, frame counter, cached padded_n and g, and the selected code-path id —
**all state lives in the arena**, no globals. Then, each `padded_n * 4 B` and
64-aligned by construction: bank OUT (x, y, vx, vy : f32; species, id : u32),
bank IN (same six), the per-particle cell-id array, and the `(g*g + 1) * 4 B`
cell-start prefix (both grid arrays are M2, allocated from M1 so the layout
stays stable). Size at 1M: 2 banks × 6 arrays × 4 B (~48 MB) + cell ids (4 MB)

- cell starts (~1 MB) ≈ 54 MB.

**Rationale:** Fixed-role banks delete ping-pong bookkeeping and any mid-frame
thread barrier (the sorted copy is the double buffer). The explicit +16 tail
exists because `(N+15) & ~15` alone adds zero padding whenever N ≡ 0 mod 16 —
including the headline N = 2^20 — so "unmasked loads are always safe" would be
false without it. Finite pinned pads are required by the NaN-hygiene rule.
`id[]` is non-negotiable: asm (FMA) and oracle positions differ by ulps, so a
boundary-straddling particle bins differently, the sort permutations diverge,
and an index-wise comparison misaligns wholesale — the oracle test is not
runnable without id matching. All-state-in-arena makes "no hidden global
state" a mechanical test (two interleaved arenas must equal two isolated
runs).

**Rejected:** an id-less layout (oracle seam cannot re-align after the first
sort); sentinel-valued or NaN padding (nondeterministic garbage respectively
accumulator poisoning); u8 species (saves 6 MB nobody needs, costs a
`vpmovzxbd` in the hottest loop); separate force-accumulator arrays (deleted
by the fused integrate pass, decision 3).

### 2. Precision

**Decision:** f32 everywhere, including accumulation. MXCSR pinned to
`0x9FC0` (FTZ=1, DAZ=1, all exceptions masked, round-to-nearest-even) at
three explicit places: every DLL export prologue (save caller MXCSR, load
pinned, restore on return — never poison the host runtime's FP state), the
exe main-thread init, and **every worker thread entry**. Kernel sources
assume the pinned MXCSR as a documented contract and are forbidden from
containing `ldmxcsr`/`stmxcsr` (source-scan test). Instruction whitelist:
only IEEE-correctly-rounded operations — add/sub/mul/div/sqrt/FMA/round/min/
max/compare/blend/permute/convert. `vrsqrtps`, `vrcpps`, all approximation
instructions, x87, and `rdtsc`/`rdrand`/`rdseed` are banned in `src/kernel/`,
enforced by a forbidden-mnemonic conformance scan.

**Rationale:** Per-particle sums have ≤ ~150 bounded terms; f32 error is
orders below the oracle epsilon, and f64 accumulation halves lane throughput
for nothing. FTZ/DAZ: friction decays velocities into the denormal range and
denormal assists cost ~100 cycles — a nondeterministic-**timing** hazard; the
flush itself is a deterministic function under a pinned MXCSR. The whitelist
is what makes "bit-exact per code path" also hold cross-vendor and across CI
runners: approximation-instruction lookup tables differ between Intel and
AMD. Worker-entry pinning is stated explicitly because it is the exact place
the contract must be airtight. The harness scrambles MXCSR before a call and
asserts an identical state hash plus a restored caller MXCSR.

**Rejected:** f64 accumulation (2× throughput cost, zero determinism
benefit); rsqrt+Newton (~2 ms/frame cheaper but vendor-specific bits — the
budget closes without it); leaving denormals enabled (timing pathology; the
.NET-side denormal mismatch is bounded ~1.2e-38 and invisible at epsilon,
documented).

### 3. Neighborhood structure

**Decision:** Uniform grid, **g = the largest power of two with
1/g ≥ rmax**, clamped to [4, 512]; cell id = `(cy << log2(g)) | cx`,
row-major; coordinate wrap by AND with g-1. Per-frame full rebuild by a
**serial stable counting sort** (histogram → exclusive prefix → stable
scatter, OUT → IN), with cell ids computed in the fused integrate pass of the
previous frame. After the sort, the 3×3 neighborhood of any particle is
**three contiguous runs** (one per grid row); the torus seam splits a run in
two (a ~15-instruction run emitter, hit for 2/g of columns). The force kernel
consumes `(base, count)` runs and nothing else — **brute force is the
degenerate case "one run = the whole array"**, which is how M1 ships without
any grid code. Inner loop: gather formulation, 1 broadcast i × 8 contiguous j
per iteration; the species coefficient via one `vpermps` from the
K-premultiplied matrix row; minimum image via `vroundps`; the pinned masking
order from the force-model section; **every run's final vector applies a
count-derived tail mask** (static sliding-window dword LUT); one
`vmovmskps`/`jz` all-miss skip branch (contributes zero either way —
determinism-safe; buys ~2× brute-force reach, ~never taken in grid mode). No
Newton-3rd-law pairing. 1×-i blocking is the baseline; 2×-i j-load
amortization is a measured upgrade, not a budget assumption.

**Rationale:** A power-of-two g makes `int(x * g)` exact exponent arithmetic —
the cross-implementation cell-border rounding hazard class is dead — without
constraining anything user-visible. The stable sort pins the full iteration
order as a pure function of (seed, params, step count) by induction. **Tail
masking is mandatory**: an unmasked over-read past a torus-seam run lands in
the next grid row's column 0 — a _genuine_ wrapped neighbor that passes the
physics mask and is then counted again by the split run; ~2/g of particles
would get deterministically wrong forces every frame. The count mask costs
~2 uops per run and closes that hole. The serial build costs ~4 ms at 1M
(the same-address histogram chain runs ~8-12 cycles/particle) — affordable,
and it deletes per-thread histograms, a 2-D cursor merge, and a barrier.
Newton-3rd pairing is not merely undesirable — the matrix is asymmetric
(`a[i][j] != a[j][i]`), so the pair force is not equal-and-opposite; the
"halve the work" intuition is simply wrong here. Budget, headline scene
(n = 1,000,000, rmax = 1/512, g = 512, ~3.8 particles/cell, k ≈ 12,
~34 candidates → ~6 vector groups/particle; divider-bound at ~12
cycles/group for `vsqrtps`+`vdivps`): force+integrate ≈ (72 + ~40
setup/reduce/integrate) cycles/particle ≈ 3.1 ms on 8 cores at 4.5 GHz;
build ≈ 4 ms serial; plot+clear ≈ 1.2 ms; blit ≈ 1-2 ms; **total ≈ 9.5-10.5
ms, ~1.6× margin against the 16.67 ms p99 line**, without SMT, approximation
instructions, or AVX-512. The dense scene (rmax = 1/256, k ≈ 48) lands at
≈ 14-15 ms — reported in the benchmark table; if it must hit 60, the named
contingency is the parallel scatter (decision 6), never approximation
instructions.

**Rejected:** grid-in-M1 (front-loads all of M2 into the first shipping
milestone); a parallel counting sort as v1 architecture (correct and kept
verbatim as the _contingency_ if the serial build misses its budget — at
these budgets it is optimization headroom, not a requirement);
temporal-coherence repair sorts (order-history-dependent); ghost/halo cells
(the seam run emitter is 15 instructions); bare over-read discipline without
tail masks (refuted above).

### 4. Internal ABI

**Decision:** Two tiers. **Seam tier** (DLL exports, exe entry, thread
entries, every call out to an OS API): full Win64 ABI with the **correct**
nonvolatile set — rbx, rbp, **rsi, rdi**, r12-r15, xmm6-15 — plus MXCSR
save/pin/restore, `vzeroupper` immediately before return, and stack args
(arg 5+) captured into registers **before** the one `and rsp, -32` realigns
the frame. **Kernel tier**: args in rcx, rdx, r8, r9 (+ r10, r11), no shadow
space, **all registers caller-owned except rsp**; justified because kernel
routines are pass-granular only — hot loops are FASM macros inlined into the
pass bodies and never call anything. rsp ≡ 0 mod 32 at kernel entries, so
all ymm spills are `vmovaps`; no frames, no unwind info in kernel code (no
SEH, no CRT; documented non-stack-walkable). `vzeroupper` is forbidden inside
`src/kernel/` (source scan). Every routine carries the truthful
register-contract header (in/out/clobbers/MXCSR precondition); a lint checks
presence and format, the review gate audits truthfulness.

**Rationale:** rsi and rdi are callee-saved in the Win64 ABI and the .NET JIT
uses both constantly — a seam that treats them as volatile corrupts managed
state intermittently, the worst-to-debug failure class; hence the set is
spelled out rather than paraphrased. The all-caller-owned kernel tier deletes
every push/pop from the kernel; it is safe precisely because the call surface
is ~6 pass-granular routines.

**Rejected:** paraphrased "match Win64" conventions (got the nonvolatile set
wrong on first statement — exactly why the set is pinned explicitly);
64-byte stack alignment (32 suffices for ymm; zmm spills in the AVX-512 path
get a local `and`); per-routine Win64 conformance inside the kernel (pure
overhead where no seam exists).

### 5. Module structure

**Decision:**

```
src/kernel/    abi.inc  cpuid.inc  init.inc  layout.inc  parse.inc  rng.inc  state.inc  step.inc  grid.inc  plot.inc
src/platform/  seam.inc (export / thread-entry MXCSR + Win64 seam frame)  pool.inc (M3)
                 window, DIB, msg loop, pacing, and file I/O are inline in src/swarm.asm
src/swarm.asm      = platform + kernel -> swarm.exe   (PE64 GUI, kernel32/user32/gdi32, <= 64 KB)
src/swarm_dll.asm  = kernel + seam shims -> swarm.kernel.dll (PE64 DLL; test artifact)
tests/Swarm.Tests  = C# xUnit v3 harness (oracle, goldens, conformance)
```

Both tops include the same kernel `.inc` files — the tested kernel is the
shipped kernel by construction. `src/kernel/` is conformance-scanned: no
imports, no API references, no writable data, no `ldmxcsr`/`vzeroupper`, no
forbidden mnemonics. Export surface (11 functions, Win64 ABI, all state
crossing the seam by **copy-out in original-id order** — the arena stays
opaque):

```
u32  swarm_version(void);                                  // ABI version, harness hard-asserts
u32  swarm_cpu_paths(void);                                // bit0 AVX2, bit1 AVX-512
u64  swarm_layout_bytes(const SwarmParams*);               // 0 = invalid (fail-closed)
i32  swarm_parse_preset(const u8* text, u32 len, SwarmParams* out);
i32  swarm_init(void* arena, u64 arena_bytes, const SwarmParams*);   // size-checked, fail-closed
i32  swarm_step(void* arena, u32 n_steps);                 // n x (build + pass(0, n))
i32  swarm_build(void* arena);                             // sort OUT -> IN (brute mode: copy)
i32  swarm_pass(void* arena, u32 first, u32 last);         // fused force+integrate, IN -> OUT
i32  swarm_read_state(void* arena, f32* x, f32* y, f32* vx, f32* vy, u32* species); // id-ordered
i32  swarm_plot(void* arena, u32* bgra, u32 w, u32 h);     // pure raster, golden-testable
void swarm_rng_fill(u64 seed, u64* out, u32 count);        // RNG oracle seam
```

`SwarmParams` is a fixed sequential struct (version, n, species_n, seed,
rmax, beta, dt, friction, force_scale, force_path 0=auto/1=AVX2/2=AVX-512/
3=scalar reference, flags, matrix[8][8]) mirrored 1:1 in C# with Pack=4.

**Rationale:** `swarm_build` + `swarm_pass` give phase-granular oracle
testing and — decisively — the **threading-decomposition seam**: `pass(0,n)`
vs `pass(0,k); pass(k,n)` must produce identical OUT, testable from M1 on
.NET threads before a single asm worker exists. Copy-out keeps the internal
layout out of the ABI; `swarm_init` takes the arena size so a short buffer
fails closed. A state-hash export is unnecessary — the harness computes
FNV-1a64 over `swarm_read_state` output.

**Rejected:** a wider export surface (separate force/integrate exports, a
hash export, a path-override export — all deletable: forces are observed
through post-pass state, the path override is a params field); a
zero-copy span-over-arena oracle seam (makes the memory layout a de-facto
public ABI; copy-out costs one export and O(N) per test call); shipping the
DLL to users (test artifact only).

### 6. Threading model (M3 — data layout committed now)

**Decision:** Only the fused force+integrate pass is parallel; build and plot
stay serial on the main thread. Determinism by construction: the pass is a
pure map (reads bank IN read-only, writes disjoint OUT[i]; each particle's
accumulation runs serially in its pinned run order), so results are
bit-identical for any thread count, assignment, and scheduling. Work
distribution: **chunked self-scheduling** — one shared counter, `lock xadd`,
chunk = a contiguous range of ~N/(8T) sorted indices. Pool: T-1 workers
(`CreateThread` once at startup; T = physical P-cores via
`GetLogicalProcessorInformationEx`, preset-overridable), main participates;
per-worker auto-reset event pairs (go/done), main `SetEvent`s all, workers
run chunks, main `WaitForMultipleObjects` — **one signal + one join per
frame, no mid-frame barrier** (fixed-role banks make it structurally
unnecessary). Workers pin MXCSR at entry. The kernel never sees threads: the
platform calls `swarm_pass(arena, first, last)` ranges. Matrix edits apply
between join and signal. Gate test wired from M1: pass-split invariance;
from M3: T=1 vs T=8 state-hash equality.

**Rationale:** Self-scheduling is ~10 lines, deterministic because the
assignment cannot affect gather results, and it absorbs hybrid P/E-core
imbalance that a static partition cannot. A work-weighted static partitioner
is machinery for nothing once any assignment yields identical bits. Serial
build inside M3 is the Amdahl trade: ~4 ms serial against a ~10 ms frame,
versus per-thread histograms + cursor merge + an extra barrier.
`WaitOnAddress` is banned — it lives in an api-set DLL, not in kernel32's
import surface; events are kernel32.

**Rejected:** work-weighted static bands and a parallel scatter as v1 (kept
as the named contingency; promoted only if the serial build measurably breaks
the budget); pairwise accumulation with per-thread force buffers (needs a
pinned reduction order plus ~8 MB/thread of traffic — a determinism liability
for a 2× the budget does not need); `WaitOnAddress` (import rule); hard
thread affinity (ideal-processor hint only).

### 7. AVX-512 dispatch

**Decision:** The step-pass loop is a FASM macro parameterized by lane width
and register file, instantiated twice (ymm/zmm). Detection once, at
`swarm_init`, at the seam — CPUID.1 OSXSAVE → `xgetbv(0)` XCR0[7:5] = 111b →
CPUID.7.0:EBX F+DQ+VL — and the resulting path id is stored **in the arena
header** (no hidden global). `force_path` in params can pin a path;
requesting AVX-512 on unsupported hardware fails closed at init. Nothing
ever falls through mid-run; AVX2 absent → message box + exit — there is no
_automatic_ fallback to the scalar path on AVX2-absent hardware. A
_selectable_ scalar path does exist (`force_path = 3`, the reference kernel
the tests and the bench run against); it is a caller-pinned choice, never an
automatic one. What AVX-512 buys: 16 lanes, k-register masking deletes
the blend chain and the tail-mask LUT (`bzhi`+`kmovw`), one `vpermps zmm`
covers 16 species (the only path to lifting the species-8 cap). Honest
expectation: the loop is divider-bound and divide throughput per element is
roughly constant across ymm/zmm on current cores — **+10-30%, not 2×; the 1M
budget must and does close on AVX2 + threads alone.** Testing: separate
goldens per path (the 16-lane reduction order differs — "bit-exact per code
path" anticipated exactly this); AVX-512 golden/oracle tests are skippable
when `swarm_cpu_paths()` lacks bit 1; an emulator (Intel SDE) is a documented
local dev aid, never a required CI claim — CI stays honest about what it
executed.

**Rationale:** Path-in-arena keeps two arenas independently pathable and
testable; the macro instantiation is ~50 lines for the second path. The gain
estimate is stated conservatively so the headline never depends on it.

**Rejected:** a function-pointer dispatch table in module data (hidden global
state); AVX-512 as a budget prerequisite (it is margin); requiring AVX-512BW
(u32 species needs no byte ops).

### 8. RNG

**Decision:** splitmix64, alone. One u64 state in the arena header; ~10
instructions; an exact 5-line C# mirror. Pinned mappings:
u01 = `(z >> 40) * 2^-24f` (top 24 bits into a 24-bit mantissa — exact, no
double rounding); bounded int = `((z >> 32) * n) >> 32` (multiply-shift,
branchless; bias ≤ 2^-29, documented-accepted). Pinned init order: for
i = 0..n-1 draw exactly x, y, species (3 draws, ascending i); velocities
start at 0. Matrix randomization draws from a domain-separated stream
(`seed XOR constant`), row-major, `a = 2*u01 - 1` — adding a consumer never
shifts existing streams. `swarm_rng_fill` exposes the raw u64 stream; the
harness asserts asm ≡ C# u64-for-u64 **before any physics test runs**.

**Rationale:** The RNG-first test ordering means every downstream divergence
is attributable to physics, not seeding.

**Rejected:** xoshiro/PCG (quality margin buys nothing for initial
placement); modulo species mapping (a division for a documented-negligible
bias improvement); warm-up discards (rules for nothing — splitmix64 needs
none).

### 9. Rendering path

**Decision:** One 32-bit top-down DIB section (`CreateDIBSection`), `BitBlt`
from a memory DC (vs `SetDIBitsToDevice`: both measured in M1, the faster one
pinned). Clear via `rep stosd` / NT stores (~0.3 ms at 1080p). Plot:
**serial**, 1 pixel per particle (a 2×2 splat is a preset toggle),
`px = min(int(x*w), w-1)` (belt behind the wrap canonicalization), color from
an 8-entry BGRA species palette; last-write-wins in cell-sorted order — the
framebuffer is deterministic and `swarm_plot` (a pure kernel routine writing
a caller buffer) gets golden-image hash tests. Cell-sorted order makes
plotting sweep the framebuffer near-scanline — ~0.8-1 ms at 1M instead of 1M
random cache-line round trips. The matrix UI/HUD is GDI in the platform
layer, outside the determinism surface: n × n colored cells, wheel/drag
edits a[i][j] in [-1,1], applied at step boundaries only.

**Rationale:** The plot stays serial because "race-free by construction"
parallel-raster schemes fail here: cell rows map to _fractional_ pixel rows,
band boundaries fall mid-pixel-row, and adjacent threads can hit the same
pixel — timing-ordered last-write-wins, silently diverging the exe from the
`swarm_plot` goldens. Serial costs ~1 ms at 1M and the budget absorbs it. A
threaded raster returns only with a genuinely pixel-row-owned decomposition,
designed and gated on its own.

**Rejected:** a threaded raster in v1 (a confirmed race at every band seam);
additive blending in the headline scene (doubles traffic; exists later as a
toggle); trails/fade in v1 (a 2M-pixel pass for aesthetics); StretchDIBits
scaling (1:1 blit, window sized to the buffer).

### 10. Preset format

**Decision:** Line-based ASCII, version line first (`swarm 1`, any other
version rejected), `key value` pairs, `matrix` block last:

```
swarm 1
n 1000000
species 6
seed 0x1D
rmax 0.001953
beta 0.3
dt 0.02
friction 0.71
force 10.0
matrix
 0.50 -0.20 ...      (species_n rows x species_n columns, each in [-1,1])
end
```

Numbers: integers decimal (seed also 0x-hex); floats `[-]digits[.digits]`,
≤ 6 fraction digits, **no exponent form, no inf/nan**. Pinned decimal→f32:
integer mantissa + power of ten, one f64 divide, one rounding to f32
(documented ≤ 1 ulp double rounding; the oracle mirrors it). **Fail-closed
two-phase commit**: phase 1 tokenizes and validates everything into a
stack-local staging struct — every key required exactly once (seen-bitmask),
all ranges per the force-model table checked; phase 2 is a single memcpy
executed only on full success. Any error → negative code + line number,
output untouched. The parser is pure kernel code over a memory buffer
(`swarm_parse_preset` is the fuzz seam); file reading is platform. `friction`
is the canonical per-step factor — no `half_life` key exists.

**Rationale:** A tiny grammar means a tiny parser and a small fuzz surface.
Making the per-step factor canonical deletes the exp2 problem entirely —
every half-life parameterization hides a transcendental routine plus an
oracle-bit-agreement hazard; this parameterization removes the routine from
existence. Fuzz property asserted by tests: random bytes never crash and
never partially apply.

**Rejected:** `half_life` in the file (forces exp2 somewhere); dt as a
rational pair (the pinned decimal parse already removes float ambiguity);
exponent notation and comments in v1 (parser states for nothing); world-size
keys (grid internals leaking into the user grammar — the unit world has no
size keys at all).

### 11. Frame pacing

**Decision:** Fixed timestep, exactly one `swarm_step` per rendered frame, no
accumulator, no catch-up. If the machine falls behind, the animation slows;
the state sequence never changes — simulation state after k frames is a pure
function of (seed, params, k) on every machine. Interactive matrix edits
commit only at step boundaries as (frame_no, edit) events, so an interactive
session is definitionally a deterministic replay of its edit log. Pacing:
`CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)` + QPC to the
next 16.667 ms deadline; skip the wait if past. No vsync exists in GDI and
dwmapi/winmm are outside the import allowlist — tearing is accepted and
disclosed. **The headline claim, pinned:** on the disclosed reference
machine, the published benchmark preset+seed runs 3600 consecutive frames
(600 warm-up discarded), unpaced, QPC-timestamped, with **p99 frame time
(step + plot + blit) ≤ 16.67 ms**; mean fps and a per-phase breakdown
(build / pass / plot / blit) recorded alongside.

**Rationale:** Catch-up stepping makes state depend on timing — a determinism
kill. The p99 form is measurable and attributable; the per-phase breakdown
makes regressions assignable.

**Rejected:** a vsync-on interactive default (unimplementable under the
import rule); "zero frames over 33.3 ms" claims (a promise about the Windows
scheduler, not this code); timestep accumulators.

### 12. Benchmark methodology (M4)

**Decision:** Competitors: tom-mohr/particle-life-app (Java/OpenGL) and
hunar4321/particle-life (C++/JS), measured on the same machine, same particle
counts, simulation-step time where the competitor exposes it (rendering
stacks differ; the honest comparison is simulation throughput, disclosed as
such). Scenes: the pinned headline preset (1M, rmax = 1/512, k ≈ 12) **and**
the dense preset (1M, rmax = 1/256, k ≈ 48), both published as preset files
with seeds — density is disclosed, never implied. Protocol: bench mode
(`swarm.exe -bench preset.txt 3600`) runs unpaced, writes
min/avg/p50/p99/max per phase to a results file via `CreateFile`/`WriteFile`;
three runs, worst run reported. Hardware disclosure: exact CPU model, core
configuration used, memory configuration, Windows build, and which code path
(AVX2/AVX-512) executed. Baselines live in docs/BENCHMARKS.md; `/bench` runs
before and after every kernel change once the suite exists.

**Rationale:** Two-scene reporting prevents a sparse headline being misread
against denser rivals; worst-of-three and p99 keep the claim conservative.

**Rejected:** a single-scene headline (density cherry-pick exposure);
fps-only reporting (hides phase regressions); cross-machine claims (one
disclosed reference machine only).

## Open risks (verify empirically)

1. **Divider throughput constant.** The force budget hangs on ~12 cycles per
   8-candidate group for `vsqrtps`+`vdivps`; this varies by microarchitecture.
   Probe: the kernel PR ships an isolated inner-loop microbench
   (cycles/candidate over 1e9 candidates); if measured > 14, the fallback is
   software-pipelining two j-groups per i — never approximation instructions.
2. **Serial build cost at 1M.** The histogram pass's same-address dependent
   chain on near-sorted input is estimated 8-12 cycles/particle; materially
   above ~4.5 ms erodes the frame margin. Probe: per-pass timing at 500k and
   1M (M2). Contingency: the per-thread per-bucket-cursor parallel scatter.
3. **Scatter locality under energetic scenes.** The ~1.5-2 ms scatter
   estimate assumes temporal coherence; a hot matrix at the v_max clamp
   degrades write locality. Probe: an adversarial preset (all |a| = 1, high
   force) vs the coherent scene; fallback is a two-pass radix (cell row, then
   cell).
4. **p99 under Windows/GDI jitter.** ~10 ms p50 leaves ~6 ms for scheduler
   and GDI noise on the p99 claim. Probe: run the 3600-frame histogram early
   (M1 scale, again at each milestone) so the reference machine's jitter
   floor is known before the 1M claim is due.
5. **Oracle epsilon horizon.** 1e-5 absolute at S = 8 for FMA-asm vs
   unfused-oracle rests on assumed divergence growth. Probe: measure empirical
   per-step divergence growth at n = 4096 across 100 seeds; tighten tolerances
   or horizons from data, recorded in the test with the measurement.
6. **Wrap canonicalization completeness.** The p ≥ 1.0 pin is proven for the
   floor-subtract path; other producers of boundary values (clamp
   interactions, minimum image at exactly 0.5) need an empirical sweep.
   Probe: a property test driving positions and velocities through the 0/1
   and 0.5 boundaries, asserting binning, plot bounds, and oracle agreement.
7. **AVX-512 frequency licensing on older parts.** May make the AVX-512 path
   a net loss pre-Ice-Lake. Probe: M3 measures both paths per machine; the
   auto path default is chosen from measurement, and the bench table reports
   which path ran.

## Quality gates (fixed)

- CI on every PR: assemble, smoke-run, `dotnet test` (RNG exactness,
  reference equivalence, determinism goldens, conformance fitness tests),
  Prettier for docs.
- An adversarial code review on kernel/ABI/platform/parsing/build changes —
  the review of record; every finding gets a documented fix or a reasoned
  decline before merge.
- Benchmarks re-run before and after every kernel change, once the suite
  exists; the baseline lives in docs/BENCHMARKS.md.

## Milestones

| Milestone        | Acceptance criteria                                                                                         |
| ---------------- | ----------------------------------------------------------------------------------------------------------- |
| M0 — Foundation  | Masterplan decisions recorded; toolchain + CI green; harness runs a walking-skeleton kernel call end to end |
| M1 — First light | 8,192 particles, brute-force AVX2, single-threaded, live window, interactive matrix, ≥ 60 fps (p99)         |
| M2 — Scale       | Uniform grid; 50k and 500k particles ≥ 60 fps; brute-vs-grid cross-check green                              |
| M3 — One million | Worker threads + AVX-512 path; 1M particles ≥ 60 fps (p99) on the reference machine                         |
| M4 — Launch      | Benchmark suite + recorded baselines (headline + dense scene), presets, write-up, v1.0                      |

**M1 amendment (recorded 2026-07-16):** M1 was originally "50k brute force
≥ 60 fps". That is arithmetically infeasible: 50k² = 2.5e9 candidate pairs
per frame at the divider-bound ~1.3-1.4 cycles/candidate is ~0.7 s/frame
single-threaded — ~47× over budget; even the all-miss skip path leaves
~0.35 s. This is machine physics, not code quality. M1's acceptance count is
therefore 8,192 particles, though ≥ 60 fps at that count is not yet met on one
thread: the early budget here projected ~12 ms/frame (~1.4× margin), but it
assumed an all-miss skip the AVX2 pass never takes — the vector pass evaluates
every lane and masks, with no early exit — so the projection was optimistic.
The measured brute-force AVX2 pass is ~52.8 ms (~19 fps) at n = 8192 on the
reference machine (docs/BENCHMARKS.md), ~3× short of 60 fps single-threaded;
closing that gap is the M2 candidate-set reduction and/or the M3 worker pool.
The M1 acceptance preset pins rmax ≤ 0.05 because the cost is rmax-dependent.
The 50k ≥ 60 fps line moves to M2, where the grid delivers it with two orders
of magnitude to spare. Pulling the grid into M1 was rejected: it front-loads all of M2 into
the first shipping milestone. The brute kernel is not throwaway — it is the
degenerate one-run case of the same loop and stays forever as the grid's
same-binary cross-check oracle.

## Prior art (measured against, not copied)

- tom-mohr/particle-life-app (Java/OpenGL) — the flagship desktop app.
- hunar4321/particle-life (C++/JS) — the viral original.
- Existing SIMD particle work is C++ intrinsics; no pure-assembly engine
  exists — that implementation niche is the reason this project is worth
  building.
