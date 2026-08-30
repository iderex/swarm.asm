# Benchmarks

Measured numbers for the swarm.asm kernel, with the methodology that produced
them. A performance claim anywhere in the repo points here; an unmeasured
performance claim is not allowed.

Numbers are **per-machine and never compared across hardware** - every baseline
row carries the CPU, the feature path, the particle count, the seed, the
commit, and the date. Re-run on your own machine before drawing a conclusion.

## What is measured

One **force + integrate pass** (`swarm_pass` over the whole population): the
O(n²) brute-force interaction loop that dominates the frame. We time the pass
in isolation - `swarm_build` once to freeze the IN bank, then repeat the pass
over that frozen bank - so the measured work is identical on every iteration
and carries none of the bank-swap or copy cost a full `swarm_step` would fold
in. Two code paths are compared at each particle count: the scalar reference
(`force_path = 3`) and the AVX2 gather path (`force_path = 1`).

Also measured, in their own sections below: the **live work window** of
`swarm.exe`, from the shipped exe's `-capture` mode, at the M1 acceptance count
and at the two committed 1M scenes `presets/headline.txt` and
`presets/dense.txt`. That is a different measurement from the pass benches
above - it is the whole of step plus plot plus blit, taken from the product
rather than from a harness.

Not yet measured here (tracked on #5, milestone M4): the full end-to-end
benchmark mode with its own results file and per-phase breakdown, and
regression gating against a stored baseline.

Partly measured, and worth naming at the top so the absence is not read as a
comparison that exists: the competitor set #153 asks for is three engines, and
all three now run here - the managed baseline and the two foreign cores, each
with its own section at the end of this document. **No comparison table is
published**, because no run of the three has been taken on a host free of a
second workload; the section that would carry the table measures the load and
says so instead.

## How to run

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run -c Release --project tests\Swarm.Bench\Swarm.Bench.csproj
```

The harness assembles the kernel first (via `build.ps1`, so the benchmarked
DLL is the shipping DLL), then drives it through P/Invoke.

### Why a hand-rolled harness and not BenchmarkDotNet

BenchmarkDotNet compiles and launches a **fresh host process** per benchmark.
On the dev machine that fresh, unsigned PE is exactly what Device Guard / Smart
App Control blocks (`0x800711C7`) - the same reason the test suite runs
in-process under the trusted `dotnet` host (MTP, not VSTest). So the benchmark
runs **in-process** too: a dependency-free min-of-rounds `Stopwatch` loop, no
NuGet package, no lock file, no spawned process. It reports the **minimum**
per-pass time over 9 rounds (after a 3-pass warm-up), each round sized to run
for **at least ~120 ms** - `max(⌊120 ms / one pass⌋, 1)` passes, so at the
larger counts, where a single pass already exceeds 120 ms, a round is one pass.
The minimum, not the mean: a force pass is a fixed amount of arithmetic, so the
fastest observed round is the one least perturbed by scheduling and clock
transitions - the honest lower bound on the kernel's cost. The harness pins
neither process priority nor thread affinity, so reruns vary by a few percent
(more at the small counts); the recorded table is one clean run - reproduce on
your own machine before drawing a conclusion.

The Smart App Control config-flip quirk applies here too: if a run fails to
load the DLL with `0x800711C7`, re-run with `-c Debug`.

## Baseline

| CPU           | Path   | n     | ms / pass | speedup | interactions/s |
| ------------- | ------ | ----- | --------- | ------- | -------------- |
| Ryzen 9 5950X | scalar | 1024  | 1.556     | -       | 673.9 M        |
| Ryzen 9 5950X | AVX2   | 1024  | 0.904     | 1.72×   | 1160.4 M       |
| Ryzen 9 5950X | scalar | 2048  | 6.163     | -       | 680.5 M        |
| Ryzen 9 5950X | AVX2   | 2048  | 3.435     | 1.79×   | 1221.0 M       |
| Ryzen 9 5950X | scalar | 4096  | 24.583    | -       | 682.5 M        |
| Ryzen 9 5950X | AVX2   | 4096  | 13.375    | 1.84×   | 1254.4 M       |
| Ryzen 9 5950X | scalar | 8192  | 98.077    | -       | 684.2 M        |
| Ryzen 9 5950X | AVX2   | 8192  | 52.765    | 1.86×   | 1271.8 M       |
| Ryzen 9 5950X | scalar | 16384 | 391.980   | -       | 684.8 M        |
| Ryzen 9 5950X | AVX2   | 16384 | 209.493   | 1.87×   | 1281.4 M       |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11, single-threaded.
- **Feature path**: AVX2 + FMA (this CPU reports no AVX-512).
- **Seed / preset**: `0x5EED`, 6 species, `rmax = 0.05`, varied attraction
  matrix. Positions are the **initial frame** (uniform-random, zero velocity) -
  the sparsest configuration, so the scalar numbers here are a floor, not a
  steady-state average (see "Reading the numbers"). interactions/s counts the
  n² candidate pairs a pass evaluates, not the force evaluations it performs.
- **Commit**: `e134c9a` (the kernel under test; the bench harness itself lands
  in a later commit - the kernel binary is identical) · **Date**: 2026-07-17.

## Reading the numbers

**The AVX2 path is ~1.7–1.9× the scalar path - not ~8×** (1.72× at n = 1024,
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
   faster - the ratio is not "same work, 8× faster", it is what remains after
   the vector path pays for the pairs the scalar path skipped. A denser preset
   (larger `rmax`) narrows the scalar skip and shifts the ratio.
2. **The vector force loop is divider-bound.** Its cost is set by the `vsqrtps`
   - `vdivps` in the force formula (the masterplan's own analysis, decision 3),
     not by an 8-wide ALU ideal - and not by the neighbour loads, which are
     already contiguous `vmovaps` in the brute-force layout (there is no
     `vgatherdps` in the kernel). Eight-wide divide/sqrt throughput on Zen 3, not
     load width, is the ceiling.

Two consequences on the roadmap:

1. **The large-N win is a smaller candidate set, not a load-layout change.**
   Once the M2 spatial grid sorts particles into cells, a pass evaluates only
   the O(n·k) neighbours actually within `rmax` instead of all n² candidates -
   that is what collapses the work. It also removes the scalar path's cheap
   skip advantage (a cell's neighbours are mostly in range), so the AVX2 ratio
   should climb well past 2×.
2. **The shared integrate/store tail is now VEX-encoded** inside the AVX2 pass
   (issue #33). It runs once per particle, after the VEX inner loop. In the
   **brute** pass the n-iteration force loop dominates, so VEX-encoding the tail
   is within run-to-run noise - the baseline table above is unchanged. In the
   **sparse grid** pass it is the reverse: cells hold ~1 neighbour, so the
   once-per-particle tail dominates the pass, and running it in legacy SSE with
   dirty ymm upper halves paid a per-instruction merge stall on every tail op.
   VEX-encoding the tail (bit-identical arithmetic, proven by the per-path
   goldens and a before/after bit-identity A/B) cut the grid pass ~2.6–2.9× -
   see the M2 table below.

**Scalar throughput is flat in n** (~684 M interactions/s from 1k to 16k): the
in-range fraction is constant in n, so the pass stays compute-bound and
cache-resident with clean O(n²) scaling and no cliff. **AVX2 throughput rises
~10%** across the same range (1160 → 1281 M), climbing toward a ~1.28 G/s
asymptote as the fixed per-particle overhead - the once-per-i integrate tail
and outer-loop setup - amortizes over more neighbours.

**What this says about the M1 8k target.** At n = 8192 the AVX2 pass is
~52.8 ms on this frame, i.e. ~19 frames/s single-threaded (the build copy is
sub-millisecond and does not move this) - and because this is the sparsest
frame, a settled, clustered swarm costs somewhat more. 8,192 particles at 60 fps
therefore needs at least ~3× more throughput than one Zen 3 core delivers here -
reachable by the M3 worker-pool fan-out across cores (the pass is already
split-invariant, proven by `PassSplitInvariance`), the M2 candidate-set
reduction, or both. We are not claiming 8k@60 on one thread; we are recording
where one thread stands so the threading and layout work has a number to beat.

## The M2 grid (uniform spatial grid)

The grid replaces the brute-force O(n²) sweep with the O(n·k) neighbourhood
pass (masterplan decision 3): a serial stable counting sort reorders the
population cell-sorted, then the force pass reads only the 3×3 cell
neighbourhood of each particle. `g` = the largest power of two with `1/g ≥ rmax`
(clamped `[4, 512]`), so a small `rmax` gives a large `g` and sparse cells -
the regime the grid wins in. The ceiling changes the answer only below
`rmax = 1/1024`, where the rule would otherwise keep doubling; at or above that
`rmax` the rule stops first and `g` is what `rmax` alone says (#148).

| CPU           | n       | rmax  | g   | build ms | pass ms | frame ms | fps   | brute proj |
| ------------- | ------- | ----- | --- | -------- | ------- | -------- | ----- | ---------- |
| Ryzen 9 5950X | 50,000  | 1/256 | 256 | 0.286    | 2.203   | 2.489    | 401.8 | 1,977 ms   |
| Ryzen 9 5950X | 50,000  | 1/512 | 512 | 0.423    | 2.062   | 2.484    | 402.5 | 1,977 ms   |
| Ryzen 9 5950X | 500,000 | 1/256 | 256 | 3.127    | 44.176  | 47.303   | 21.1  | 197,664 ms |
| Ryzen 9 5950X | 500,000 | 1/512 | 512 | 3.160    | 23.822  | 26.982   | 37.1  | 197,664 ms |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11, **single-threaded**.
- **Feature path**: AVX2 + FMA, `FLAG_GRID`. **Seed / preset**: `0x5EED`,
  6 species, varied attraction matrix; the initial (uniform-random) frame - the
  build cost is `g`-dominated and stable, the pass cost is lowest on this
  sparsest frame (a settled, clustered swarm costs somewhat more).
- **frame** = build + pass (each timed min-of-rounds over frozen input, so the
  work is identical every round). **brute proj** = n² candidate pairs ÷ the
  measured AVX2 interaction throughput (~1.28 G/s from the baseline table);
  the O(n²) brute frame is not run at these counts (it would take seconds to
  minutes per frame).
- **Commit**: the M2 grid kernel (grid build #24 + neighbourhood force #30) with
  the VEX-encoded integrate tail (#33) · **Date**: 2026-07-17. The pre-#33 grid
  pass on this machine was 5.923 / 5.986 / 84.075 / 62.351 ms for the four rows;
  brute is unaffected (the tail is negligible there).

### Grid pass after resolving the run set once per cell (#87)

The grid pass used to call `neighbour_runs` once per particle. IN is
cell-sorted and the pass walks `i` ascending, so consecutive particles share a
cell and the answer is the same for all of them; a three-dword cache in the
pass frame skips the call on a repeat.

Three alternating runs per build, all listed rather than a chosen one, because
the run-to-run spread here is the same size as part of the effect. Each figure
is already a min over the bench's 9 rounds. Pass ms only: the build column is
not touched by this change and moved by up to 0.5 ms between runs of the same
build, which is the scale of the noise on this machine.

| n       | rmax  | g   | pass ms, per-particle    | pass ms, per-cell        |
| ------- | ----- | --- | ------------------------ | ------------------------ |
| 50,000  | 1/256 | 256 | 2.278 / 2.238            | 2.166 / 2.137            |
| 50,000  | 1/512 | 512 | 2.111 / 2.142            | 2.188 / 2.089            |
| 500,000 | 1/256 | 256 | 45.560 / 46.982 / 46.998 | 48.065 / 41.422 / 44.198 |
| 500,000 | 1/512 | 512 | 28.009 / 25.864 / 26.804 | 23.947 / 23.429 / 23.458 |

- **500,000 at g = 512 is the clean result**: every cached run is faster than
  every uncached one, medians 23.46 against 26.80, about **12% off the pass**.
  Mean particles per cell is `n/g² ≈ 1.9`, so roughly half the calls are
  skipped.
- **500,000 at g = 256 is a smaller and noisier win**: medians 44.20 against
  46.98, about **6%**, but the slowest cached run (48.065) is above every
  uncached one, so two of three runs separate and one does not. `k ≈ 7.6` here
  and about 87% of calls are skipped, yet the relative win is smaller: the
  denser cells make the force groups dominate, so the same absolute saving of
  roughly 3 ms is a smaller share of a longer pass.
- **50,000 is inside the noise in both directions** and no win is claimed
  there. `k` is below 1, so most particles are alone in their cell and the two
  added compares are the whole of it.
- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11,
  **single-threaded**, AVX2 + FMA, `FLAG_GRID`, seed `0x5EED`, 6 species, the
  initial uniform-random frame. Same bench binary and DLL slot throughout;
  only the kernel DLL was swapped, alternating between the two builds.
- **Commit**: `0bccc12` · **Date**: 2026-08-06. The change these numbers
  measure lands with this section, so the per-cell column is that commit's
  `src/kernel/step.inc` and the per-particle column is its parent `129600b`.
  Output is bit-identical across the change: whole-arena hashes match for 216
  configurations, including split passes, recorded on the pull request that
  closed #87.

### Grid build after the pad-only copy (#77)

`build_core` used to copy the whole OUT bank into IN in grid mode as well, and
`grid_sort` then overwrote `IN[0..n)` out of OUT for all six components. Only
the pad tail `[n, padded_n)` genuinely had to carry over. The table above was
taken before that change; the build column moves and nothing else does.

| n       | rmax  | g   | build ms before | build ms after | delta |
| ------- | ----- | --- | --------------- | -------------- | ----- |
| 50,000  | 1/256 | 256 | 0.302 / 0.300   | 0.209 / 0.208  | -31%  |
| 50,000  | 1/512 | 512 | 0.497 / 0.514   | 0.375 / 0.376  | -26%  |
| 500,000 | 1/256 | 256 | 3.214 / 3.159   | 2.604 / 2.572  | -19%  |
| 500,000 | 1/512 | 512 | 3.248 / 3.289   | 2.781 / 2.790  | -15%  |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11,
  **single-threaded**, AVX2 + FMA, `FLAG_GRID`, seed `0x5EED`, 6 species, the
  same uniform-random initial frame the table above uses.
- Two independent runs of `Swarm.Bench` per build, both listed, each already a
  min over the bench's 9 rounds. The two builds differ only in
  `src/kernel/step.inc`; the same bench binary and the same kernel DLL slot
  were used, swapping only the DLL.
- **pass ms** did not move outside run-to-run spread: 44.05 / 44.35 after
  against 44.33 / 44.30 before at `n = 500,000, rmax = 1/256`, and 24.01 /
  23.77 against 23.99 / 23.89 at `rmax = 1/512`. The change touches the build
  and nothing the pass reads.
- **Commit**: `a647ac7` · **Date**: 2026-08-06. The change these numbers
  measure lands with this section, so the after column is that commit's
  `src/kernel/step.inc` and the before column is its parent `96189c2`.
  Bit-exactness is not inferred from these numbers: the whole arena hashes
  identically across the change for 84 configurations, which is recorded on the
  pull request that closed #77.

### Reading the M2 numbers

**50,000 particles hold 60 fps on one core with room to spare** - ~402 fps,
~6.7× headroom under the 16.67 ms budget - where the brute-force frame would be
~1,977 ms (0.5 fps). The grid is **~790× faster than brute at 50k** and turns an
un-runnable count into a trivial one. (Pre-#33 this was ~155 fps; VEX-encoding
the integrate tail nearly tripled the sparse-grid pass - see the note above.)

**500,000 particles are close to 60 fps on one core** - 27 ms/frame at the best
config (g = 512), ~37 fps - and the grid is **~7,300× faster than the ~198 s
brute frame**. The gap to 60 fps is now ~1.6× (pre-#33 it was ~4× at 65 ms/frame),
which the M3 worker-pool fan-out across cores closes: the neighbourhood pass is
already **split-invariant** (`GridPassSplitInvariance`,
`pass(0,n) == pass(0,k);pass(k,n)` bit-for-bit), so it parallelises without a
determinism change. The counting-sort **build is cheap** (0.3 ms at 50k, ~3 ms
at 500k in this table, and lower again after #77 above) and never the
bottleneck; the pass dominates, and a larger `g` (sparser
cells, smaller `k`) is the lever - g = 512 beats g = 256 at 500k (24 vs 44 ms)
for that reason.

So M2 delivers the algorithmic win (the grid makes 500k _simulable_ at all, and
50k trivially interactive) on one core; **500k @ 60 fps is an M3 threading
target**, now within ~1.6× of one core, with the number above as the baseline to
beat.

## The M3 worker pool (parallel pass; #68)

The M3 worker pool fans the force+integrate pass across a persistent pool of
one-per-physical-core workers (`CreateThread` once, main participates as
worker 0, auto-reset events for wake/join). The build (counting sort) stays
serial in v1. The pass is a pure, split-invariant map, so the threaded result
is **bit-identical to the serial pass for every thread count**
(`PassParallelMatchesSerial` asserts exact equality across `T = 1, 2, 4, max`);
this is pure throughput, no accuracy trade. Work is a static even partition of
`[0, n)` with every boundary rounded to a multiple of 16 (16 f32 = one 64-byte
line, so no OUT array is false-shared across workers).

| CPU           | n       | g   | T   | pass ms | frame ms | fps  | pass speedup |
| ------------- | ------- | --- | --- | ------- | -------- | ---- | ------------ |
| Ryzen 9 5950X | 500,000 | 512 | 1   | 64.320  | 74.827   | 13.4 | 0.98×        |
| Ryzen 9 5950X | 500,000 | 512 | 2   | 32.561  | 43.069   | 23.2 | 1.93×        |
| Ryzen 9 5950X | 500,000 | 512 | 4   | 15.982  | 26.490   | 37.8 | 3.93×        |
| Ryzen 9 5950X | 500,000 | 512 | 8   | 8.146   | 18.654   | 53.6 | 7.71×        |
| Ryzen 9 5950X | 500,000 | 512 | 16  | 4.979   | 15.487   | 64.6 | 12.61×       |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11. **T** = worker
  count; `T = 16` is the auto-detected physical-core count (SMT is not used - a
  divider-bound AVX2 loop gains nothing from a second sibling on the shared
  divide/sqrt port). **Feature path**: AVX2 + FMA, `FLAG_GRID`. **Seed /
  preset**: `0x5EED`, 6 species, `rmax = 1/512`, varied attraction matrix.
- **pass ms** = the threaded pass (`swarm_pass_mt`), min-of-rounds over the
  frozen sorted IN bank (identical work every round), so the scaling is clean.
  **frame** = serial build + threaded pass. **pass speedup** = serial pass ÷
  threaded pass; the `T = 1` row (0.98×) shows the pool wake/join overhead is
  negligible against a 60 ms pass.
- **serial build** here is **10.5 ms** - the counting sort timed on the
  **initial uniform-random** frame, whose ~1.9 particles across `g² = 262,144`
  cells scatter the stable backward pass across memory. That is the worst-case
  build; the settled post-warmup distribution the M2 table times is **~3.2 ms**
  (clustered cells, better locality). The frame column uses the worst-case
  build, so a steady-state frame at `T = 16` is nearer **~8 ms (~120 fps)**.
- **Commit**: the M3 worker pool (`src/platform/pool.inc`, #68) · **Date**:
  2026-07-18.

### Reading the M3 numbers - 500k @ 60 fps reached

**500,000 particles clear 60 fps on 16 cores.** At `T = 16` the threaded pass
is **4.98 ms** (a **12.6×** speedup over the 62.8 ms serial pass), and even with
the worst-case 10.5 ms uniform-frame build the frame is **15.5 ms - 64.6 fps**,
inside the 16.67 ms budget. Against the settled ~3.2 ms build the steady-state
frame is ~8 ms (~120 fps). Either way the ~4× gap the M2 baseline recorded is
closed by threading alone.

**Scaling is near-linear to 8 cores, then tapers.** 1.93× / 3.93× / 7.71× at
`T = 2 / 4 / 8` is essentially ideal - the 16-aligned partition keeps the seven
OUT arrays off shared cache lines, so there is no false-sharing collapse. The
9th–16th cores add 7.71× → 12.6× (a run-to-run 12–15× at the top of the sweep):
the second CCD reaches the working set across the inter-CCD fabric and the pass
shifts partly bandwidth-bound past 8 cores, exactly the risk decision 6 flagged.
It is a scaling taper, not a correctness effect - the state stays bit-identical.

**Determinism is independent of T.** The static split is bit-identical for any
`T` because each particle's output is a pure function of the frozen IN bank plus
`cell_start`; the per-thread MXCSR pin (each worker crosses the same seam the
exports do, pinning `0x9FC0` FTZ/DAZ before any FP op) is what makes that hold
across threads. `PassParallelMatchesSerial` gates it at `T = 1, 2, 4, 16` on
both the AVX2 and scalar paths, exact equality.

**1M is measured, and it is the section below.** This paragraph used to say
there was no 1M row and that threads alone might not reach 60 fps there. There
is one now, at "The 1M baseline", and on the headline scene threads alone do
reach the budget at the kernel seam. What that does and does not settle is
stated there rather than here.

## The 1M baseline (serial and threaded; #176)

Every 1M figure above this line was a projection from the 500k rows. This is
the measurement they stood in for: the grid frame at n = 1,048,576, serial and
then across the pool, on the two scenes decision 12 names.

**Three whole runs of the harness, not one.** The run-to-run spread at this
count is wider than several of the differences a reader would otherwise draw
from a single run, so all three are printed and every reading below is taken on
the worst of the three rather than the best.

```
dotnet run --project tests/Swarm.Bench/Swarm.Bench.csproj -c Release
```

### Serial, one core

| run | scene    | rmax     |   g | build ms | pass ms | frame ms |  fps |
| --- | -------- | -------- | --: | -------: | ------: | -------: | ---: |
| 1   | headline | 0.001953 | 512 |    6.631 |  65.069 |   71.700 | 13.9 |
| 2   | headline | 0.001953 | 512 |    7.305 |  73.509 |   80.814 | 12.4 |
| 3   | headline | 0.001953 | 512 |    7.232 |  73.343 |   80.575 | 12.4 |
| 1   | dense    | 0.003906 | 256 |    6.597 | 156.778 |  163.375 |  6.1 |
| 2   | dense    | 0.003906 | 256 |    8.504 | 167.743 |  176.247 |  5.7 |
| 3   | dense    | 0.003906 | 256 |    8.827 | 172.576 |  181.402 |  5.5 |

### Threaded pass, serial build added back

`T = 16*` is the pool's auto-detected physical-core count.

| run | scene    |    T | pass ms | frame ms |  fps | pass × |
| --- | -------- | ---: | ------: | -------: | ---: | ------ |
| 1   | headline |    8 |  13.531 |   20.162 | 49.6 | 4.81×  |
| 2   | headline |    8 |  13.582 |   20.887 | 47.9 | 5.41×  |
| 3   | headline |    8 |  14.009 |   21.240 | 47.1 | 5.24×  |
| 1   | headline | 16\* |   7.184 |   13.815 | 72.4 | 9.06×  |
| 2   | headline | 16\* |   8.105 |   15.410 | 64.9 | 9.07×  |
| 3   | headline | 16\* |   8.303 |   15.535 | 64.4 | 8.83×  |
| 1   | dense    | 16\* |  18.724 |   25.321 | 39.5 | 8.37×  |
| 2   | dense    | 16\* |  19.869 |   28.373 | 35.2 | 8.44×  |
| 3   | dense    | 16\* |  21.207 |   30.033 | 33.3 | 8.14×  |

The full `T` sweep (1, 2, 4, 8, auto) is in the harness output; the rows above
are the two that carry the reading.

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  build 10.0.26200. **Feature path**: `swarm_cpu_paths` reports `0x1`, so AVX2
  and no AVX-512; `force_path = 1`.
- **Scenes**: `n = 1,048,576` (the ABI's maximum `n`), 6 species, seed `0x5EED`,
  `beta = 0.3`, `dt = 0.02`, `friction = 0.71`, `force_scale = 10`, `FLAG_GRID`,
  and the harness's deterministic `sin`-filled matrix. Headline is
  `rmax = 1/512` so `g` is 512; dense is `rmax = 1/256` so `g` is 256 and
  each cell holds roughly four times the particles.
- **build** is the near-sorted counting sort, i.e. every frame after the first.
  The first frame's build at this count is in the #177 table below and is three
  to five times this one.
- **Commit**: `4924c3b` · **Date**: 2026-08-06.
- **The host was not quiesced**, and the three runs above are what that costs:
  9.1 ms between the best and worst serial headline frame, on a figure of ~80.

### Reading the 1M numbers

**Does the frame budget close with threads alone at 1M?** On the headline scene,
at the kernel seam, yes. The worst of the three runs puts the threaded frame at
**15.535 ms** against the 16.67 ms budget, and the best at 13.815 ms. On the
dense scene, no, and not close: the worst is **30.033 ms**, about 1.8× over.

**What that answer is not.** These are `swarm_build` plus `swarm_pass` at the
P/Invoke seam. A frame the user waits for also plots and blits 1M particles, and
none of that is in the numbers above. The M1 acceptance figure at 8,192 is a
work window measured inside the shipped exe and includes both; there is no
equivalent capture at 1M, so nothing here is a frame-rate claim about
`swarm.exe`. The headline claim is #125's and still needs one.

**And these are minima, not percentiles.** Every figure here is the best of nine
rounds, which is the right primitive for comparing kernels and the wrong one for
a 60 fps claim. A budget is met at p99 or it is not met. 15.535 ms of best-case
work against a 16.67 ms budget leaves 1.1 ms for everything the minimum
excluded, which is not a margin anyone should spend in advance.

**Scaling stops paying at 8 cores, exactly as 500k said it would.** The pass
scales 5.24× at `T = 8` and 8.83× at `T = 16` on the worst run, so the second CCD
returns about a third of what the first eight cores did. That is the taper the
500k section already recorded and attributed to the working set crossing the
inter-CCD fabric, and at twice the particles it is not better.

**The build is now a visible share of the threaded frame.** Serial build against
the `T = 16` frame is 7.232 of 15.535 ms on the worst headline run, about 47%.
Serial at 500k it was under a third. Risk 2's contingency, the parallel scatter,
is already triggered on the #177 measurement below; this is what triggering it
is worth, and it is the single largest remaining item in the threaded frame.

The contingency has since been built, and the rows in this section predate it:
their frame column carries a serial build. "The 1M threaded frame with the
parallel build in it" further down is the same frame measured with it, and it is
the row to read for the current binary rather than this one.

## The serial grid build at 1M (risk 2's probe; #177)

Masterplan open-risk 2 estimates the counting sort's histogram chain at **8-12
cycles/particle on near-sorted input** and calls anything **materially above
~4.5 ms** at 1M an erosion of the frame margin. That estimate is what decides
whether the build stays serial, and it had never been checked at 1M. This is
the check.

Build only, `swarm_build` over a frozen arena, min-of-rounds like every other
figure here. No kernel change.

| n         | rmax  | g   | sorted ms | cyc/particle | unsorted ms | cyc/particle |
| --------- | ----- | --- | --------- | ------------ | ----------- | ------------ |
| 500,000   | 1/256 | 256 | 2.643     | 25.9         | 5.404       | 53.0         |
| 500,000   | 1/512 | 512 | 2.544     | 24.9         | 7.580       | 74.3         |
| 1,048,576 | 1/256 | 256 | 6.784     | 31.7         | 25.901      | 121.0        |
| 1,048,576 | 1/512 | 512 | 7.049     | 32.9         | 26.521      | 123.9        |

- **Two input states, because the difference is larger than the budget.** The
  build scatters OUT into cell order, so how far OUT already is from that order
  sets the write locality. **sorted** is OUT already cell-ordered: a pass has
  run over sorted IN and written OUT at the same indices, which is every frame
  after the first and is the near-sorted input risk 2 states its estimate for.
  **unsorted** is the id-ordered initial frame, which is the first frame of a
  run. Reporting either one alone as "the build cost" would settle the risk by
  picking a column.
- **500,000 is the control, not a result.** Its sorted figures have to
  reproduce the build column of the M2 grid table above, and its unsorted
  figures the worst-case build the M3 section describes. They do: 2.643 / 2.544
  against that run's 2.485 / 2.632, and 5.404 / 7.580 against that run's
  6.546 ms. An instrument that failed this control would be measuring something
  other than what the other rows measure.
- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  build 10.0.26200, **single-threaded** - the build is serial by design.
  **Feature path**: AVX2 + FMA (no AVX-512), `force_path = 1`, `FLAG_GRID`.
  **Seed / preset**: `0x5EED`, 6 species, the bench's varied attraction matrix.
- **Cycles** are derived at `RefGhz = 4.9` as in the `#59` section below; the
  ms column is the clock-free primitive and the verdict below does not rest on
  the derivation.
- **Commit**: `668d9aa` · **Date**: 2026-08-06. Run on a host that was not
  quiesced.

### Reading it - risk 2 misses its budget, in both currencies

**Near-sorted, 1M, `g = 512`: 7.049 ms and ~32.9 cycles/particle.** The
estimate is 8-12 cycles/particle and the line is ~4.5 ms. The measurement is
**~2.7x the top of the cycle estimate** and **~1.6x the millisecond line**.

**The verdict does not depend on the assumed clock, and it cannot be rescued by
choosing a different one.** The two halves of the estimate are not consistent
with each other at this part's clock: 12 cycles/particle at 1,048,576 particles
is 2.6 ms at 4.9 GHz, not the ~4 ms decision 3 states, so the estimate was
written against a clock of roughly 2.5 GHz. It misses either way. The 7.049 ms
is measured and carries no clock at all, so it exceeds the 4.5 ms line whatever
was assumed; and for 7.049 ms to be 12 cycles/particle the part would have to
be running at 1.79 GHz, which it is not under any load.

**The growth from 500k to 1M is worse than linear in n**, and that is the part
that matters for the headline. Near-sorted at `g = 512`, 500k costs 2.544 ms
and 1M costs 7.049 ms: 2.1x the particles for **2.8x the build**. `g` is 512
for both, so the O(g²) zero-and-prefix half is identical between
them and cannot be the cause; what grows is the scatter, at ~2 particles per
cell against ~4. Extrapolating this row to a count above 1M is not supported by
two points, and none is offered.

**So risk 2's contingency is triggered**, with 7.049 ms as the number that
triggered it. The masterplan records that against the risk. Note that risk 2's
contingency is the **per-thread per-bucket-cursor parallel scatter**, which is
also decision 6's; the two-pass radix is risk **3**'s fallback and is not what
this measurement authorises.

**What this does not say.** It does not say the 1M frame budget is lost - the
pass is the larger term and is threaded, and no 1M pass figure is recorded here
yet. It says the build's own budget line is missed on the input state the risk
is written about, which is the question this probe was asked.

## Where the 1M build's time sits (#243)

The section above records the serial build at 1M and triggers risk 2's
contingency. What it does not record is how that time divides between the
build's two kinds of work, and the contingency's whole trade turns on the
division. The per-thread per-bucket-cursor scatter divides the work that is
proportional to `n` by the worker count, and multiplies the work that is not by
it, because a cursor per thread per bucket means one histogram of every bucket
per worker.

`grid_sort` is four phases over one `(g*g + 1)`-dword block: zero, histogram,
inclusive prefix, backward scatter (`src/kernel/grid.inc`). The zero and the
prefix walk every bucket and never read `n`. The histogram and the scatter are
proportional to it.

**How the split is taken, without a clock inside the kernel.** `src/kernel/`
makes no API calls, so a phase timer cannot live there. It does not have to.
`g` is a function of `rmax` alone, the largest power of two with
`1/g >= rmax` clamped to `[4, 512]` (`src/kernel/layout.inc`), so holding `rmax`
fixed and moving `n` holds the O(g²) work exactly constant and moves only the
O(n) work. A least-squares line through the four smallest populations,
extrapolated back to `n = 0`, is the O(g²) half on its own.

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run -c Release --project tests\Swarm.Bench\Swarm.Bench.csproj -- --buildsplit
```

### The ladder

Both grid dimensions the 1M scenes use, both input states, min-of-rounds like
every other figure here. This is the third of the three runs, so its rows can be
matched against that run in the table below it.

| n         | g = 512 sorted | g = 512 unsorted | g = 256 sorted | g = 256 unsorted |
| --------- | -------------- | ---------------- | -------------- | ---------------- |
| 1,024     | 0.192          | 0.192            | 0.065          | 0.043            |
| 2,048     | 0.226          | 0.245            | 0.043          | 0.045            |
| 4,096     | 0.319          | 0.314            | 0.054          | 0.090            |
| 8,192     | 0.304          | 0.266            | 0.084          | 0.096            |
| 16,384    | 0.293          | 0.369            | 0.088          | 0.153            |
| 65,536    | 0.448          | 0.920            | 0.317          | 0.675            |
| 262,144   | 1.334          | 2.719            | 1.456          | 2.742            |
| 500,000   | 2.619          | 6.708            | 2.649          | 5.525            |
| 1,048,576 | 6.959          | 39.647           | 7.241          | 31.547           |

All figures in milliseconds.

### The three runs, near-sorted input

| run | g   | O(g²) half | build at 1M | O(n) half | O(g²) share |
| --- | --- | ---------- | ----------- | --------- | ----------- |
| 1   | 512 | 0.191      | 6.697       | 6.506     | 2.9%        |
| 2   | 512 | 0.187      | 7.755       | 7.568     | 2.4%        |
| 3   | 512 | 0.202      | 6.959       | 6.757     | 2.9%        |
| 1   | 256 | 0.041      | 8.818       | 8.777     | 0.5%        |
| 2   | 256 | 0.045      | 10.658      | 10.613    | 0.4%        |
| 3   | 256 | 0.046      | 7.241       | 7.194     | 0.6%        |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  build 10.0.26200, **single-threaded** - the build is serial by design.
  **Feature path**: AVX2 + FMA (no AVX-512), `force_path = 1`, `FLAG_GRID`.
  **Seed / preset**: `0x5EED`, 6 species, the bench's varied attraction matrix.
- **Kernel commit**: `7663810` · **Date**: 2026-08-09. The change these numbers
  come with adds a bench section and touches no kernel source, so the measured
  binary is that commit's. Run on a host that was **not** quiesced, and the two
  input columns show it differently: see the disclosure below.

### Reading it - the contingency divides 97% and multiplies 3%

**The O(g²) half is 0.187 to 0.202 ms at `g = 512`, against a build of 6.7 to
7.8 ms.** So 97% of the serial build at 1M is the histogram and the scatter, and
under 3% is the zero and the prefix. At `g = 256` the same half is 0.041 to
0.046 ms and the share is under 1%.

**The intercept is the figure this section stands on, and it is the stable one.**
Across the three runs it moves by 0.015 ms at `g = 512` while the build at 1M in
the same runs moves by 1.06 ms. That is the point of taking the split at the
bottom of the ladder rather than by subtracting neighbouring rows near the top,
where the spread across rows doing identical build work is larger than the
quantity being isolated.

**It scales with the cell count, which is the instrument's own control.** 0.19 ms
at 262,144 cells against 0.044 ms at 65,536 is 4.3x for a 4x difference in
buckets, on a quantity that is a memset and a prefix over exactly that many
dwords. A half that did not scale that way would not be the half it is named
for.

**What that says about the contingency, as arithmetic and not as a prediction.**
Under the platform-owned scratch shape, `T` workers each need a histogram of
every bucket, so the O(g²) work becomes `T`-fold. At the `T = 16` the threaded
rows record and `g = 512`, an upper bound on the added work is
`16 x 0.202 = 3.2 ms` of serial-equivalent time, against 6.8 ms of O(n) work
that gets divided by the same 16. The bound is loose in the direction that
favours the contingency: the per-worker zeroing is parallel, and only the
cross-thread prefix is genuinely serialised. It is loose in the other direction
too, because this measurement does **not** separate the zero from the prefix,
and which of the two carries the 0.19 ms decides how much of the 3.2 ms
parallelises. Separating them needs a clock inside `grid_sort`, which is the
thing this measurement was built to avoid.

**One column did not reproduce and it is left unexplained.** The near-sorted
build at 1M reproduces the probe above: 6.959 to 7.755 ms here against its
7.049 ms. The unsorted column does not. It runs 39.6 to 44.7 ms at `g = 512`
against the 26.521 ms recorded there, and it inverts that section's near-equality
between the two dimensions. Nothing here identifies a cause, and no claim is made
that the recorded figure is wrong or that a regression exists; the host was not
quiesced for either. The unsorted column is printed because dropping a column
that disagreed would be the worse of the two options, and nothing in this
section's reading rests on it.

## Risk 2's contingency, built and measured (#243)

The section above says what the contingency trades. This is what it costs and
what it returns, with the parallel scatter in the tree rather than argued about.

`swarm_build_mt` (`pool_build`, `src/platform/pool.inc`) runs the counting sort's
two O(n) phases across the worker pool: each worker histograms its own slice into
its own block of per-cell counts, one sweep turns those blocks into per-worker
per-cell write cursors and publishes `cell_start`, and each worker then scatters
its own slice forward from its own cursors. The result is byte-identical to the
serial `swarm_build` at every worker count, asserted by
`BuildParallelMatchesSerial` in `tests/Swarm.Tests/ThreadingTests.cs` over the
whole arena image - the reordered bank and the run-start table together - on the
unsorted first frame and on the near-sorted steady state.

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run -c Release --project tests\Swarm.Bench\Swarm.Bench.csproj -- --buildmt
```

Reference machine: AMD Ryzen 9 5950X (Zen 3), 16 physical / 32 logical cores,
Windows 11, AVX2 (`swarm_cpu_paths = 0x1`). `n = 1,048,576`, both grid dimensions
the 1M scenes use, seed and matrix as every other grid row here. Each figure is
the best of nine rounds. **The host was not quiesced.** The serial column is
re-measured inside the same run, so the two columns are subtractable.

- **Commit**: `49fba06` plus this change, which landed as `c5e6ce0` ·
  **Date**: 2026-08-09.

### The two runs

`T` is the pool's actual worker count; the two `16` rows in each block are the
explicit request and the auto-detect, which resolve to the same number on this
part. `x` is serial ms divided by parallel ms.

`rmax = 1/512`, `g = 512`, 262,144 cells - the headline scene:

| run | serial sorted | T=1   | T=2   | T=4   | T=8   | T=16  | T=16 (auto) |
| --- | ------------- | ----- | ----- | ----- | ----- | ----- | ----------- |
| 1   | 5.741         | 5.694 | 4.380 | 3.617 | 3.598 | 4.085 | 3.338       |
| 2   | 5.712         | 5.748 | 4.498 | 3.135 | 3.731 | 3.087 | 3.459       |

| run | serial unsorted | T=1    | T=2    | T=4    | T=8    | T=16   | T=16 (auto) |
| --- | --------------- | ------ | ------ | ------ | ------ | ------ | ----------- |
| 1   | 21.086          | 17.968 | 16.431 | 12.001 | 12.475 | 12.594 | 11.103      |
| 2   | 23.963          | 22.001 | 13.485 | 10.002 | 10.844 | 11.830 | 11.445      |

`rmax = 1/256`, `g = 256`, 65,536 cells - the dense scene:

| run | serial sorted | T=1   | T=2   | T=4   | T=8   | T=16  | T=16 (auto) |
| --- | ------------- | ----- | ----- | ----- | ----- | ----- | ----------- |
| 1   | 5.958         | 5.652 | 3.922 | 2.598 | 2.532 | 2.183 | 2.179       |
| 2   | 5.818         | 5.460 | 3.183 | 3.026 | 2.353 | 2.393 | 2.153       |

| run | serial unsorted | T=1    | T=2    | T=4   | T=8   | T=16  | T=16 (auto) |
| --- | --------------- | ------ | ------ | ----- | ----- | ----- | ----------- |
| 1   | 20.638          | 15.390 | 12.364 | 7.691 | 5.203 | 7.151 | 6.905       |
| 2   | 17.335          | 13.532 | 10.577 | 8.189 | 4.625 | 6.412 | 6.913       |

### Reading it - risk 2's line is met at the headline scene

At the worker count the shipped binary resolves to, the headline scene's
near-sorted build is **3.338 and 3.459 ms** across the two runs, against risk 2's
`~4.5 ms` line and against 5.741 and 5.712 ms serial in the same runs. The line
is met. The dense scene comes to 2.179 and 2.153 ms.

The first frame moves further in absolute terms and less in ratio: 21.086 to
11.103 ms and 23.963 to 11.445 ms at `g = 512`. That frame is still four times
over the 16.67 ms budget on its own, and nothing here changes that - the
acceptance in #125 discards 600 warm-up frames, so the headline claim is not
exposed to it, and a run's first frame still is.

**The build does not use the whole pool, and that is the result rather than a
detail.** Measured before the bound existed, with every worker taking part, the
headline scene at `T = 16` ran 0.75x and 1.03x across two runs - at or below
break-even, and slower than the serial build it replaces on one of them. The
mechanism is the one the section above predicted: a worker whose slice holds
fewer particles than the cell array holds buckets spends more time walking
buckets than particles. So `pool_build` splits the build across
`W = clamp(n / (g*g), 1, T)` workers and leaves the rest parked, while the pass
continues to use all of them.

That bound is derived from the two work terms rather than fitted, and it lands on
the measured optimum at both dimensions independently: `W = 4` at `g = 512`, where
the un-bounded sweep peaks at `T = 4`, and `W = 16` at `g = 256`, where it peaks
at the top. It is a rule about one machine's measured optimum only to the extent
that both dimensions agreeing is evidence; a third dimension has not been
measured, and no claim is made beyond the two rows above.

**What the sweep above therefore is.** With the bound in place, `T` is the pool
size and not the number of workers the build uses, so the `g = 512` rows from
`T = 4` upward are all running `W = 4` and their differences are host noise, not
scaling. The spread across those rows - 3.087 to 4.085 ms for the same work - is
the size of that noise on an unquiesced host, and it is larger than several of
the steps in the tables above.

**One figure to carry forward rather than settle here.** `docs/BENCHMARKS.md`
records the 1M threaded frame at 15.535 ms worst-run, of which the build was
7.232 ms. These runs put the same build between 3.3 and 3.5 ms with a serial
column of 5.7, which is neither the 7.232 nor the 7.049 of the probe. The
instrument, the host state and the day differ; subtracting one from the other
would be a claim rather than a measurement. The frame re-taken with this change
in it is the section below.

## The 1M threaded frame with the parallel build in it (#125)

The section above ends by refusing a subtraction: the build got faster and the
frame figure it belongs to was measured before that, on another day and another
host state. This is that frame re-taken, so the two halves come from one run of
one instrument.

The harness change is that the threaded rows now time the parallel build at the
same `T` as the pass and add that, instead of carrying the serial build in from
the section above. The serial-build column is printed beside it, because the
recorded rows under "The 1M baseline" carry that shape and a reader has to be
able to tell the two generations apart without arithmetic across runs.

**Three whole runs**, as that section takes them, and every reading below is on
the worst of the three.

```
dotnet run --project tests/Swarm.Bench/Swarm.Bench.csproj -c Release
```

### Serial, one core, in the same three runs

| run | scene    | build ms | pass ms | frame ms |
| --- | -------- | -------: | ------: | -------: |
| 1   | headline |    6.609 |  61.213 |   67.822 |
| 2   | headline |    7.829 |  69.666 |   77.495 |
| 3   | headline |    7.534 |  61.320 |   68.854 |
| 1   | dense    |    8.120 | 180.521 |  188.642 |
| 2   | dense    |    8.779 | 158.941 |  167.720 |
| 3   | dense    |    7.779 | 193.182 |  200.961 |

### Parallel build and threaded pass

`T = 16*` is the pool's auto-detected physical-core count. `serial-build frame`
is the same threaded pass with the serial build of that run added back, i.e. the
shape the recorded 1M rows carry.

| run | scene    |    T | build ms | pass ms | frame ms |  fps | serial-build frame ms |
| --- | -------- | ---: | -------: | ------: | -------: | ---: | --------------------: |
| 1   | headline |    8 |    4.638 |  13.393 |   18.032 | 55.5 |                20.003 |
| 2   | headline |    8 |    4.185 |  13.291 |   17.475 | 57.2 |                21.120 |
| 3   | headline |    8 |    3.653 |  13.062 |   16.714 | 59.8 |                20.595 |
| 1   | headline | 16\* |    3.607 |   7.291 |   10.898 | 91.8 |                13.901 |
| 2   | headline | 16\* |    4.200 |   7.148 |   11.348 | 88.1 |                14.977 |
| 3   | headline | 16\* |    4.423 |   7.471 |   11.894 | 84.1 |                15.005 |
| 1   | dense    | 16\* |    2.690 |  18.391 |   21.081 | 47.4 |                26.511 |
| 2   | dense    | 16\* |    2.416 |  17.659 |   20.075 | 49.8 |                26.438 |
| 3   | dense    | 16\* |    3.232 |  20.408 |   23.640 | 42.3 |                28.188 |

The full `T` sweep (1, 2, 4, 8, auto) is in the harness output; the rows above
are the ones that carry the reading.

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  build 10.0.26200, 32 logical processors. **Feature path**: `swarm_cpu_paths`
  reports `0x1`, so AVX2 and no AVX-512.
- **Scenes**: `n = 1,048,576`, 6 species, seed `0x5EED`, `beta = 0.3`,
  `dt = 0.02`, `friction = 0.71`, `force_scale = 10`, `FLAG_GRID`, and the
  harness's deterministic `sin`-filled matrix. Headline is `rmax = 1/512` so
  `g` is 512; dense is `rmax = 1/256` so `g` is 256.
- **build** is near-sorted in both columns, i.e. every frame after the first.
- **Kernel commit**: `cfac27b`, unchanged by this measurement; the harness edit
  that prints these columns lands with this section. **Date**: 2026-08-17.
- **The host was not quiesced.** The serial headline frame moves 9.7 ms across
  the three runs, on a figure of ~70, which is the same spread the earlier 1M
  section recorded and is larger than several of the steps below.

### Reading it

**The threaded headline frame at the seam is 11.894 ms worst-run**, against the
16.67 ms budget, and 10.898 ms on the best. The dense scene is 23.640 ms worst
and 20.075 ms best, so it stays about 1.4x over.

**What the parallel build is worth in the frame is now a subtraction inside one
run** rather than across two. On the worst headline run it is 15.005 - 11.894 =
3.111 ms; on the best, 13.901 - 10.898 = 3.003 ms. That is the figure the
section above declined to state, and it can be stated here only because both
columns came out of the same process on the same host state.

**The build's share of the threaded frame has roughly halved.** The earlier
section reads the serial build as 7.232 of 15.535 ms, about 47%. Here it is
4.423 of 11.894 ms on the worst run, about 37%, and the pass is what is left to
attack.

**At `T = 1` the parallel build is level with the serial one**, within the
run-to-run spread: `build x` comes out 0.81, 1.04 and 1.02 across the three
runs. The contingency neither costs nor buys anything at one worker, which is
what the `W = clamp(n/(g*g), 1, T)` bound predicts.

**This is not a frame-rate claim, and it is not #125's acceptance.** Every
figure here is a minimum over nine rounds at the P/Invoke seam. Plot and blit
are not in it, no percentile is in it, and the live capture rows further down
this document - worst p99 150.849 ms on `presets/headline.txt` - are the frame a
user actually waits for and are untouched by this. What this section changes is
the size of the kernel-seam half of the gap, not the gap.

## The plot phase at 1M (#125)

Decision 11's acceptance asks for the frame broken into build, pass, plot and
blit. The two sections above cover build and pass. Plot had no figure anywhere
in this document at any count, so the only thing known about it was that it sat
somewhere inside the undivided work window the live rows record.

`plot_core` is two pieces of different shape. The clear is a `rep stosd` over
`w * h` pixels and does not depend on `n` at all; the raster is one scattered
dword store per particle and depends on `n` and on where the particles are.
They are separated here the way the build's two halves are (#243), by fitting
the bottom four rungs of an `n` ladder back to `n = 0`, because the grammar
accepts no `n` small enough to time a clear on its own.

`w` and `h` are the shipped executable's framebuffer, `FRAME_W = FRAME_H = 1024`
at `src/swarm.asm:36-37`, so this is the buffer the live loop actually rasters
into. `FLAG_SPLAT` is off in every row, which is the 1-pixel raster the rest of
this document publishes.

**Three whole runs.**

```
dotnet run --project tests/Swarm.Bench/Swarm.Bench.csproj -c Release -- --plot
```

### The n ladder, OUT cell-ordered, rmax = 1/512

`lit px` is the share of the framebuffer the raster left non-background,
counted off the buffer the timed calls left behind. It is a property of the
state rather than of the clock, and it came out identical in all three runs.

|         n | run 1 ms | run 2 ms | run 3 ms | lit px % |
| --------: | -------: | -------: | -------: | -------: |
|      1024 |    0.187 |    0.176 |    0.177 |      0.1 |
|      2048 |    0.184 |    0.186 |    0.185 |      0.2 |
|      4096 |    0.194 |    0.199 |    0.197 |      0.4 |
|      8192 |    0.213 |    0.214 |    0.216 |      0.8 |
|     16384 |    0.269 |    0.296 |    0.298 |      1.5 |
|     65536 |    0.421 |    0.449 |    0.452 |      6.0 |
|    262144 |    0.753 |    0.894 |    0.786 |     22.1 |
|   500,000 |    1.220 |    1.297 |    1.213 |     38.0 |
| 1,048,576 |    2.366 |    3.482 |    2.414 |     63.0 |

| run | clear (fitted to n = 0) ms | plot at 1M ms | raster there ms | raster % |
| --- | -------------------------: | ------------: | --------------: | -------: |
| 1   |                      0.179 |         2.366 |           2.187 |     92.4 |
| 2   |                      0.174 |         3.482 |           3.307 |     95.0 |
| 3   |                      0.173 |         2.414 |           2.240 |     92.8 |

The clear is an extrapolation and is printed beside the rung it starts from:
the bottom rung is 0.176 to 0.187 ms and the fit moves it by hundredths.

### The state of bank OUT at n = 1,048,576

`ordered` is build then pass, so OUT sits at cell-ordered indices, which is
every frame after the first. `id-order` is the initial draw, never built, which
is the first frame only. `settled` is `swarm_step` run 600 times first, the
warm-up decision 11's acceptance discards.

|     rmax |   g | state    | run 1 ms | run 2 ms | run 3 ms | lit px % |
| -------: | --: | -------- | -------: | -------: | -------: | -------: |
| 0.001953 | 512 | ordered  |    2.410 |    2.374 |    2.337 |     63.0 |
| 0.001953 | 512 | id-order |    2.996 |    3.869 |    2.840 |     63.3 |
| 0.001953 | 512 | settled  |    2.594 |    2.354 |    2.903 |     62.1 |
| 0.003906 | 256 | ordered  |    2.502 |    2.523 |    3.566 |     62.0 |
| 0.003906 | 256 | id-order |    4.054 |    3.289 |    3.573 |     63.3 |
| 0.003906 | 256 | settled  |    2.390 |    2.462 |    3.527 |     60.8 |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  build 10.0.26200, 32 logical processors. **Feature path**: `swarm_cpu_paths`
  reports `0x1`, so AVX2 and no AVX-512.
- **Params**: 6 species, seed `0x5EED`, `beta = 0.3`, `dt = 0.02`,
  `friction = 0.71`, `force_scale = 10`, `FLAG_GRID`, and the harness's
  deterministic `sin`-filled matrix. **Only `rmax` is taken from the committed
  scenes**; every other field is this harness's standard set, so no row here is
  `presets/headline.txt` or `presets/dense.txt`.
- **Kernel commit**: `4e04539`, unchanged by this measurement; the harness
  section that prints these tables lands with this text. **Date**: 2026-08-17.
- **The host was not quiesced.** The spread is the reading below rather than a
  footnote to it.

### Reading it - the plot is small, and its input state does not move it here

**The whole plot at 1M is 2.3 to 4.1 ms**, taking every row and every run
together. The largest figure anywhere above is 4.054 ms and the smallest 2.337.

**It is the raster and not the clear.** The clear comes out 0.173 to 0.179 ms
across three runs, one of the steadiest quantities in this document, and the
raster is 92 to 95% of the plot at 1M. So the phase scales with the particle
count and not with the window, and a larger framebuffer would not move it much
at this `n`.

**The three OUT states do not separate.** The temptation is to read cell-ordered
as the cheap one, and the rows refuse it: `ordered` at `g = 256` reaches 3.566,
above `id-order`'s best of 2.840 and `settled`'s best of 2.390, and the spread
_within_ one row reaches 1.49x (2.390 to 3.527). Nothing here supports an
ordering claim in either direction. The likely reason is that the framebuffer is
1024 \* 1024 \* 4 = 4 MiB and fits this part's L3 several times over, so the
scatter never reaches memory and locality has little left to buy.

**The settled rows are not evidence about a clustered scene.** For `n` balls in
`n` bins an independent uniform draw fills `1 - 1/e` = 63.2% of them, and the
`id-order` rows measure 63.3%. After 600 steps the settled rows read 62.1% and
60.8%, so at pixel resolution these scenes are still very nearly uniform. A
scene that genuinely clustered would show a much lower lit share, and none here
does; whether such a scene rasters differently is not answered by these rows.

**What this does to #125's gap, and what it does not.** The live capture rows
further down record a worst p99 of 150.849 ms on `presets/headline.txt`, and the
threaded seam frame above is 11.894 ms worst-run. The plot phase is bounded at
about 4 ms across everything measured here, which is 2.7% of that p99, so **plot
is not where the difference sits**. That is a bound and not a subtraction, for
two reasons that both have to be said: every figure above is a minimum over nine
rounds, so it describes the phase's floor and not its tail, and the params are
this harness's rather than the committed scene's. What is left unattributed is
the blit and whatever the live pass costs on an evolved scene.

That last sentence named #152 as what would separate them. It is the wrong
pointer twice over. #152 landed as the percentile report inside `-capture`, not
as a per-phase split, so the live instrument still times one undivided window;
and the evolved-scene half needed no new instrument at all. The section below
takes it at the seam by settling the committed scene first, and finds that half
is the whole of it.

## The committed headline scene along its settle depth (#125)

The three sections above measure build, pass and plot at 1M and leave a hole in
the middle of the document. They are taken on this harness's params, on a bank
that has never been stepped, and they add up to a threaded frame of 11.894 ms
worst-run. The live capture rows further down are taken on
`presets/headline.txt` over 3600 frames and record a worst p99 of 150.849 ms.
Nothing here explained the factor between them, and the plot section says so.

This section is the missing variable measured rather than argued about: the
**settle depth**. It runs the seam on the committed scene's own params, at the
depths a capture walks through - the field `swarm_init` leaves, decision 11's
600 discarded warm-up frames, and on to 3600, which is one capture.

```powershell
dotnet run --project tests/Swarm.Bench/Swarm.Bench.csproj -c Release -- --scene
```

The candidate count travels with every row because it is the mechanism a
clustering scene would work through: the force loop walks the 3x3 cell
neighbourhood, so the same `n` costs whatever the occupancy of those nine cells
is. A count that did not move would rule that mechanism out rather than leave it
as a story told about the timings.

### What a rung costs the scene it measures

One arena carries a whole run of the ladder, and a rung does not leave the world
where it found it: `build_core` is OUT to IN and `pass_core` is IN to OUT, so the
timing rounds advance the scene. The offset is priced rather than argued for, on
a fresh arena advanced by `swarm_step_mt` with nothing else touching it. The
candidate count is the probe because it is a property of the state alone, with no
clock in it.

| settle | cand/particle |
| -----: | ------------: |
|    600 |         220.3 |
|    601 |         219.3 |
|    602 |         221.0 |
|    603 |         219.9 |

The ladder's 600 rung reports 221.0, which is the 602 row. So a rung leaves the
scene exactly two steps further on, the `steps` column below is the settle alone,
and the deepest rung sits at 3612 rather than 3600 - four tenths of a percent of
the depth, against a quantity that moves by a factor of 29 across the table.

The 600 row is also the serial stepper's answer. `swarm_step(arena, 600)` on the
same params reports the same 220.3, which is `pool_step`'s bit-identity contract
holding on this scene rather than being taken on trust.

### The ladder, three whole runs

`cand/p` and `lit %` came out identical in all three runs, to every digit
printed, so they are carried once.

| steps | cand/p | lit % |
| ----: | -----: | ----: |
|     0 |   37.0 |  63.4 |
|   600 |  221.0 |  39.8 |
|  1200 |  543.4 |  28.2 |
|  1800 |  824.8 |  19.5 |
|  2400 |  983.0 |  15.1 |
|  3000 | 1057.5 |  13.1 |
|  3600 | 1092.0 |  12.6 |

Serial, one core:

| steps | build ms |  pass ms | plot ms | frame ms |
| ----: | -------: | -------: | ------: | -------: |
|     0 |    6.202 |   57.866 |   2.405 |   66.473 |
|     0 |    5.988 |   55.856 |   2.269 |   64.113 |
|     0 |    8.170 |   73.327 |   3.741 |   85.239 |
|   600 |    5.832 |  192.708 |   2.374 |  200.915 |
|   600 |    5.775 |  193.195 |   2.246 |  201.215 |
|   600 |    6.595 |  214.470 |   2.610 |  223.674 |
|  1800 |    5.766 |  657.530 |   2.237 |  665.534 |
|  1800 |    5.657 |  666.169 |   2.337 |  674.162 |
|  1800 |    6.165 |  847.771 |   3.697 |  857.633 |
|  3600 |    5.255 |  837.395 |   2.317 |  844.967 |
|  3600 |    7.747 |  944.669 |   2.839 |  955.255 |
|  3600 |    6.755 | 1060.635 |   2.931 | 1070.321 |

Threaded, `T = 16` (the pool's auto-detected physical-core count), with the plot
column repeated from the serial table because the raster is not fanned out:

| steps | mt build ms | mt pass ms | mt frame ms |  fps |
| ----: | ----------: | ---------: | ----------: | ---: |
|     0 |       3.008 |      6.417 |      11.831 | 84.5 |
|     0 |       2.929 |      6.410 |      11.608 | 86.1 |
|     0 |       3.751 |      8.904 |      16.396 | 61.0 |
|   600 |       3.050 |     22.729 |      28.153 | 35.5 |
|   600 |       2.870 |     23.378 |      28.493 | 35.1 |
|   600 |       4.849 |     34.275 |      41.733 | 24.0 |
|  1800 |       2.964 |     69.034 |      74.236 | 13.5 |
|  1800 |       3.029 |     59.588 |      64.954 | 15.4 |
|  1800 |       3.873 |     91.343 |      98.912 | 10.1 |
|  3600 |       2.876 |     84.659 |      89.852 | 11.1 |
|  3600 |       4.761 |    205.538 |     213.138 |  4.7 |
|  3600 |       4.221 |    120.670 |     127.822 |  7.8 |

The 1200, 2400 and 3000 rungs are in the harness output; the four above are the
ones the reading rests on.

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise build
  10.0.26200, 32 logical processors. **Feature path**: `swarm_cpu_paths` reports
  `0x1`, so AVX2 and no AVX-512; the scene names no path, so `force_path = 0`
  resolves to `PATH_AVX2`.
- **Scene**: `presets/headline.txt`, field for field - `n = 1,048,576`, 4
  species, seed `0x9E3779B97F4A7C15`, `rmax = 0.001953` so `g` is 512,
  `beta = 0.3`, `dt = 0.02`, `friction = 0.71`, `force_scale = 10`, and the
  matrix the file carries. `FLAG_GRID` is applied, which is what the exe does
  with a preset. This is the first seam section in this document taken on a
  committed scene rather than on the harness's own params.
- **Framebuffer**: 1024 x 1024, the shipped executable's, `FLAG_SPLAT` off.
- **Kernel commit**: `47434aa`, unchanged by this measurement; the harness mode
  that prints these tables lands with this text. **Date**: 2026-08-22.
- Every figure is a minimum over nine rounds, as everywhere in this harness.
- **The host was not quiesced, and it shows more here than anywhere above.** The
  threaded 3600 rung spans 89.852 to 213.138 ms across the three runs. The
  readings below therefore take the FASTEST run wherever a figure is used to
  argue that the frame is too large, because the fast run is the conservative
  direction for that claim.

### Reading it - the gap is the scene, and it is the pass

**The neighbourhood the force loop walks grows by a factor of 29.5 over one
capture**, 37.0 candidates per particle at step 0 and 1092.0 at 3600. The 37.0 is
the figure this document already records for the uniform headline scene, so the
ladder starts where the recorded rows sit and leaves them behind.

**The scene clusters, and the raster is the witness.** The lit share of the
framebuffer falls from 63.4% to 12.6% while `n` never changes, so a million
particles end up in an eighth of the pixels they started in. Whether that follows
the count or the parameters is not answerable from this ladder alone, which moves
both; the section after it holds each still in turn and answers it. The plot section
above measures three OUT states and finds none of them below 60.8% lit, and notes
that a genuinely clustered scene would show a much lower share and that whether
one exists was not answered there. This is that scene, and it does.

**All of the growth is the pass.** Across the whole ladder the build stays
between 5.255 and 8.170 ms serial and 2.870 and 4.849 ms threaded, and the plot
between 2.237 and 3.741 ms. Neither has a trend in it. The threaded pass goes
from 6.417 ms to 84.659 ms on the fastest run, a factor of 13.2.

**It is sublinear in the candidate count**, 13.2x of pass against 29.5x of
candidates, so the per-candidate cost falls as the scene clusters. Nothing here
measures why; a denser cell's candidates being contiguous is the obvious guess
and it stays a guess.

**This closes the hole the two sections above left open.** The recorded live
capture on this scene reports a mean of 91.030 ms and a p50 of 103.353 ms. The
threaded seam frame at 3600 steps is 89.852 ms on the fastest run, and it does
not include the blit. The live frame and the seam frame are the same frame once
the seam is measured at the depth the live run is measured at, so the factor of
roughly nine between them was never the instrument, the blit, or anything
unmeasured: it is the settle. What is left over for the blit and for the
distribution's tail is small beside it, and neither is separated here.

**What it does to the lever order.** #125's step 2 lists the M3 kernel levers
against a frame whose pass was 7.291 ms threaded. On the committed scene at
capture depth the pass is 84.659 ms on the fastest run, so the levers on that
list move a percentage of a figure that is itself twelve times larger than the
whole 16.67 ms budget. The build was 37% of the frame in the section above and is
3.2% here. Any route to the headline goes through what the pass costs once the
scene has clustered, and this document holds no measurement of that quantity
other than the tables above.

**Two things this section does not do.** It states no percentile: every figure is
a floor over nine rounds and the live acceptance is a p99. And it never touches
the blit, which is `BitBlt` behind the platform boundary the seam does not
reach - so three of decision 11's four phases are now measured at this scene and
the fourth is still not.

## The same ladder on the harness scene, at both counts (#5)

The section above changed two things at once. It moved from this harness's params
to `presets/headline.txt` **and** it walked a settle depth no seam row had walked,
and it is the settle that its reading attributes the clustering to. That leaves
the obvious question unanswered: is the clustering a property of a million
particles, or of that particular scene?

It also leaves a published claim standing on ground worth re-examining. The M3
pool section reads its table as 500,000 particles clearing 60 fps, and it makes
that reading by taking the threaded pass off an **unstepped** bank and the build
off the **settled** distribution, in the same sentence. Each half is the cheaper
of the two states available to it, and no single frame is ever in both.

This runs the same ladder on the harness's own scene at both counts. Against the
section above it holds the count still and moves only the params; between its own
two counts it holds the params still and moves only the count.

```powershell
dotnet run --project tests/Swarm.Bench/Swarm.Bench.csproj -c Release -- --m3settle
```

### The scene does not cluster, at either count

`cand/p` and `lit %` were identical across all three runs at each count, so they
are carried once.

| steps | cand/p @ 500k | lit % @ 500k | cand/p @ 1M | lit % @ 1M |
| ----: | ------------: | -----------: | ----------: | ---------: |
|     0 |          18.2 |         38.0 |        37.0 |       63.0 |
|   600 |          18.4 |         37.7 |        37.5 |       62.2 |
|  1800 |          18.4 |         37.7 |        37.4 |       62.1 |
|  3600 |          18.4 |         37.7 |        37.5 |       62.1 |

Threaded frame (`mt build + mt pass + plot`), over all seven rungs of all three
runs at each count:

| scene              | frame ms, lowest | frame ms, highest |
| ------------------ | ---------------: | ----------------: |
| harness, 500,000   |            7.307 |             9.062 |
| harness, 1,048,576 |           13.587 |            16.975 |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise build
  10.0.26200, 32 logical processors. **Feature path**: `swarm_cpu_paths` reports
  `0x1`, so AVX2 and no AVX-512; `force_path = 0` resolves to `PATH_AVX2`.
- **Scene**: this harness's standard set - 6 species, seed `0x5EED`, the
  deterministic `sin`-filled matrix, `beta = 0.3`, `dt = 0.02`,
  `friction = 0.71`, `force_scale = 10`, `FLAG_GRID`, `rmax = 1/512` so `g` is
  512 at both counts. It is the scene the M3 pool table and the 1M baseline rows
  were taken on, which is why it and not another one is here.
- **Framebuffer**: 1024 x 1024, `FLAG_SPLAT` off, as above.
- **Kernel commit**: `3ac0bb6`, unchanged by this measurement. **Date**:
  2026-08-22.
- Every figure is a minimum over nine rounds. The rung offset the section above
  prices applies here unchanged: a rung leaves the scene two steps further on.
- **The host was not quiesced.** The full tables are in the harness output; the
  four rungs above are the ones the reading rests on.

### Reading it - the clustering is the scene, and the 500k claim holds

**Nothing clusters here.** Over the depth that took the committed headline scene
from 37.0 candidates per particle to 1092.0, this scene moves from 37.0 to 37.5,
and its lit share of the framebuffer from 63.0% to 62.1%. The settle is not a
force that clusters a scene; it is time, and what a scene does with time is a
property of its matrix and its species count.

**The two 1M scenes start identically and end a factor of 29 apart.** Both report
37.0 candidates per particle at step 0, which is what `n / g²` over a 3x3
neighbourhood gives and is a check that the two ladders are measuring the same
grid. What separates them is entirely what the parameters do to the scene
afterwards. So the gap the section above attributes to settle depth is a property
of `presets/headline.txt`, not of the particle count, and no lever inside the
kernel is what stands between the two numbers.

**"500k @ 60 fps reached" survives this.** The threaded frame stays between 7.307
and 9.062 ms at every depth in every run, with the plot inside it, against a
16.67 ms budget. The reading being examined arrives at 15.487 ms by pairing the
worst-case build with the unstepped pass, and the measured settled frame is
better than that, not worse. The asymmetry in how it was assembled is real and
worth not repeating; the conclusion it reached is correct on this scene.

**1M on this scene straddles the budget rather than clearing it.** 13.587 to
16.975 ms across 21 rows, against 16.67. That is consistent with the recorded
threaded 1M row of 11.894 ms once this section's plot column, 2.5 to 3.7 ms, is
added to it - those rows carry build plus pass and no raster. Neither figure is a
frame rate: the blit is not in either, and both are floors over nine rounds
rather than the p99 the acceptance asks for.

## Scatter locality under an energetic scene (risk 3's probe; #178)

Masterplan open risk 3 says the scatter estimate assumes temporal coherence, and
that a hot matrix at the `v_max` clamp degrades write locality. Its probe is
named in the risk itself: an adversarial preset, all `|a| = 1` and high force,
against the coherent scene. Its fallback is its own, a two-pass radix over cell
row then cell, and is not risk 2's parallel scatter above.

**Three scenes, not two.** The scene every other row in this document uses is
not a calm control: measured below, it already sits with 64% of its velocity
components at the clamp. A two-scene probe would have compared energetic against
energetic and reported the difference as an answer.

**All three are stepped before they are timed.** A scene is not energetic at
frame 0. It is energetic once the matrix has driven velocities to the clamp and
pulled the population into clumps and voids, so each scene runs 120 steps first.
Timing frame 0 would compare three identical uniform-random distributions and
find, correctly and uselessly, no difference.

**Repeats are interleaved, not blocked**, so host drift lands on all three
scenes rather than on whichever ran last.

| rep | scene       | force_scale | at v_max | build ms | pass ms |
| --- | ----------- | ----------: | -------: | -------: | ------: |
| 1   | calm        |         1.0 |     0.1% |    7.196 |  85.375 |
| 1   | coherent    |        10.0 |    64.0% |    7.162 |  69.818 |
| 1   | adversarial |       100.0 |    97.8% |    6.230 |  70.692 |
| 2   | calm        |         1.0 |     0.1% |    5.793 |  65.617 |
| 2   | coherent    |        10.0 |    64.0% |    6.300 |  68.280 |
| 2   | adversarial |       100.0 |    97.8% |    6.403 |  70.792 |
| 3   | calm        |         1.0 |     0.1% |    5.899 |  66.849 |
| 3   | coherent    |        10.0 |    64.0% |    6.696 |  67.232 |
| 3   | adversarial |       100.0 |    97.8% |    6.022 |  63.723 |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  build 10.0.26200. **Feature path**: `swarm_cpu_paths` reports `0x1`;
  `force_path = 1`, single-threaded.
- **Scenes**: `n = 1,048,576`, `rmax = 1/512` so `g = 512` for all three, 6
  species, seed `0x5EED`, `FLAG_GRID`, 120 steps before timing. `adversarial`
  sets every matrix cell to +1 or -1 and `force_scale` to the grammar's ceiling
  of 100. `calm` and `coherent` keep the harness's varied `sin` matrix and
  differ from each other only in `force_scale`, 1 against 10.
- **at v_max** is the share of the 2n velocity components sitting at the
  per-axis clamp after the settle, read back through `swarm_read_state`. It is
  identical across reps to the printed precision because the simulation is
  deterministic.
- **build** is the near-sorted counting sort, which is the input state risk 3 is
  written about. Each figure is a min over nine rounds.
- **Commit**: `15878b3` · **Date**: 2026-08-06.

### Reading the risk 3 numbers

**The premise is real and is measured rather than assumed.** The three scenes
span 0.1%, 64.0% and 97.8% of velocity components at the clamp, so the hostile
scene is hostile in exactly the way the risk describes, and the calm control is
genuinely calm.

**The predicted degradation does not appear.** The worst adversarial build,
6.403 ms, is below the worst calm build at 7.196 ms and below the worst coherent
build at 7.162 ms. Within one scene the spread across reps is 1.403 ms for calm,
0.862 ms for coherent and 0.381 ms for adversarial, so every difference between
scenes is smaller than the calm scene's own run-to-run spread. Nothing here
separates the three, and the hostile scene is the steadiest of them.

**So risk 3's fallback is not triggered.** The two-pass radix is not authorised
by this measurement, and no number here asks for it.

**A hypothesis for the direction, offered as a hypothesis.** Clustering
concentrates the scatter's writes into fewer distinct cells, which is better
locality rather than worse, and the risk assumed the opposite. This probe does
not measure it: nothing here reads the per-cell occupancy distribution, and
saying so is the honest end of the sentence.

**What this does not cover.** One grid dimension (`g = 512`), one particle count,
one settle length. A longer settle, a denser `g`, or a scene engineered to
oscillate rather than to clump could all behave differently and none was run.
The figures are minima over nine rounds, so they say what the cheapest observed
build costs, not what a p99 build costs. And the ~1.5-2 ms scatter estimate that
risk 3 opens with is separately wrong by roughly a factor of three at this
count - that is risk 2's finding in the section above, and it is not what this
probe measured.

## The M1 live frame at 8,192 (`swarm.exe -capture`; #171)

The M1 acceptance measurement, and the only row here taken from the shipped
executable instead of a harness. `swarm.exe -capture` runs the normal paced
live loop and records the QueryPerformanceCounter deltas of the **work window**
of each frame - step plus plot plus blit, never the pacing wait - then writes
3600 raw `u64` samples per phase to `swarm-frames.bin` and exits. The wait is
outside the window on purpose: a paced loop measured wall to wall reports
16.67 ms by construction and would say nothing about how much room is left.

The window is read at five points, so the dump carries five planes: `build`,
`pass`, `plot`, `blit` and the whole `frame` they add up to. The four phases
are consecutive deltas, so the identity is exact and `CaptureReportTests`
asserts it frame by frame. **The rows tabulated below predate the split** and
were taken when the window was two reads, and then four; every added read sits
inside the window, so its cost is counted in the phase that follows it and is
not subtracted anywhere.

The run also reduces those samples itself and writes `swarm-frames.txt` beside
the dump, so a reading of the capture does not depend on anyone remembering to
apply the snippet further down. The dump stays the artifact a figure is
recomputed from, and it stays in recorded order; the reduction runs after it has
landed. Both files come from the same run, so a figure in one belongs to the
samples in the other.

What the file looks like. This is run 1 of "The live frame split into its
phases" below, quoted whole rather than re-run, so the shape and a recorded row
are the same bytes. **It is a `'SWRMFRM2'` report**, so it carries a `step`
line where a run of the current executable writes a `build` line and a `pass`
line; the rest of the shape is unchanged:

```
swarm.asm frame-time capture
samples=3600  n=8192  species=4  flags=0x00000001  seed=0x9E3779B97F4A7C15  cpu_paths=0x1  qpc_freq=10000000
step  mean=1.039 ms  p50=1.043 ms  p99=1.687 ms  max=2.234 ms
plot  mean=0.222 ms  p50=0.215 ms  p99=0.313 ms  max=0.631 ms
blit  mean=0.419 ms  p50=0.398 ms  p99=0.668 ms  max=1.185 ms
frame mean=1.679 ms  p50=1.669 ms  p99=2.354 ms  max=3.152 ms
```

Each figure line carries the same four names in the same order the snippet
prints, and by the same definition: `FrameStatsTests` asserts the exe's
reduction against that definition across the P/Invoke seam, and
`CaptureReportTests` runs a real capture and checks every plane's line against
the plane in the dump beside it. The `frame` line is the whole work window and
is what a row recorded before the split holds; a phase percentile is that
phase's own distribution and does not add up to the frame's. Hardware, OS and the rest of the disclosure stay here rather than in
the file, because the exe cannot read them within kernel32/user32/gdi32.

The budget is one frame at 60 fps, 16.67 ms, on p99.

### rmax = 0.05, the shipped acceptance preset (g = 16)

| run | mean ms | p50 ms | p99 ms | max ms |
| --- | ------- | ------ | ------ | ------ |
| 1   | 3.187   | 2.941  | 6.100  | 9.068  |
| 2   | 2.149   | 1.690  | 5.066  | 5.980  |
| 3   | 1.499   | 1.487  | 2.099  | 2.344  |

### rmax = 0.08 (g = 8), a local preset edit, not shipped

| run | mean ms | p50 ms | p99 ms | max ms |
| --- | ------- | ------ | ------ | ------ |
| 1   | 2.346   | 2.475  | 2.806  | 7.104  |
| 2   | 2.322   | 2.453  | 2.778  | 5.124  |
| 3   | 2.348   | 2.470  | 2.816  | 14.244 |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  build 10.0.26200. **Feature path**: `swarm_cpu_paths` reports `0x1`, so AVX2
  and no AVX-512; the preset's `force_path = 0` resolves to `PATH_AVX2`. The
  live loop drives `pool_step`, so the pass runs threaded at the auto-detected
  16 physical cores while the grid build stays serial.
- **Scene**: `n = 8192`, `FLAG_GRID`, 4 species, seed `0x9E3779B97F4A7C15`,
  the preset compiled into the exe. `g` follows from `rmax` by the layout rule
  (largest power of two with `1/g ≥ rmax`): 16 at `rmax = 0.05`, 8 at 0.08.
  Every run is 3600 consecutive frames from process start, with no warm-up
  discarded - the first frames are in the samples.
- **Commit**: `e2f762b` · **Date**: 2026-08-06.
- The `rmax = 0.08` rows come from a **locally edited preset**, assembled,
  measured and reverted. No committed preset carries 0.08; the M1 amendment
  pins the shipped one at 0.05 or below, and `ExePresetTests` refuses a drift
  to 0.08 in the image.
- **The host was not quiesced.** Other work was running on the machine during
  the capture window, which is the honest reading of the `rmax = 0.05` spread
  below rather than something to average away.

Each figure above is recomputed from its own dump, so the number and the file
travel together:

```powershell
$b = [IO.File]::ReadAllBytes('swarm-frames.bin')
$freq = [BitConverter]::ToUInt64($b, 8)
$count = [BitConverter]::ToUInt64($b, 16)
$plane = 4   # 0 build, 1 pass, 2 plot, 3 blit, 4 the whole frame
$at = 40 + 8 * $count * $plane
$ms = @(for ($i = 0; $i -lt $count; $i++) { [BitConverter]::ToUInt64($b, $at + 8 * $i) * 1000.0 / $freq })
$s = $ms | Sort-Object
[string]::Format([Globalization.CultureInfo]::InvariantCulture,
  "mean={0:F3} ms  p50={1:F3} ms  p99={2:F3} ms  max={3:F3} ms",
  ($ms | Measure-Object -Average).Average,
  $s[[int][math]::Floor(0.50 * ($count - 1))],
  $s[[int][math]::Floor(0.99 * ($count - 1))],
  $s[$count - 1])
```

The dump is a 40-byte header - `'SWRMFRM3'`, then `qpc_freq`, `count`, `n`,
`flags`, `seed` - followed by five planes of `count` little-endian `u64` tick
deltas, in the order `build`, `pass`, `plot`, `blit`, `frame`. `count` is the
samples per plane, so the file is `40 + 40 * count` bytes. The magic is the
format version, and the two older ones are still readable: a `'SWRMFRM2'` dump
is four planes in the order `step`, `plot`, `blit`, `frame` at `40 + 32 * count`
bytes, and a `'SWRMFRM1'` dump is one plane read at `40 + 8 * i`. Every row
tabulated in this document was taken from one of those two, so the snippet
above needs its plane index and stride adjusted before it is pointed at an
archived dump. The scene the
samples belong to is inside the file, so a dump cannot be quoted against a run
it did not come from. `flags` carries the plot mode as well as
the spatial one, so it is what separates a 1-pixel capture from a `-splat` one.

**Plot mode: 1 pixel per particle** for the rows below, taken before the 2x2
raster existed.

### Reading the M1 numbers - 8,192 @ 60 fps reached

**The worst p99 of six runs is 6.100 ms against a 16.67 ms budget.** Not the
best run, not a mean of runs: the worst single reading is 2.7× inside budget,
and the worst individual frame anywhere in the six runs is 14.244 ms, still
under one frame period. **M1's acceptance count clears 60 fps**, and it does so
on the shipped preset with the shipped binary.

**The spread between runs is larger than anything the scene explains.** The
three `rmax = 0.05` runs report means of 3.187, 2.149 and 1.499 ms - a factor
of two across identical binaries, identical scene and identical seed. That is
the host, not the engine, and it is why the claim above is stated on the worst
reading. A quiesced machine would report a tighter and lower band; nothing here
needs it to, because the margin survives the noisy one.

**At this count the work window is not force-bound**, which is the more useful
result. `rmax = 0.08` gives `g = 8` and therefore roughly four times the
candidate neighbours per particle that `g = 16` does, so a force-dominated
window would show 0.08 far slower. It does not: the quietest 0.05 run sits at
1.499 ms mean against 2.346 for 0.08, a gap of ~0.85 ms where a four-fold force
increase would be much wider. So at least ~1.5 ms of the window is
`rmax`-independent, and the remaining headroom at 8,192 is not in the force
pass. The rmax-independent part is **not decomposed here** - the clear, the
plot and the `BitBlt` are one window in this instrument, and separating them
needs a finer one than #169 built.

**This does not transfer to a larger count.** The reading says the force pass
is cheap relative to a fixed 1024×1024 raster cost at 8,192 particles; at 500k
and 1M the force pass dominates again and the M2 and M3 rows above are the
relevant ones.

## The 1M live frame on the committed scenes (`swarm.exe -capture`; #166)

The first rows here quoted against **committed preset files** rather than
against a configuration described in prose. Both scenes are the decision 12
pair, and both are read from the file by the shipped exe:

```powershell
.\build\swarm.exe presets\headline.txt -capture
.\build\swarm.exe presets\dense.txt -capture
```

Same instrument as the M1 section above: the paced live loop, the work window
of each frame only (step plus plot plus blit, never the pacing wait), 3600
samples written to `swarm-frames.bin`, recomputed with the snippet printed
there. The budget is one frame at 60 fps, 16.67 ms, on p99. **These rows
predate the phase split**, so their dumps are `'SWRMFRM1'` and carry one plane;
the snippet reads such a dump at `40 + 8 * i`, as the M1 section states.

**Plot mode: 1 pixel per particle.** The commands above carry no `-splat`, and
the plot is inside the timed window, so the mode is part of what these rows
measure and a preset path does not state it. Decision 9's amendment requires it
beside the row for that reason. No 2x2 row exists here; that raster's cost has
not been measured.

### `presets/headline.txt` - 1M, rmax = 0.001953, g = 512, k = 12.6

| run | mean ms | p50 ms  | p99 ms  | max ms  |
| --- | ------- | ------- | ------- | ------- |
| 1   | 91.030  | 103.353 | 144.919 | 297.549 |
| 2   | 91.499  | 102.720 | 150.849 | 397.415 |
| 3   | 83.412  | 87.872  | 148.587 | 243.700 |

### `presets/dense.txt` - 1M, rmax = 0.003906, g = 256, k = 50.3

| run | mean ms | p50 ms  | p99 ms  | max ms  |
| --- | ------- | ------- | ------- | ------- |
| 1   | 140.640 | 151.018 | 244.476 | 428.052 |
| 2   | 137.985 | 151.884 | 222.048 | 265.362 |
| 3   | 136.928 | 147.497 | 229.806 | 333.474 |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise build
  10.0.26200. **Feature path**: `swarm_cpu_paths` reports `0x1`, so AVX2 and no
  AVX-512; both presets carry `force_path = 0`, which resolves to `PATH_AVX2`.
  The live loop drives `pool_step`, so the pass runs threaded across the
  auto-detected 16 physical cores while the grid build stays serial.
- **Scene**: whatever is in the two files, which is the point of this section.
  `n = 1048576`, 4 species, seed `0x9E3779B97F4A7C15`, `FLAG_GRID` applied by
  the exe, and only `rmax` differing between them. Each dump's own header
  repeats `n = 1048576`, `flags = 0x1` and `seed = 0x9E3779B97F4A7C15`, so a
  file cannot be quoted against a run it did not come from. `flags = 0x1` is
  `FLAG_GRID` alone, so the header also settles the plot mode: a `-splat` run
  records `0x3` and is not one of these.
- **Commit**: `33047f3` for `src/`, which the capture build is byte-for-byte,
  plus the two preset files added by this change. **Date**: 2026-08-08.
- Every run is 3600 consecutive frames from process start with **no warm-up
  discarded**, so the uniform opening field is inside the samples. One headline
  run costs about 5.5 minutes of wall clock and one dense run about 8.5.
- **The host was not quiesced.** Other work was running during the captures,
  which is what the `max` column and the spread between runs are for.

### Reading the 1M rows - the 60 fps budget is missed on both scenes

**Worst p99 of three headline runs is 150.849 ms against a 16.67 ms budget**,
about 9.0x over, and the dense scene's worst p99 is 244.476 ms, about 14.7x
over. Stated on the worst reading of three, as everywhere here. The 1M headline
target is not met by the shipped exe today and nothing in this section claims
otherwise; #125 is where the levers toward it are held.

**The two scenes differ by density and nothing else**, which is what makes the
pair worth carrying. `rmax` moves by a factor of two, `g` halves from 512 to
256, the candidate neighbourhood the force loop walks goes from 37.0 to 145.0
per particle, and the frame roughly 1.5x's. That the frame grows far less than
the candidate count does is the grid build and the raster cost being
`rmax`-independent, the same shape the M1 section reads at 8,192, and it is
**not decomposed here** - this instrument times one window.

**The mean sits below the p50 in all six runs.** That is a left tail rather
than a right one: a population of frames materially faster than the median,
which is where the uniform opening field lands before the scene organises. The
mechanism is not measured here, and the percentiles are reported over the whole
run rather than over a trimmed one, so the tables are the run and not an
edited version of it.

**These rows are not comparable to the #148 sweep**, though both are 1M at
`g = 512` and `g = 256`. The sweep times build plus pass as a minimum over
rounds on a frozen uniform bank, from the harness, at 6 species and seed
`0x5EED`; this section times the whole live window including plot and `BitBlt`,
as a distribution over an evolving scene, from the exe. Reading a plot cost out
of the difference between them would be comparing two different measurements.

## The live frame split into its phases (#125)

The first figures for the `BitBlt` anywhere in this document, and the first for
the raster taken from the shipped executable rather than from the harness.

#125's acceptance asks for a row "with the per-phase breakdown (build / pass /
plot / blit)". Until this section the live instrument timed one undivided
window, which is why three notes on that issue arrive at an unattributed
remainder without being able to say whether any of it was the blit. The window
is now read at five points and the dump carries five planes; the two sections
above are what the instrument looked like before, and their rows are not
re-taken here.

**THE ROWS IN THIS SECTION ARE FOUR-PLANE ROWS AND THE INSTRUMENT IS NOW
FIVE.** They were taken when `step` was one figure, and they are left as they
were rather than re-labelled: a row says what the run it came from recorded.
The paragraph below says what has changed since, and no build-against-pass row
has been taken from the live executable yet - the reason is at the end of this
section.

```powershell
.\build\swarm.exe -capture
.\build\swarm.exe presets\headline.txt -capture
```

**What a phase is, in the rows below.** `step` is `pool_step`, the grid build
and the force pass together. `plot` is `sim_plot`, the clear and the raster
into the DIB. `blit` is the `BitBlt` of that DIB into the window DC. `frame` is
the whole work window and is the figure a row recorded before any split holds.

**What a phase is now.** `step` is gone, replaced by `build` and `pass`. The
frame no longer makes one `pool_step` call: it performs that routine's loop
body itself - `pool_build`, then the read, then `pool_fanout` and the frame
counter - so the division is the product's own rather than a seam figure taken
on a harness scene. The cost of writing the step out twice is that the two
statements can drift, and `LiveFrameStepEquivalenceTests` is what stands under
it: it reads both sources and refuses a `pool_step` that grew an operation the
live frame does not perform, in either direction.

### The built-in acceptance preset - n = 8,192, rmax = 0.05, g = 16

| run | phase | mean ms | p50 ms | p99 ms | max ms |
| --- | ----- | ------- | ------ | ------ | ------ |
| 1   | step  | 1.039   | 1.043  | 1.687  | 2.234  |
| 1   | plot  | 0.222   | 0.215  | 0.313  | 0.631  |
| 1   | blit  | 0.419   | 0.398  | 0.668  | 1.185  |
| 1   | frame | 1.679   | 1.669  | 2.354  | 3.152  |
| 2   | step  | 1.048   | 1.045  | 1.679  | 3.154  |
| 2   | plot  | 0.225   | 0.217  | 0.320  | 0.803  |
| 2   | blit  | 0.426   | 0.406  | 0.676  | 1.267  |
| 2   | frame | 1.698   | 1.694  | 2.376  | 4.137  |
| 3   | step  | 1.040   | 1.048  | 1.713  | 2.003  |
| 3   | plot  | 0.219   | 0.213  | 0.302  | 0.777  |
| 3   | blit  | 0.407   | 0.390  | 0.629  | 2.003  |
| 3   | frame | 1.666   | 1.665  | 2.370  | 3.309  |

### `presets/headline.txt` - 1M, rmax = 0.001953, g = 512

| run | phase | mean ms | p50 ms  | p99 ms  | max ms   |
| --- | ----- | ------- | ------- | ------- | -------- |
| 1   | step  | 106.406 | 119.219 | 216.508 | 589.466  |
| 1   | plot  | 4.246   | 4.000   | 6.743   | 44.416   |
| 1   | blit  | 0.481   | 0.468   | 0.790   | 3.047    |
| 1   | frame | 111.134 | 124.142 | 224.029 | 632.265  |
| 2   | step  | 112.524 | 125.299 | 221.613 | 1720.733 |
| 2   | plot  | 4.697   | 4.469   | 8.103   | 23.992   |
| 2   | blit  | 0.516   | 0.489   | 0.931   | 25.072   |
| 2   | frame | 117.738 | 130.712 | 228.112 | 1726.760 |
| 3   | step  | 106.956 | 121.772 | 206.125 | 348.340  |
| 3   | plot  | 4.511   | 4.345   | 7.252   | 14.174   |
| 3   | blit  | 0.505   | 0.481   | 0.938   | 2.347    |
| 3   | frame | 111.971 | 126.834 | 212.169 | 355.933  |

Same machine, build, instrument and reduction as the table above it, disclosed
once below for both. Run 2's `max` of 1726.760 ms is what an unquiesced host
costs and is left in rather than trimmed.

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  build 10.0.26200. **Feature path**: every dump header reads
  `cpu_paths=0x1`, so AVX2 and no AVX-512, and the preset's `force_path = 0`
  resolves to `PATH_AVX2`. The live loop drives `pool_step`, so the pass and
  the grid build both run across the auto-detected 16 physical cores.
- **Scenes**: two, one per table. The preset compiled into the exe -
  `n = 8192`, 4 species, seed `0x9E3779B97F4A7C15`, `rmax = 0.05` so `g = 16`,
  `FLAG_GRID` - and the committed `presets/headline.txt`, which is the same 4
  species and seed at `n = 1048576` and `rmax = 0.001953`, so `g = 512`. Each
  dump header repeats `n`, `flags` and `seed`, and `flags = 0x00000001` is
  `FLAG_GRID` alone, so every run here is a 1-pixel raster and not a `-splat`
  one.
- **Build**: `build.ps1` over this change's `src/`, `swarm.exe` 26,112 bytes.
  The image is not byte-stable across assemblies - the PE `TimeDateStamp` at
  `0x88` and the `CheckSum` at `0xD8` move and nothing else does, measured by
  assembling twice and comparing - so the build is identified by its source
  rather than by a hash. `src/swarm.asm` gained one comment block in
  `capture_write`'s contract header after these runs were taken; assembling the
  source from before and after that edit produced identical images, byte for
  byte.
- Every run is 3600 consecutive frames from process start with **no warm-up
  discarded**, which is how the two live sections above are reduced. One
  8,192 run costs about a minute of wall clock and one 1M run about seven.
- **The host was not quiesced**, and the capture window sat on the interactive
  desktop rather than a private one. Whether occluding or minimising the window
  changes what `BitBlt` costs was not measured, so the blit figures are for a
  visible window and are not claimed for any other state.
- **The window is four QueryPerformanceCounter reads now rather than two.**
  The two added reads are inside the window, so each is counted in the phase
  that follows it and nothing is subtracted anywhere. The `frame` figures are
  therefore not strictly the same instrument as the rows recorded above them.

### What the split says at 8,192

**The blit has a number, and it is a quarter of the frame at this count.** Its
mean is 0.407 to 0.426 ms across the three runs against a frame mean of 1.666
to 1.698, so 24 to 25%. Every earlier reading in this document had to leave it
inside an unattributed remainder; #125's notes name it three times as the one
phase with no row anywhere.

**The raster is the smallest of the three**, 0.219 to 0.225 ms mean, below the
blit in every run and on every figure. That ordering is worth stating because
the two are easy to conflate: one walks `n` particles and the other moves a
fixed 4 MiB of framebuffer, and at 8,192 particles the fixed cost is the larger
of the two.

**`step` is about 62% of the frame** and is the only phase that scales with
`n`, which is what makes the other two a floor the frame cannot go below at
this framebuffer size. The three phases are consecutive deltas, so they sum to
the `frame` plane exactly, frame by frame; `CaptureReportTests` asserts that
identity over all 3600 samples rather than leaving it to be assumed.

**A phase percentile is not a share of the frame percentile.** The columns are
four independent reductions of four distributions, so the `p99` row of a phase
is that phase's own tail and the phase p99s do not add up to the frame p99.
Only the per-frame samples in the dump add up, and they add up exactly.

### What the split says at 1M on the committed scene

**The blit is 0.4% of the frame and the question it was blocking is answered.**
Its mean is 0.481 to 0.516 ms against a frame mean of 111.134 to 117.738, and
its worst p99 across three runs is 0.938 ms against a frame p99 of 228.112.
Three notes on #125 leave a remainder unattributed with the sentence that what
has no row anywhere is the `BitBlt`. It has one now, and it is not where any of
the distance sits.

**The blit barely moves with the particle count.** 0.407 to 0.426 ms mean at
8,192 and 0.481 to 0.516 ms at 1M, for the same 1024x1024 framebuffer. The
~20% rise is not attributed here - it is a different scene, a different frame
rate and a different host moment, and nothing in these runs separates those.

**The raster is about 4% and the step is about 96%.** The plot's mean is 4.246
to 4.697 ms, which is above every figure the plot-phase section recorded. Its
`ordered` rows at this geometry - build then pass, which is what every live
frame after the first is - read 2.337 to 2.410 ms, its whole `g = 512` block
spans 2.337 to 3.869, and its largest figure at any geometry or state is
4.054. The two are not the same measurement - minima over nine rounds on a
frozen harness bank against means over 3600 frames of an evolved committed
scene - and the difference is not decomposed here. What it does say is that no
reading of that section bounds the live raster from above, which that section
half predicted about itself: it measured three OUT states, found none below
60.8% lit, and said a genuinely clustered scene was not among them. This one
is, and the settle-depth section reads its lit share down to 12.6%. Everything
left over is `step`, which this instrument cannot divide into build and pass.

**So decision 11's four phases were three-quarters measured live when these
runs were taken.** `plot` and `blit` had their own distributions from the
shipped exe; `build` and `pass` were one `pool_step` call and were split only
at the P/Invoke seam, which is what the sections above them do.

**THE INSTRUMENT REACHES THE FOURTH PHASE NOW, AND NO ROW HAS BEEN TAKEN WITH
IT.** The frame is read at five points and the dump carries `build` and `pass`
as separate planes, so the sentence above about a remainder this instrument
cannot divide has stopped being true of the executable and stays true of the
rows. What is missing is a run, and the reason is the one the ported-cores
section measures at the end of this document: a foreign process held 419% of
one core across every run taken tonight, and the instrument was left unused
rather than pointed at a host in that state. `CaptureReportTests` runs the full
3,600-frame capture on every `dotnet test`, asserts the four phases sum to the
frame exactly over all of them, and requires the `build` and `pass` planes to
carry work in more than half the frames - so the split is exercised and
refusable without a published row standing behind it.

### These three runs do not reproduce the rows recorded at `33047f3`

The 1M live section above records a worst p99 of 150.849 ms on this preset,
captured on 2026-08-08. These three runs read 224.029, 228.112 and 212.169. A
note on #125 dated 2026-08-29 saw 247.173 in a single run and declined to call
it a drift measurement, on the grounds that one run against three, on an
unquiesced host and at a different commit, is not that.

Three runs are the re-reading that note asked for, and the direction is worth
recording plainly: **the gap is larger than the spread inside either set**, and
it points the wrong way for the obvious explanation. The parallel grid build
(#243) landed the day after `33047f3`, so this binary's build phase is the
faster one and the frame should have fallen rather than risen.

What these runs do not do is attribute that. The host was not quiesced in
either set, the two sets are months and many commits apart, and no bisection
was run. It is recorded here as a disagreement between two readings of the same
committed file, not as a regression measurement.

## The AVX2 force inner loop (cycles/candidate; #59)

The premise the masterplan force-cost analysis (decision 3 / open-risk-1) and
the gated `force_path = 4` rsqrt design (#38) both rest on: what does one
candidate pair cost in the AVX2 force group, and is that group
**throughput-bound** (divide unit saturated) or **latency-bound** on its
`vsqrtps`/`vdivps` chain? The bench answers it from the same brute AVX2 pass the
baseline table times - there is no separate kernel entry to isolate the group,
so the isolation is arithmetic: at a large `n` the O(n²) inner loop is all of
the pass bar the once-per-i integrate tail (1/n of the work), so
`ms/pass ÷ n²` is the per-candidate inner-loop cost to within ~0.01%. The group
processes 8 candidate lanes and runs exactly **one `vsqrtps` + one `vdivps`**,
so cost/group = 8 × cost/candidate.

| CPU           | n     | ns/candidate | M pairs/s | cyc/candidate | cyc/group |
| ------------- | ----- | ------------ | --------- | ------------- | --------- |
| Ryzen 9 5950X | 1024  | 0.875        | 1142.9    | 4.29          | 34.3      |
| Ryzen 9 5950X | 16384 | 0.798        | 1252.5    | 3.91          | 31.3      |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11, single-threaded.
- **Feature path**: AVX2 + FMA (no AVX-512), `force_path = 1`, brute (no grid).
- **Seed / preset**: `0x5EED`, 6 species, `rmax = 0.05`, varied attraction
  matrix; initial (uniform-random) frame - the AVX2 path has no early exit, so
  it evaluates the full force formula on every candidate regardless of preset,
  and cost/candidate does not depend on the in-range fraction.
- **Cycles**: `ns/candidate` is the clock-free measured primitive; cycles are
  derived at `RefGhz = 4.9` (this part's single-core sustained-AVX2 boost) - a
  recorded per-machine constant, like every number here. The verdict below is
  robust across the plausible boost-clock range: the measured ~28–31 cyc/group
  (at 4.4–4.9 GHz) stays far above the ~3–4 cyc carried-chain floor at any
  single-core AVX2 clock.
- **Commit**: kernel under test `c4a73a0` (the force loop `step.inc` is
  unchanged since the baseline above; the bench harness lands with this row) ·
  **Date**: 2026-07-17.

### Reading the numbers - throughput-bound, ~2.8× the budget line

**The loop is throughput-bound, not latency-bound.** The verdict rests on the
loop's dependency structure, not on the n-sweep. Across force groups the _only_
loop-carried dependency is the `fx`/`fy` accumulator add (`step.inc`, the
`vaddps ymm6`/`ymm7`) - a ~3–4 cycle chain; the `vsqrtps → … → vdivps` work is
recomputed each group from independent neighbour loads and does **not** carry
between groups. A loop bound by its carried chain would cost ~3–4 cyc/group; the
measured **~31 cyc/group** is ~8× that floor, so the binding constraint is
execution-unit **throughput**, not the dependency chain - consistent with the
loop tracking the sustained ~1.25 G/s the baseline table shows.

**The n-sweep is _not_ the discriminator** (the earlier draft that leaned on it
was wrong). A flat cost/candidate as n grows is the **same** signature for a
throughput-bound and a latency-bound loop - extra iterations of a carried chain
add no exploitable ILP either, and the hardware's in-flight window is bounded by
the reorder buffer, not by `n`. All the n-sweep bounds is the per-i amortization
term: cost/candidate moves only ~9.6% (0.875 → 0.798 ns) from n = 1024 to
n = 16384. That residual is **not** the once-per-i integrate tail (which is
~0.1% of the pass at n = 1024, far too small to shift per-candidate cost by
~10%); the likelier cause is per-i pipeline serialization at the integrate
barrier plus, on this pre-#33 kernel, the SSE-encoded tail's VEX↔SSE transition -
since removed by #33 (which VEX-encodes the tail; see the baseline section
above), so on the current kernel this residual is expected to largely close -
the exact split is not isolated here. The representative n = 16384 row (tail
~0.006%) is unaffected either way. A true
empirical latency-vs-throughput discriminator - e.g. a split-accumulator kernel
variant - would need a kernel edit, out of scope for this kernel-read-only
bench; the verdict rests on the 31-vs-3–4 cyc argument.

**Cost is ~3.9 cycles/candidate ≈ 31 cycles/group** - about **2.8× the
masterplan budget line** of ~1.3–1.4 cyc/candidate (~12 cyc/group). That budget
assumed a divider-throughput-limited group; the measurement says the real group
is roughly twice that. This is already implicit in the baseline throughput
(~1.25 G/s) the brute projections use - it is stated here in cycle terms so the
1M budget projection can be re-based on it.

**What it says for the software-pipelining lever (#61 / open-risk-1).**
Open-risk-1's rule is "if measured > 14 cyc/candidate, the fallback is
software-pipelining two j-groups." Measured **3.9 ≪ 14**, so the threshold is
not tripped. More fundamentally, software-pipelining two j-groups is a
_latency_-hiding transform, and this loop is throughput-bound, not latency-bound
(the carried chain is ~3–4 cyc/group against a ~31 cyc/group cost). Interleaving
two groups by hand cannot lift a throughput ceiling the units already set - it
would only add
register pressure for ~0 gain. The IEEE-exact pipelining attempt should expect
no throughput win here; its value, if any, is in relieving a bottleneck the
measurement does not show.

**What it says for the rsqrt premise (#38).** The `force_path = 4` case leans on
the divide unit being **>~90%** of the loop. This measurement does **not**
support that. The measured ~31 cyc/group is ~2× the published Zen 3 `vsqrtps` +
`vdivps` ymm divide-pipe reciprocal throughput (~11–15 cyc/group), so the divide
unit is **roughly half** the loop, not >90% - the ~33 non-divide FP ops
(`vsubps`/`vmulps`/`vroundps`/`vblendvps`/`vpermps` …) co-limit throughput on the
FP-ALU ports. Since rsqrt + Newton–Raphson removes divide-pipe pressure but adds
~4–5 ops onto those already-busy ALU ports, the #38 force-group estimate
(1.3–1.8×) is likely **optimistic** at the top of its range. Caveat: this
divide-vs-ALU split rests on published Zen 3 throughput tables, not a
per-execution-port measurement. Cleanly isolating the divide fraction would need
hardware perf counters per port, or a kernel-edit differential (assemble a
variant with the sqrt/div stubbed and re-time) - both out of scope for an
in-process, kernel-read-only microbench. So the **>90% divider premise is
unconfirmed and points optimistic**; treat the rsqrt speedup as unproven until
#61 (whose IEEE-exact result exposes the non-divide headroom directly) or a
port-level probe reports.

## The grid dimension at 1M across the rmax ceiling (#148)

Whether the `g <= 512` ceiling in `src/kernel/layout.inc` costs the headline
count anything, and if it does, which dimension the ceiling should be. The two
halves of the frame move against each other. The force pass gets cheaper as
cells get finer, because fewer particles fall inside the 3x3 neighbourhood it
walks. The build gets dearer, because it zeroes and prefixes `g*g + 1` cell
ends every frame: 1 MB and 262k entries at `g = 512`, 4 MB and 1M at 1024,
16 MB and 4M at 2048. Only the total says which wins, so the total is what is
timed here.

`g` is not an input. It follows from `rmax` by the layout rule, the largest
power of two with `1/g >= rmax`, and then meets the ceiling, so a point above
512 needs a different kernel rather than a different argument. Three builds
were assembled, differing in one instruction operand, the `cmp edx, 512` in
`arena_dims_core`, set to 512, 1024 and 2048. Nothing was merged with a raised
ceiling and the tree carries 512: these are local builds, assembled, measured
and reverted, the same way the `rmax = 0.08` rows in the M1 section were taken.
The dimension each row reports is read out of the arena header (`AH_G`) after
`swarm_init`, never recomputed by the harness, so a row cannot disagree with
the build that produced it.

```powershell
& "C:\Program Files\dotnet\dotnet.exe" tests\Swarm.Bench\bin\Release\net9.0\Swarm.Bench.dll --gsweep
```

### The sweep

| rmax   | ceiling | g    | cand/pt | build ms | pass ms | frame ms | worst frame ms | fps  |
| ------ | ------- | ---- | ------- | -------- | ------- | -------- | -------------- | ---- |
| 0.0020 | 512     | 256  | 145.00  | 6.389    | 134.497 | 140.885  | 162.576        | 7.1  |
| 0.0020 | 1024    | 256  | 145.00  | 6.504    | 133.331 | 139.836  | 143.792        | 7.2  |
| 0.0020 | 2048    | 256  | 145.00  | 6.269    | 134.565 | 140.834  | 144.375        | 7.1  |
| 0.0010 | 512     | 512  | 37.01   | 6.183    | 54.077  | 60.261   | 64.673         | 16.6 |
| 0.0010 | 1024    | 512  | 37.01   | 5.909    | 56.824  | 62.733   | 69.951         | 15.9 |
| 0.0010 | 2048    | 512  | 37.01   | 5.899    | 55.346  | 61.245   | 69.196         | 16.3 |
| 0.0007 | 512     | 512  | 37.01   | 5.827    | 54.046  | 59.873   | 61.610         | 16.7 |
| 0.0007 | 1024    | 1024 | 10.01   | 6.839    | 39.327  | 46.166   | 48.772         | 21.7 |
| 0.0007 | 2048    | 1024 | 10.01   | 7.340    | 39.526  | 46.866   | 50.060         | 21.3 |
| 0.0004 | 512     | 512  | 37.01   | 6.936    | 53.582  | 60.518   | 63.649         | 16.5 |
| 0.0004 | 1024    | 1024 | 10.01   | 7.359    | 39.426  | 46.785   | 49.061         | 21.4 |
| 0.0004 | 2048    | 2048 | 3.25    | 10.922   | 38.138  | 49.060   | 50.417         | 20.4 |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise build
  10.0.26200. **Feature path**: `swarm_cpu_paths` reports `0x1`, so AVX2 and no
  AVX-512, and `force_path = 1` selects the AVX2 path explicitly.
- **Scene**: `n = 1,048,576`, `FLAG_GRID`, 6 species, seed `0x5EED`,
  `beta = 0.3`, `dt = 0.02`, `friction = 0.71`, `force_scale = 10`, the
  harness's `MakeGridParams` matrix. Positions are the **initial
  uniform-random frame**, as in the M2 and M3 tables above.
- **Commit**: `481f986`, plus the one-operand ceiling change for the 1024 and
  2048 rows. **Date**: 2026-08-07.
- **build** and **pass** are each min-of-rounds over frozen input, as everywhere
  else here. **frame** is their sum from the fastest of **three repeats** of
  the whole sweep; **worst frame ms** is the slowest of the same three, so the
  run-to-run spread is in the table rather than hidden by the minimum.
- **cand/pt** is the mean number of particles in the 3x3 wrapped neighbourhood
  a particle's force loop walks, counted from the copied-out positions with the
  kernel's own cell rule and its own wrap. On the uniform frame every row lands
  within 0.01 of `9n/g² + 1`, which is what a correct count of a uniform field
  should be and is the check that the column means what it says.
- **The host was not quiesced.** Other work was running during the sweep, which
  is what the worst-frame column is for.

### Reading the sweep

**The ceiling does throttle the headline count, by about 23%.** At
`rmax = 0.0007` the frame goes from 59.873 ms at `g = 512` to 46.166 ms at
1024, and at `rmax = 0.0004` from 60.518 ms to 46.785 ms. Both gaps are
13.7 ms, more than four times the worst-to-best spread of either row they span,
so neither is the host.
Below `1/1024` the shipped ceiling is leaving roughly a quarter of the frame on
the floor.

**It binds strictly below `1/1024`, not at it.** At `rmax = 0.001` all three
builds resolve to `g = 512` and report the same frame within 4%, because the
layout rule only doubles while the **next** dimension's edge still covers
`rmax`, and `1/1024 = 0.000977` does not cover `0.001`. So `rmax = 0.001` is
not a capped point, and the same holds for the `rmax = 0.002` row at `g = 256`.
Those two rmax values are the control in this sweep rather than the subject:
three builds that differ only in a ceiling none of them reaches agree to within
0.8% at `g = 256` and 4% at `g = 512`, which is what says the ceiling operand
changes nothing except which dimensions are reachable.

**The minimising dimension at 1M is `g = 1024`, and 2048 is past the
crossover.** At `rmax = 0.0004`, 1024 gives 46.785 ms and 2048 gives 49.060 ms.
That 2.3 ms difference on the totals is the size of the spread within those
rows, so the totals alone would not settle it. The decomposition does, and each
half is well outside the noise: going from 1024 to 2048 the build rises from
6.7-7.4 ms to 10.9-11.5 ms across the repeats, a little over 4 ms, while the
pass falls only from 39.4 ms to 38.1 ms, about 1.3 ms. The `O(g²)` term is
buying less than a third of what it costs by that point, and the crossover
therefore sits between 1024 and 2048 rather than being asserted from the shape
of the curve.

**Why the pass stops paying.** `cand/pt` falls 145 to 37 to 10 to 3.25 as the
dimension doubles, a factor of about 3.6 each time, and the pass falls 134 to
54 to 39 to 38 ms. The first doubling converts almost all of it; the last
converts almost none. At `g = 2048` there are 3.25 candidates in a
neighbourhood, so the per-particle cost of enumerating the nine cells and their
runs is most of what is left. The `ns/cand` the harness derives from the pass
time says the same thing, rising from roughly 0.9 at `g = 256` to 1.4, 3.8 and
11.2 as the dimension doubles. There is no further pass saving to buy at this
count, at any dimension.

**What this does not settle.** The pass here is **serial**, which is what the
method asked for and what keeps the sweep off all sixteen cores, but the 1M
headline runs the pass threaded and the build serial. Threading shrinks the
only term that a finer grid improves and leaves untouched the term it makes
worse, so the balance moves toward the coarser dimension, and by how much is
not measured here. Concretely, an 11 ms serial build at `g = 2048` is already
two thirds of a 60 fps frame on its own, against about 7 ms at 1024. Nothing in
this sweep says where the optimum sits once the pass is threaded, and the
figure above should not be quoted as if it did. One count, one seed, one
uniform-random frame, one machine.

## The managed baseline of the competitor comparison (#153)

The comparison #153 asks for is three engines beside this one: a C++ port, a
Java port, and a naive idiomatic C# port as the managed baseline. This section
is the managed baseline only. It is the third that needs no foreign toolchain
and no change to the machine every number in this document is taken on, so it
is the third that could be taken here; the other two are not measured, no
comparison table is published, and the README is unchanged.

The port is `tests/Swarm.Bench/ManagedBaseline.cs`. It computes the same
force + integrate pass over the same seeded population, which is asserted
rather than claimed: `ManagedBaselineParityTests` holds it to `TestOracle`, the
reference the kernel itself is checked against, and the measured divergence is
zero at every scene the suite runs. A baseline made faster by computing less
reds that test.

Two layouts are implemented and both are timed, one `float[]` per field and one
struct per particle, because the fairness argument is what a reader is entitled
to challenge and a shape chosen by preference is not an argument. The faster of
the two is the one the comparison quotes. Neither uses `Vector<T>` or the
intrinsics: a port that did would compare hand-written AVX2 against JIT-emitted
AVX2 rather than against managed code.

### How to run

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run -c Release --project tests\Swarm.Bench\Swarm.Bench.csproj -- --managed
```

Every column comes out of one process on one host, which is the reason the port
lives inside this harness rather than in a project of its own: a managed figure
taken in one run and a kernel figure carried in from another would be two
machine states presented as a comparison. The instrument is the one the
`## Baseline` rows use, min-of-nine over a pass against frozen input. The
managed side is warmed with three passes first, because tiered compilation
would otherwise time the loop before it was promoted; the quoted figure is a
minimum over nine rounds, so a round that ran before promotion cannot be the
one reported.

### Three runs, all fifteen cells

| n     | run | C# SoA ms | C# AoS ms | scalar ms | AVX2 ms | AVX2 / C# |
| ----- | --- | --------- | --------- | --------- | ------- | --------- |
| 1024  | 1   | 1.635     | 1.787     | 1.809     | 0.834   | 1.96×     |
| 1024  | 2   | 1.472     | 1.559     | 1.744     | 0.859   | 1.71×     |
| 1024  | 3   | 1.656     | 1.576     | 1.622     | 0.857   | 1.93×     |
| 2048  | 1   | 6.091     | 6.530     | 7.925     | 3.307   | 1.84×     |
| 2048  | 2   | 6.174     | 6.266     | 6.833     | 3.074   | 2.01×     |
| 2048  | 3   | 6.157     | 6.164     | 6.315     | 3.313   | 1.86×     |
| 4096  | 1   | 24.608    | 31.035    | 30.301    | 13.641  | 1.80×     |
| 4096  | 2   | 25.260    | 26.755    | 26.268    | 13.344  | 1.89×     |
| 4096  | 3   | 24.189    | 24.572    | 25.251    | 13.008  | 1.86×     |
| 8192  | 1   | 95.778    | 119.488   | 102.764   | 53.368  | 1.79×     |
| 8192  | 2   | 95.903    | 97.454    | 106.144   | 53.230  | 1.80×     |
| 8192  | 3   | 94.652    | 99.176    | 100.856   | 52.095  | 1.82×     |
| 16384 | 1   | 400.485   | 480.757   | 520.762   | 209.882 | 1.91×     |
| 16384 | 2   | 398.736   | 405.348   | 443.539   | 214.992 | 1.85×     |
| 16384 | 3   | 388.546   | 391.146   | 483.564   | 235.237 | 1.65×     |

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  10.0.26200, single-threaded.
- **Feature path**: AVX2 + FMA (this CPU reports no AVX-512), .NET 10.0.9
  running a `net9.0` build, workstation GC, tiered PGO left at its default.
- **Seed / scene**: `0x5EED`, 6 species, `rmax = 0.05`, `beta = 0.3`,
  `dt = 0.02`, `friction = 0.71`, force scale 10, the varied attraction matrix
  the `## Baseline` rows use. Read out of `MakeParams` rather than restated, so
  the two tables cannot drift into different scenes.
- **Commit**: `b26a590` · **Date**: 2026-08-23.
- The three runs are consecutive invocations of the command above on a host
  that was not otherwise quiesced. Nothing was pinned, no priority was raised.

### Reading it - the vector path is the advantage, not the language

**The AVX2 kernel is 1.65× to 2.01× the managed baseline, median 1.85× over the
fifteen cells.** That is the number this section exists to produce, and it is
about an eighth of what an 8-wide kernel against scalar managed code "should"
be. The reasons are the ones `## Reading the numbers` already gives for the
AVX2-against-scalar figure and they apply here unchanged, because the managed
baseline rejects an out-of-range candidate before the force math exactly as the
scalar kernel path does, while the AVX2 path masks instead of branching. At
`rmax = 0.05` under 1% of candidate pairs are in range, so the early-out is
most of the loop, and this is a comparison of two strategies rather than of two
instruction sets. It is stated here rather than normalised away.

**Plain C# lands within a few percent of the hand-written scalar path at every
count.** At 8192 the managed SoA pass is 94.652 ms against the scalar kernel's
100.856 ms in the same run; the managed candidate-pair rate is 633 M to 712 M
per second across the fifteen cells against the 673 M to 685 M the
`## Baseline` rows record for the scalar path. The engine's margin over managed
code is the vector path and nothing else, which is what makes the AVX2 row the
one worth quoting and the scalar row a reference rather than a product.

**The two managed layouts are close, and the structure-of-arrays one is quoted
because it is never the slower.** In runs 2 and 3 they are within a few percent
at every count; run 1's array-of-structs column at 8192 and 16384 is the widest
gap in the table and is not reproduced by either later run, so the layout
choice is not what decides the comparison. Quoting the faster of the two is
what makes the baseline a floor on plain managed performance instead of a
strawman, and it costs the engine's ratio nothing worth arguing about.

**The kernel columns in these runs read above this document's own recorded
rows at n = 16384, and that is disclosed rather than smoothed.** The
`## Baseline` section records 391.980 ms scalar and 209.493 ms AVX2 at that
count. Here the scalar column reads 13% to 33% above it and the AVX2 column
reads between 0.2% and 12% above it, while at every smaller count both are
within a few percent. The managed passes run first at each count, so at 16384
the kernel columns are taken after roughly twenty seconds of sustained
single-core float work; that is a plausible cause and it is not established.
What matters for the claim is the direction. A kernel measured slower than its
own baseline makes the quoted ratio a conservative one for this engine, so the
1.85× is a floor on the margin rather than a flattering reading of it.

## The ported competitor cores (#153)

The other two engines of the comparison exist here now, as **ported cores**:
each engine's force law, damping and integration transcribed into C# in
`tests/Swarm.Bench/CompetitorCores.cs`, run in this harness beside the kernel
and the managed baseline, over one drawn population, in one process, on one
host, timed by the instrument the `## Baseline` rows use.

**No comparison table is published from them yet**, and the reason is measured
rather than asserted - it is at the end of this section.

### What a ported core is, and what it is not

It is not a measurement of anyone's program. A figure taken from an executable
this repository did not build is not reproducible from this repository alone,
which is the rule every row in this document is held to. Those are two
different claims and this document keeps them in different sentences: a ported
core is source in this tree that anybody can re-run, and an external
observation of a competitor's own build would be dated, quoted with its version
and build environment, and marked as something a reader can weigh but not
reproduce from here. Nothing below is the second kind.

**Neither engine's acceleration structure is ported.** Both cores walk every
ordered pair, which is what the managed baseline and the `## Baseline` kernel
rows already do. A table in which one side enumerates fewer pairs measures the
acceleration structure rather than the core, and this engine's own structure
has its own sections above. So every column walks n^2 pair slots and the only
thing that differs between them is the arithmetic each engine specifies for one
pair and for one particle.

### tom-mohr/particle-life-app, at `3ba0c4e`

**It expresses the rule set this repository pins.** Its accelerator
(`src/main/java/com/particle_life/app/Main.java:275-279`) is the same knee, the
same tent and the same matrix entry as `docs/MASTERPLAN.md`; the term
`|1 + beta - 2*xn|` is `|2*xn - 1 - beta|` term for term. That is checked
rather than claimed: `CompetitorCoreTests.TheJavaAcceleratorIsThePinnedForceCurve`
walks both branches of the curve at three values of beta.

The deviations, each marked at the line in the port that makes it:

1. **An extra factor of rmax on the acceleration.** `Physics.java:437` applies
   `rmax * force * dt`, and `Accelerator.java` documents why - the
   accelerator's result "is also interpreted as relative to rmax".
2. **Friction renormalised to 60 fps**, `pow(friction, 60 * dt)` at
   `Physics.java:401`. At the benchmark scene that is `0.71^1.2`, not `0.71`.
3. **No velocity clamp.** This engine bounds velocity at `rmax / dt` so a
   particle cannot cross a cell in one step; `Physics.java` has no such bound.
4. **Double precision throughout**, because `Physics` carries `Vector3d`.

**Those four are the whole list, and that is asserted rather than asserted at.**
All four are removable from the scene rather than from the port - deviation 1
by handing the port `forceScale / rmax`, deviation 2 by a timestep of `1/60`,
deviation 3 by a scene that never reaches the bound - and with them removed
`CompetitorCoreTests.JavaCoreIsThisEnginesLawOnceTheFourNamedDeviationsAreRemoved`
requires the port to land on `TestOracle`, the reference the kernel itself is
checked against, at three scenes chosen to put a different share of the
population on each side of the knee. A port carrying an unnamed fifth
difference cannot pass that.

Two smaller differences are in the port's comments rather than on this list,
because neither changes a trajectory: the neighbour test is `<=` rmax squared
where this engine's is `<`, and a pair at exactly rmax carries zero force under
both laws; and the shortest-connection wrap differs from `dx -= round(dx)` only
exactly at +/-0.5, where round-half-even sends 0.5 to 0.

The container grid (`Physics.java:309-395`) and the twelve-thread fan-out
(`Physics.java:37`) are not ported, for the reason given above.

### hunar4321/particle-life, at `2562787`

**It does not express the rule set**, and that is the finding rather than an
obstacle to be normalised away. Read against `docs/MASTERPLAN.md`, at
`particle_life/src/ofApp.cpp`:

- The force is `1/r` inside a radius and zero outside it (`:59`). No repulsion
  knee, no matrix-weighted tent; the matrix entry enters once, as the scalar
  coefficient `G / -100` (`:39`).
- The world is bounded by a **clamp**, not a wrap (`:84-90`), and the offset
  between two particles is therefore the plain difference with no wrap at all.
- There is **no dt**: velocity is added straight to position (`:79-80`).
- Each particle is integrated **once per partner group**, not once per frame
  against a frozen state. `ofApp::update` calls `interaction` once per ordered
  species pair (`:474-489`) and each call ends by moving its first group, so a
  particle has already moved before the next group is summed against it.

The last of those is the sharpest one and it is the property the port exists to
carry, so it is pinned by arithmetic rather than by prose:
`CompetitorCoreTests.TheCppCoreIntegratesPerPartnerGroup` asserts that one
update's displacement is the SUM of the intermediate velocities and not the
final velocity, which is what a frozen-state update would produce. Ported as
the source has it, the worst gap at its fixture is 1.03E-05; with the
integration lifted out of the group loop it falls to 2.98E-08, the rounding of
one f32 add at a coordinate of order 0.5.

Three further departures are made by this repository's scene rather than by the
engine, and each is a comment at the line that makes it: the wall repel is left
out, because its default of 10.0 belongs to a 1600 x 900 pixel world
(`ofApp.h:215-222`) and would cover the whole of a unit square; `viscosity` is
taken as `1 - friction`, because this scene declares a friction coefficient
where the source declares a viscosity; and the coefficient is the scene's
matrix entry put through the source's `G / -100` unchanged, where the
application supplies a slider in [-100, 100].

### How to run

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run -c Release --project tests\Swarm.Bench\Swarm.Bench.csproj -- --cores
```

Both ported cores advance their state in place, so each timed call restores the
drawn population first. That restore is inside the timed window rather than
hidden outside it, and the report times it on its own as a column so its size
is a measurement instead of an assurance: it comes out at 0.000 to 0.005 ms
against passes of 0.9 to 2143 ms.

### Why there is no table here yet

**The host is not fit to take one tonight, and that is measured.** Two
consecutive runs of the command above disagree with each other by more than the
differences a comparison would be about:

| n     | scalar kernel ms, run 1 | run 2   | cpp core ms, run 1 | run 2   |
| ----- | ----------------------- | ------- | ------------------ | ------- |
| 2048  | 21.162                  | 8.874   | 8.651              | 7.456   |
| 4096  | 32.109                  | 37.975  | 31.617             | 39.203  |
| 8192  | 119.145                 | 114.593 | 117.053            | 145.873 |
| 16384 | 569.866                 | 595.371 | 575.028            | 632.556 |

The scalar column moves by a factor of 2.4 between two runs of one binary at
n = 2048, and reads 45% above the 391.980 ms this document records for the same
row at n = 16384. A single foreign process accounts for it, and it was measured
across each run rather than inferred:

```
LOAD eu5 dCPU=409.05s over window 97.59s = 419.1% of one core = 13.1% of 32 logical
LOAD eu5 dCPU=393.17s over window 93.37s = 421.1% of one core = 13.2% of 32 logical
```

- **Machine**: AMD Ryzen 9 5950X (Zen 3, 16C/32T), Windows 11 Enterprise
  10.0.26200, single-threaded. **Feature path**: AVX2 + FMA, no AVX-512
  (`cpu paths (bits) : 0x1`), .NET 10.0.11 running a `net9.0` build.
  **Scene / seed**: `MakeParams`, `0x5EED`, 6 species, `rmax = 0.05`,
  `beta = 0.3`, `dt = 0.02`, `friction = 0.71`, force scale 10.
  **Commit**: `2fbdc75` plus this change's own working tree, which is why the
  numbers above are quoted only against each other and never against a row
  elsewhere in this document. **Date**: 2026-08-30.

The AVX2 column is the one that survived - 210.646 and 227.331 ms at n = 16384
against the 209.493 recorded above - so this is not a claim that nothing on
this host is measurable tonight. It is the narrower statement that the scalar
column, which a cross-engine table is read against, is not.

So what lands here is the apparatus and not the row. The comparison is one
command away from a number and the command is above; what it needs is a host
carrying no second workload, and that is the only thing still between this
document and the table #153 asks for.

### What is still owed on #153

**The comparison table, and nothing else.** Both foreign cores are in the tree
and both run from one command; what is missing is a run on a host carrying no
second workload, for the reason the section above measures.

The obstacle this sub-section used to lead with is gone rather than solved,
and the difference is worth stating. It said both engines sat behind a
toolchain outside this repository - a Java runtime and build tool that are not
on this machine, an openFrameworks release and its `ofxGui` addon for the C++
one - and that the C++ engine could not express the pinned rules anyway. All
of that is still true of their PROGRAMS. None of it is in the way any more,
because what is compared here is each engine's core, ported, and a ported core
needs no foreign toolchain. The rule that shut the door on the shipped
binaries in that repository is unchanged and is why the door stays shut: a
figure from an executable this repository did not build is not reproducible
from this repository alone.

The question that stood open here - every engine measured as it stands with
its deviations published, or a rule set cut down to what all three can express

- is answered by the first of the two, and the answer is executed above rather
  than restated: each core is ported as its source has it, and every deviation is
  a comment at the line that makes it and a line on the row.
