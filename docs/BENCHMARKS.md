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
2. **The shared integrate/store tail is now VEX-encoded** inside the AVX2 pass
   (issue #33). It runs once per particle, after the VEX inner loop. In the
   **brute** pass the n-iteration force loop dominates, so VEX-encoding the tail
   is within run-to-run noise — the baseline table above is unchanged. In the
   **sparse grid** pass it is the reverse: cells hold ~1 neighbour, so the
   once-per-particle tail dominates the pass, and running it in legacy SSE with
   dirty ymm upper halves paid a per-instruction merge stall on every tail op.
   VEX-encoding the tail (bit-identical arithmetic, proven by the per-path
   goldens and a before/after bit-identity A/B) cut the grid pass ~2.6–2.9× —
   see the M2 table below.

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
| Ryzen 9 5950X | 50,000  | 1/256 | 256 | 0.286    | 2.203   | 2.489    | 401.8 | 1,977 ms   |
| Ryzen 9 5950X | 50,000  | 1/512 | 512 | 0.423    | 2.062   | 2.484    | 402.5 | 1,977 ms   |
| Ryzen 9 5950X | 500,000 | 1/256 | 256 | 3.127    | 44.176  | 47.303   | 21.1  | 197,664 ms |
| Ryzen 9 5950X | 500,000 | 1/512 | 512 | 3.160    | 23.822  | 26.982   | 37.1  | 197,664 ms |

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
- **Commit**: the M2 grid kernel (grid build #24 + neighbourhood force #30) with
  the VEX-encoded integrate tail (#33) · **Date**: 2026-07-17. The pre-#33 grid
  pass on this machine was 5.923 / 5.986 / 84.075 / 62.351 ms for the four rows;
  brute is unaffected (the tail is negligible there).

### Reading the M2 numbers

**50,000 particles hold 60 fps on one core with room to spare** — ~402 fps,
~6.7× headroom under the 16.67 ms budget — where the brute-force frame would be
~1,977 ms (0.5 fps). The grid is **~790× faster than brute at 50k** and turns an
un-runnable count into a trivial one. (Pre-#33 this was ~155 fps; VEX-encoding
the integrate tail nearly tripled the sparse-grid pass — see the note above.)

**500,000 particles are close to 60 fps on one core** — 27 ms/frame at the best
config (g = 512), ~37 fps — and the grid is **~7,300× faster than the ~198 s
brute frame**. The gap to 60 fps is now ~1.6× (pre-#33 it was ~4× at 65 ms/frame),
which the M3 worker-pool fan-out across cores closes: the neighbourhood pass is
already **split-invariant** (`GridPassSplitInvariance`,
`pass(0,n) == pass(0,k);pass(k,n)` bit-for-bit), so it parallelises without a
determinism change. The counting-sort **build is cheap** (0.3 ms at 50k, ~3 ms
at 500k) and never the bottleneck; the pass dominates, and a larger `g` (sparser
cells, smaller `k`) is the lever — g = 512 beats g = 256 at 500k (24 vs 44 ms)
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
  count; `T = 16` is the auto-detected physical-core count (SMT is not used — a
  divider-bound AVX2 loop gains nothing from a second sibling on the shared
  divide/sqrt port). **Feature path**: AVX2 + FMA, `FLAG_GRID`. **Seed /
  preset**: `0x5EED`, 6 species, `rmax = 1/512`, varied attraction matrix.
- **pass ms** = the threaded pass (`swarm_pass_mt`), min-of-rounds over the
  frozen sorted IN bank (identical work every round), so the scaling is clean.
  **frame** = serial build + threaded pass. **pass speedup** = serial pass ÷
  threaded pass; the `T = 1` row (0.98×) shows the pool wake/join overhead is
  negligible against a 60 ms pass.
- **serial build** here is **10.5 ms** — the counting sort timed on the
  **initial uniform-random** frame, whose ~1.9 particles across `g² = 262,144`
  cells scatter the stable backward pass across memory. That is the worst-case
  build; the settled post-warmup distribution the M2 table times is **~3.2 ms**
  (clustered cells, better locality). The frame column uses the worst-case
  build, so a steady-state frame at `T = 16` is nearer **~8 ms (~120 fps)**.
- **Commit**: the M3 worker pool (`src/platform/pool.inc`, #68) · **Date**:
  2026-07-18.

### Reading the M3 numbers — 500k @ 60 fps reached

**500,000 particles clear 60 fps on 16 cores.** At `T = 16` the threaded pass
is **4.98 ms** (a **12.6×** speedup over the 62.8 ms serial pass), and even with
the worst-case 10.5 ms uniform-frame build the frame is **15.5 ms — 64.6 fps**,
inside the 16.67 ms budget. Against the settled ~3.2 ms build the steady-state
frame is ~8 ms (~120 fps). Either way the ~4× gap the M2 baseline recorded is
closed by threading alone.

**Scaling is near-linear to 8 cores, then tapers.** 1.93× / 3.93× / 7.71× at
`T = 2 / 4 / 8` is essentially ideal — the 16-aligned partition keeps the seven
OUT arrays off shared cache lines, so there is no false-sharing collapse. The
9th–16th cores add 7.71× → 12.6× (a run-to-run 12–15× at the top of the sweep):
the second CCD reaches the working set across the inter-CCD fabric and the pass
shifts partly bandwidth-bound past 8 cores, exactly the risk decision 6 flagged.
It is a scaling taper, not a correctness effect — the state stays bit-identical.

**Determinism is independent of T.** The static split is bit-identical for any
`T` because each particle's output is a pure function of the frozen IN bank plus
`cell_start`; the per-thread MXCSR pin (each worker crosses the same seam the
exports do, pinning `0x9FC0` FTZ/DAZ before any FP op) is what makes that hold
across threads. `PassParallelMatchesSerial` gates it at `T = 1, 2, 4, 16` on
both the AVX2 and scalar paths, exact equality.

**1M is still the open target.** At 1M the cells are ~2× denser (`g` clamps at
512), `k` rises, and the pass grows super-linearly; there is no measured 1M row
yet. Threads alone may not reach 60 fps at 1M — decision 6 pairs M3 with the
AVX-512 path for that, and a measured 1M serial baseline is the next step.

## The AVX2 force inner loop (cycles/candidate; #59)

The premise the masterplan force-cost analysis (decision 3 / open-risk-1) and
the gated `force_path = 4` rsqrt design (#38) both rest on: what does one
candidate pair cost in the AVX2 force group, and is that group
**throughput-bound** (divide unit saturated) or **latency-bound** on its
`vsqrtps`/`vdivps` chain? The bench answers it from the same brute AVX2 pass the
baseline table times — there is no separate kernel entry to isolate the group,
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
  matrix; initial (uniform-random) frame — the AVX2 path has no early exit, so
  it evaluates the full force formula on every candidate regardless of preset,
  and cost/candidate does not depend on the in-range fraction.
- **Cycles**: `ns/candidate` is the clock-free measured primitive; cycles are
  derived at `RefGhz = 4.9` (this part's single-core sustained-AVX2 boost) — a
  recorded per-machine constant, like every number here. The verdict below is
  robust across the plausible boost-clock range: the measured ~28–31 cyc/group
  (at 4.4–4.9 GHz) stays far above the ~3–4 cyc carried-chain floor at any
  single-core AVX2 clock.
- **Commit**: kernel under test `c4a73a0` (the force loop `step.inc` is
  unchanged since the baseline above; the bench harness lands with this row) ·
  **Date**: 2026-07-17.

### Reading the numbers — throughput-bound, ~2.8× the budget line

**The loop is throughput-bound, not latency-bound.** The verdict rests on the
loop's dependency structure, not on the n-sweep. Across force groups the _only_
loop-carried dependency is the `fx`/`fy` accumulator add (`step.inc`, the
`vaddps ymm6`/`ymm7`) — a ~3–4 cycle chain; the `vsqrtps → … → vdivps` work is
recomputed each group from independent neighbour loads and does **not** carry
between groups. A loop bound by its carried chain would cost ~3–4 cyc/group; the
measured **~31 cyc/group** is ~8× that floor, so the binding constraint is
execution-unit **throughput**, not the dependency chain — consistent with the
loop tracking the sustained ~1.25 G/s the baseline table shows.

**The n-sweep is _not_ the discriminator** (the earlier draft that leaned on it
was wrong). A flat cost/candidate as n grows is the **same** signature for a
throughput-bound and a latency-bound loop — extra iterations of a carried chain
add no exploitable ILP either, and the hardware's in-flight window is bounded by
the reorder buffer, not by `n`. All the n-sweep bounds is the per-i amortization
term: cost/candidate moves only ~9.6% (0.875 → 0.798 ns) from n = 1024 to
n = 16384. That residual is **not** the once-per-i integrate tail (which is
~0.1% of the pass at n = 1024, far too small to shift per-candidate cost by
~10%); the likelier cause is per-i pipeline serialization at the integrate
barrier plus, on this pre-#33 kernel, the SSE-encoded tail's VEX↔SSE transition
— since removed by #33 (which VEX-encodes the tail; see the baseline section
above), so on the current kernel this residual is expected to largely close —
the exact split is not isolated here. The representative n = 16384 row (tail
~0.006%) is unaffected either way. A true
empirical latency-vs-throughput discriminator — e.g. a split-accumulator kernel
variant — would need a kernel edit, out of scope for this kernel-read-only
bench; the verdict rests on the 31-vs-3–4 cyc argument.

**Cost is ~3.9 cycles/candidate ≈ 31 cycles/group** — about **2.8× the
masterplan budget line** of ~1.3–1.4 cyc/candidate (~12 cyc/group). That budget
assumed a divider-throughput-limited group; the measurement says the real group
is roughly twice that. This is already implicit in the baseline throughput
(~1.25 G/s) the brute projections use — it is stated here in cycle terms so the
1M budget projection can be re-based on it.

**What it says for the software-pipelining lever (#61 / open-risk-1).**
Open-risk-1's rule is "if measured > 14 cyc/candidate, the fallback is
software-pipelining two j-groups." Measured **3.9 ≪ 14**, so the threshold is
not tripped. More fundamentally, software-pipelining two j-groups is a
_latency_-hiding transform, and this loop is throughput-bound, not latency-bound
(the carried chain is ~3–4 cyc/group against a ~31 cyc/group cost). Interleaving
two groups by hand cannot lift a throughput ceiling the units already set — it
would only add
register pressure for ~0 gain. The IEEE-exact pipelining attempt should expect
no throughput win here; its value, if any, is in relieving a bottleneck the
measurement does not show.

**What it says for the rsqrt premise (#38).** The `force_path = 4` case leans on
the divide unit being **>~90%** of the loop. This measurement does **not**
support that. The measured ~31 cyc/group is ~2× the published Zen 3 `vsqrtps` +
`vdivps` ymm divide-pipe reciprocal throughput (~11–15 cyc/group), so the divide
unit is **roughly half** the loop, not >90% — the ~33 non-divide FP ops
(`vsubps`/`vmulps`/`vroundps`/`vblendvps`/`vpermps` …) co-limit throughput on the
FP-ALU ports. Since rsqrt + Newton–Raphson removes divide-pipe pressure but adds
~4–5 ops onto those already-busy ALU ports, the #38 force-group estimate
(1.3–1.8×) is likely **optimistic** at the top of its range. Caveat: this
divide-vs-ALU split rests on published Zen 3 throughput tables, not a
per-execution-port measurement. Cleanly isolating the divide fraction would need
hardware perf counters per port, or a kernel-edit differential (assemble a
variant with the sqrt/div stubbed and re-time) — both out of scope for an
in-process, kernel-read-only microbench. So the **>90% divider premise is
unconfirmed and points optimistic**; treat the rsqrt speedup as unproven until
#61 (whose IEEE-exact result exposes the non-divide headroom directly) or a
port-level probe reports.
