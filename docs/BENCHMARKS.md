# Benchmarks

Measured numbers for the swarm.asm kernel, with the methodology that produced
them. A performance claim anywhere in the repo points here; an unmeasured
performance claim is not allowed.

Numbers are **per-machine and never compared across hardware** — every baseline
row carries the CPU, the feature path, the particle count, the seed, the
commit, and the date. Re-run on your own machine before drawing a conclusion.

## What is measured

One **force + integrate pass** (`swarm_pass` over the whole population): the
O(n²) brute-force interaction loop that dominates the frame. We time the pass
in isolation — `swarm_build` once to freeze the IN bank, then repeat the pass
over that frozen bank — so the measured work is identical on every iteration
and carries none of the bank-swap or copy cost a full `swarm_step` would fold
in. Two code paths are compared at each particle count: the scalar reference
(`force_path = 3`) and the AVX2 gather path (`force_path = 1`).

Not yet measured here (tracked on #5, milestone M4): the end-to-end
`swarm.exe` frame-time capture (mean / p99 fps at a fixed seed and count), the
1,048,576-particle headline, and regression gating against a stored baseline.

## How to run

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run -c Release --project tests\Swarm.Bench\Swarm.Bench.csproj
```

The harness assembles the kernel first (via `build.ps1`, so the benchmarked
DLL is the shipping DLL), then drives it through P/Invoke.

### Why a hand-rolled harness and not BenchmarkDotNet

BenchmarkDotNet compiles and launches a **fresh host process** per benchmark.
On the dev machine that fresh, unsigned PE is exactly what Device Guard / Smart
App Control blocks (`0x800711C7`) — the same reason the test suite runs
in-process under the trusted `dotnet` host (MTP, not VSTest). So the benchmark
runs **in-process** too: a dependency-free min-of-rounds `Stopwatch` loop, no
NuGet package, no lock file, no spawned process. It reports the **minimum**
per-pass time over 9 rounds (after a 3-pass warm-up), each round sized to run
for **at least ~120 ms** — `max(⌊120 ms / one pass⌋, 1)` passes, so at the
larger counts, where a single pass already exceeds 120 ms, a round is one pass.
The minimum, not the mean: a force pass is a fixed amount of arithmetic, so the
fastest observed round is the one least perturbed by scheduling and clock
transitions — the honest lower bound on the kernel's cost. The harness pins
neither process priority nor thread affinity, so reruns vary by a few percent
(more at the small counts); the recorded table is one clean run — reproduce on
your own machine before drawing a conclusion.

The Smart App Control config-flip quirk applies here too: if a run fails to
load the DLL with `0x800711C7`, re-run with `-c Debug`.

## Baseline

| CPU           | Path   | n     | ms / pass | speedup | interactions/s |
| ------------- | ------ | ----- | --------- | ------- | -------------- |
| Ryzen 9 5950X | scalar | 1024  | 1.556     | —       | 673.9 M        |
| Ryzen 9 5950X | AVX2   | 1024  | 0.904     | 1.72×   | 1160.4 M       |
| Ryzen 9 5950X | scalar | 2048  | 6.163     | —       | 680.5 M        |
| Ryzen 9 5950X | AVX2   | 2048  | 3.435     | 1.79×   | 1221.0 M       |
| Ryzen 9 5950X | scalar | 4096  | 24.583    | —       | 682.5 M        |
| Ryzen 9 5950X | AVX2   | 4096  | 13.375    | 1.84×   | 1254.4 M       |
| Ryzen 9 5950X | scalar | 8192  | 98.077    | —       | 684.2 M        |
| Ryzen 9 5950X | AVX2   | 8192  | 52.765    | 1.86×   | 1271.8 M       |
| Ryzen 9 5950X | scalar | 16384 | 391.980   | —       | 684.8 M        |
| Ryzen 9 5950X | AVX2   | 16384 | 209.493   | 1.87×   | 1281.4 M       |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11, single-threaded.
- **Feature path**: AVX2 + FMA (this CPU reports no AVX-512).
- **Seed / preset**: `0x5EED`, 6 species, `rmax = 0.05`, varied attraction
  matrix. Positions are the **initial frame** (uniform-random, zero velocity) —
  the sparsest configuration, so the scalar numbers here are a floor, not a
  steady-state average (see "Reading the numbers"). interactions/s counts the
  n² candidate pairs a pass evaluates, not the force evaluations it performs.
- **Commit**: `e134c9a` (the kernel under test; the bench harness itself lands
  in a later commit — the kernel binary is identical) · **Date**: 2026-07-17.

## Reading the numbers

**The AVX2 path is ~1.7–1.9× the scalar path — not ~8×** (1.72× at n = 1024,
rising to 1.87× at n = 16384). An 8-wide vector kernel naïvely "should" be 8×;
it is not, and the gap is the honest, useful result of measuring. Two things
account for it, and neither is a gather:

1. **The two paths do not do the same work.** The scalar reference rejects a
   candidate pair _before_ the expensive force math whenever it is out of range
   (`r² ≥ rmax²` → skip). On the unit torus at `rmax = 0.05` only ~0.8% of the
   n² candidate pairs are in range, so the scalar path runs the full
   sqrt/divide/matrix-lookup on under one pair in a hundred. The AVX2 path has
   no such early exit: it evaluates the whole force formula for all eight lanes
   and masks the out-of-range ones to zero. So the vector path computes the
   real force ~100× more often than the scalar path and _still_ finishes ~1.85×
   faster — the ratio is not "same work, 8× faster", it is what remains after
   the vector path pays for the pairs the scalar path skipped. A denser preset
   (larger `rmax`) narrows the scalar skip and shifts the ratio.
2. **The vector force loop is divider-bound.** Its cost is set by the `vsqrtps`
   - `vdivps` in the force formula (the masterplan's own analysis, decision 3),
     not by an 8-wide ALU ideal — and not by the neighbour loads, which are
     already contiguous `vmovaps` in the brute-force layout (there is no
     `vgatherdps` in the kernel). Eight-wide divide/sqrt throughput on Zen 3, not
     load width, is the ceiling.

Two consequences on the roadmap:

1. **The large-N win is a smaller candidate set, not a load-layout change.**
   Once the M2 spatial grid sorts particles into cells, a pass evaluates only
   the O(n·k) neighbours actually within `rmax` instead of all n² candidates —
   that is what collapses the work. It also removes the scalar path's cheap
   skip advantage (a cell's neighbours are mostly in range), so the AVX2 ratio
   should climb well past 2×.
2. **The shared integrate/store tail still uses SSE encoding** inside the AVX2
   pass. It runs once per particle, after the n-iteration VEX inner loop, so
   the VEX↔SSE transition is a small fraction of the pass rather than a
   hot-loop cost; VEX-encoding it is a tracked cleanup, and this baseline is
   what a fix is measured against.

**Scalar throughput is flat in n** (~684 M interactions/s from 1k to 16k): the
in-range fraction is constant in n, so the pass stays compute-bound and
cache-resident with clean O(n²) scaling and no cliff. **AVX2 throughput rises
~10%** across the same range (1160 → 1281 M), climbing toward a ~1.28 G/s
asymptote as the fixed per-particle overhead — the once-per-i integrate tail
and outer-loop setup — amortizes over more neighbours.

**What this says about the M1 8k target.** At n = 8192 the AVX2 pass is
~52.8 ms on this frame, i.e. ~19 frames/s single-threaded (the build copy is
sub-millisecond and does not move this) — and because this is the sparsest
frame, a settled, clustered swarm costs somewhat more. 8,192 particles at 60 fps
therefore needs at least ~3× more throughput than one Zen 3 core delivers here —
reachable by the M3 worker-pool fan-out across cores (the pass is already
split-invariant, proven by `PassSplitInvariance`), the M2 candidate-set
reduction, or both. We are not claiming 8k@60 on one thread; we are recording
where one thread stands so the threading and layout work has a number to beat.

## The M2 grid (uniform spatial grid)

The grid replaces the brute-force O(n²) sweep with the O(n·k) neighbourhood
pass (masterplan decision 3): a serial stable counting sort reorders the
population cell-sorted, then the force pass reads only the 3×3 cell
neighbourhood of each particle. `g` = the largest power of two with `1/g ≥ rmax`
(clamped `[4, 512]`), so a small `rmax` gives a large `g` and sparse cells —
the regime the grid wins in.

| CPU           | n       | rmax  | g   | build ms | pass ms | frame ms | fps   | brute proj |
| ------------- | ------- | ----- | --- | -------- | ------- | -------- | ----- | ---------- |
| Ryzen 9 5950X | 50,000  | 1/256 | 256 | 0.295    | 5.923   | 6.219    | 160.8 | 1,977 ms   |
| Ryzen 9 5950X | 50,000  | 1/512 | 512 | 0.490    | 5.986   | 6.476    | 154.4 | 1,977 ms   |
| Ryzen 9 5950X | 500,000 | 1/256 | 256 | 3.142    | 84.075  | 87.216   | 11.5  | 197,664 ms |
| Ryzen 9 5950X | 500,000 | 1/512 | 512 | 3.162    | 62.351  | 65.513   | 15.3  | 197,664 ms |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11, **single-threaded**.
- **Feature path**: AVX2 + FMA, `FLAG_GRID`. **Seed / preset**: `0x5EED`,
  6 species, varied attraction matrix; the initial (uniform-random) frame — the
  build cost is `g`-dominated and stable, the pass cost is lowest on this
  sparsest frame (a settled, clustered swarm costs somewhat more).
- **frame** = build + pass (each timed min-of-rounds over frozen input, so the
  work is identical every round). **brute proj** = n² candidate pairs ÷ the
  measured AVX2 interaction throughput (~1.28 G/s from the baseline table);
  the O(n²) brute frame is not run at these counts (it would take seconds to
  minutes per frame).
- **Commit**: the M2 grid kernel (grid build #24 + neighbourhood force #30) ·
  **Date**: 2026-07-17.

### Reading the M2 numbers

**50,000 particles hold 60 fps on one core** — 154–161 fps, ~2.5× headroom
under the 16.67 ms budget — where the brute-force frame would be ~1,977 ms
(0.5 fps). The grid is **~300× faster than brute at 50k** and turns an
un-runnable count into a comfortable one.

**500,000 particles do not hold 60 fps on one core** — 65 ms/frame at the best
config (g = 512), ~15 fps — but the grid is still **~3,000× faster than the
~198 s brute frame**. The gap to 60 fps is ~4×, which is exactly what the M3
worker-pool fan-out across cores is for: the neighbourhood pass is already
**split-invariant** (`GridPassSplitInvariance`, `pass(0,n) == pass(0,k);pass(k,n)`
bit-for-bit), so it parallelises without a determinism change. The counting-sort
**build is cheap** (0.3 ms at 50k, ~3 ms at 500k) and never the bottleneck; the
pass dominates, and a larger `g` (sparser cells, smaller `k`) is the lever —
g = 512 beats g = 256 at 500k (62 vs 84 ms) for that reason.

So M2 delivers the algorithmic win (the grid makes 500k _simulable_ at all, and
50k interactive) on one core; **500k @ 60 fps is an M3 threading target**, with
the number above as the baseline to beat.
