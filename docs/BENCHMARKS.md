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
- **Date**: 2026-08-06. Output is bit-identical across the change: whole-arena
  hashes match for 216 configurations, including split passes, recorded on the
  pull request that closed #87.

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
- **Date**: 2026-08-06. Bit-exactness is not inferred from these numbers: the
  whole arena hashes identically across the change for 84 configurations, which
  is recorded on the pull request that closed #77.

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
live loop and records the QueryPerformanceCounter delta of the **work window**
of each frame - step plus plot plus blit, never the pacing wait - then writes
3600 raw `u64` samples to `swarm-frames.bin` and exits. The wait is outside the
window on purpose: a paced loop measured wall to wall reports 16.67 ms by
construction and would say nothing about how much room is left.

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
$ms = @(for ($i = 0; $i -lt $count; $i++) { [BitConverter]::ToUInt64($b, 40 + 8 * $i) * 1000.0 / $freq })
$s = $ms | Sort-Object
[string]::Format([Globalization.CultureInfo]::InvariantCulture,
  "mean={0:F3} ms  p50={1:F3} ms  p99={2:F3} ms  max={3:F3} ms",
  ($ms | Measure-Object -Average).Average,
  $s[[int][math]::Floor(0.50 * ($count - 1))],
  $s[[int][math]::Floor(0.99 * ($count - 1))],
  $s[$count - 1])
```

The dump is a 40-byte header - `'SWRMFRM1'`, then `qpc_freq`, `count`, `n`,
`flags`, `seed` - followed by `count` little-endian `u64` tick deltas. The
scene the samples belong to is inside the file, so a dump cannot be quoted
against a run it did not come from.

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
there. The budget is one frame at 60 fps, 16.67 ms, on p99.

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
  file cannot be quoted against a run it did not come from.
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
