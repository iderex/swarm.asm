using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

// A repo artifact is English: format every number with '.' as the decimal
// point regardless of the dev machine's locale, so the table is reproducible.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// swarm.asm - force-kernel micro-benchmark (see docs/BENCHMARKS.md).
//
// Measures one force+integrate pass (swarm_pass over the whole population) for
// the scalar reference path and the AVX2 gather path, across a range of
// particle counts. swarm_pass is the O(n^2) hot loop the SIMD path
// accelerates; timing it in isolation - build once, then repeat the pass over
// the frozen IN bank - keeps the measured work identical every iteration and
// free of the bank-swap/copy cost that a full swarm_step would fold in.
//
// The report is the AVX2-vs-scalar speedup and the interaction throughput; the
// numbers are per-machine (never compared across hardware) and are recorded
// with their methodology in docs/BENCHMARKS.md.

string dll = EnsureBuilt();
nint handle = NativeLibrary.Load(dll);
NativeLibrary.SetDllImportResolver(
    Assembly.GetExecutingAssembly(),
    (name, _, _) => name == "swarm.kernel.dll" ? handle : nint.Zero);

int paths = Native.swarm_cpu_paths();
bool haveAvx2 = (paths & 1) != 0; // CPU_AVX2   = bit 0 (abi.inc)
bool haveAvx512 = (paths & 2) != 0; // CPU_AVX512 = bit 1

Console.WriteLine("swarm.asm force-kernel micro-benchmark");
Console.WriteLine($"  logical processors : {Environment.ProcessorCount}");
Console.WriteLine($"  cpu paths (bits)   : 0x{paths:X}  (AVX2={haveAvx2}, AVX-512={haveAvx512})");
Console.WriteLine($"  build              : {dll}");
Console.WriteLine();

// The grid-dimension sweep (#148) is behind an argument and returns instead of
// falling through, for two reasons. It answers one question that none of the
// sections below ask, and it is the only measurement here that has to be
// repeated against more than one kernel build - the cell-dimension ceiling is
// a constant in src/kernel/layout.inc, so a point above the shipped 512 comes
// from a locally raised build rather than from a switch. Running the default
// report first would put minutes of unrelated work in front of every repeat.
if (args.Contains("--gsweep"))
{
    GridSweep();
    return 0;
}

// The build's n-independent half (#243) is behind an argument for the first of
// those two reasons: it asks a question none of the sections below ask, and it
// wants a quiesced host more than any of them, because the quantity it is after
// is a tenth of a millisecond sitting inside a seven-millisecond figure.
if (args.Contains("--buildsplit"))
{
    BuildSplit();
    return 0;
}

int[] ns = [1024, 2048, 4096, 8192, 16384];
const uint Scalar = 3, Avx2 = 1;

Console.WriteLine(
    $"{"n",9} {"scalar ms",12} {"avx2 ms",12} {"speedup",9} {"scalar Mp/s",13} {"avx2 Mp/s",12}");
Console.WriteLine(new string('-', 72));
foreach (int n in ns)
{
    double scalarMs = TimePass((uint)n, Scalar);
    double avx2Ms = haveAvx2 ? TimePass((uint)n, Avx2) : double.NaN;

    double pairs = (double)n * n; // brute-force interaction count per pass
    double sMp = pairs / (scalarMs * 1e3); // millions of pairs / second
    double aMp = pairs / (avx2Ms * 1e3);

    string avxCol = haveAvx2 ? avx2Ms.ToString("0.000") : "n/a";
    string speed = haveAvx2 ? $"{scalarMs / avx2Ms:0.00}x" : "n/a";
    string aMpCol = haveAvx2 ? aMp.ToString("0.0") : "n/a";
    Console.WriteLine(
        $"{n,9} {scalarMs,12:0.000} {avxCol,12} {speed,9} {sMp,13:0.0} {aMpCol,12}");
}
Console.WriteLine();
Console.WriteLine("ms = best (min) per-pass time over 9 rounds; per-machine - record in docs/BENCHMARKS.md.");

// --- M2 grid: build (counting sort) + 3x3 neighbourhood pass at scale -------
// The grid replaces the O(n^2) sweep with O(n*k). g = the largest power of two
// with 1/g >= rmax (clamped [4,512]); a small rmax gives a large g, so cells
// are sparse and k (neighbours per particle) is small - that is the regime the
// grid wins in. We time the AVX2 grid frame = build (once, OUT frozen) + the
// neighbourhood pass (build once, then repeat over the frozen sorted IN), so
// the work is identical every round. The brute-force frame at these counts is
// O(n^2) and impractical to run, so it is PROJECTED from the measured AVX2
// interaction throughput (the table above) and clearly labelled as such.
Console.WriteLine();
Console.WriteLine("M2 grid (AVX2, FLAG_GRID): build + 3x3 neighbourhood pass");
double avx2ThroughputMpS = haveAvx2 ? (16384.0 * 16384.0) / (TimePass(16384, Avx2) * 1e3) : 0;
Console.WriteLine(
    $"{"n",9} {"rmax",8} {"g",5} {"build ms",10} {"pass ms",10} {"frame ms",10} {"fps",8} {"brute proj",12}");
Console.WriteLine(new string('-', 80));
foreach (int n in new[] { 50_000, 500_000 })
{
    foreach (float rmax in new[] { 1f / 256f, 1f / 512f })
    {
        var (buildMs, passMs) = TimeGrid((uint)n, rmax);
        double frameMs = buildMs + passMs;
        double fps = 1000.0 / frameMs;
        // Projected single-core brute frame: n^2 candidate pairs / measured Mp/s.
        double bruteProjMs = haveAvx2 ? ((double)n * n) / (avx2ThroughputMpS * 1e3) : double.NaN;
        int g = GridDim(rmax);
        Console.WriteLine(
            $"{n,9} {rmax,8:0.00000} {g,5} {buildMs,10:0.000} {passMs,10:0.000} " +
            $"{frameMs,10:0.000} {fps,8:0.0} {bruteProjMs,10:0.0} ms");
    }
}
Console.WriteLine();
Console.WriteLine("frame = build + pass (single core); brute proj = n^2 / measured AVX2 Mp/s (O(n^2) not run).");
Console.WriteLine("60 fps needs frame <= 16.67 ms; multi-core is M3.");

// --- M3 worker pool: parallel 3x3 neighbourhood pass at 500k (issue #68) -----
// The M2 grid makes 500k simulable but at ~65 ms/frame (~15 fps) on one core;
// M3 fans the (split-invariant) pass across the pool. We time the AVX2 grid
// frame = serial build (counting sort, stays serial in v1) + the THREADED pass
// over the frozen sorted IN bank (swarm_pass_mt), so the measured work is
// identical every round. The pass is bit-identical to the serial pass for any T
// (PassParallelMatchesSerial), so this is pure throughput, no accuracy trade.
// T = 0 asks the pool for the physical-core count (SMT hurts a divider-bound
// AVX2 loop); we also sweep smaller T to show the scaling curve.
if (haveAvx2)
{
    const int nMt = 500_000;
    const float rmaxMt = 1f / 512f; // g = 512, the best serial config at 500k
    Console.WriteLine();
    Console.WriteLine($"M3 worker pool (AVX2, FLAG_GRID): parallel pass at n={nMt}, g={GridDim(rmaxMt)}");

    double serialPassMs = TimeGridPass((uint)nMt, rmaxMt);
    double buildMs = TimeGridBuild((uint)nMt, rmaxMt);
    Console.WriteLine(
        $"  serial build : {buildMs,8:0.000} ms   serial pass : {serialPassMs,8:0.000} ms   " +
        $"frame {buildMs + serialPassMs,7:0.000} ms  ({1000.0 / (buildMs + serialPassMs),5:0.0} fps)");
    Console.WriteLine();
    Console.WriteLine($"{"T",4} {"pass ms",10} {"frame ms",10} {"fps",8} {"pass x",8}");
    Console.WriteLine(new string('-', 46));
    foreach (int t in new[] { 1, 2, 4, 8, 0 })
    {
        int actual = Native.swarm_pool_init(t);
        try
        {
            double passMs = TimeGridPassMt((uint)nMt, rmaxMt);
            double frameMs = buildMs + passMs;
            string label = t == 0 ? $"{actual}*" : actual.ToString();
            Console.WriteLine(
                $"{label,4} {passMs,10:0.000} {frameMs,10:0.000} {1000.0 / frameMs,8:0.0} {serialPassMs / passMs,7:0.00}x");
        }
        finally { Native.swarm_pool_shutdown(); }
    }
    Console.WriteLine();
    Console.WriteLine("frame = serial build + threaded pass; pass x = serial pass / threaded pass.");
    Console.WriteLine("* = physical-core count (auto). Pass is bit-identical to serial for every T.");
}

// --- The 1M baseline: serial frame, then the same frame threaded (issue #176)
// Every 1M figure in docs/BENCHMARKS.md before this was a projection from the
// 500k rows. The lever order for 1M - more threads, the AVX-512 path, the
// parallel-scatter contingency - is meant to be chosen from a measurement, and
// there was none to choose from.
//
// Two scenes, because decision 12 names two and they answer different halves of
// the question. Headline is rmax = 1/512, where the layout rule stops at
// g = 512 and the cells are as sparse as that rmax allows; dense is
// rmax = 1/256, where g halves to
// 256, each cell holds ~4x the particles, and k rises with it.
//
// The serial rows use the same instrument as the 500k rows above: build and
// pass timed separately over frozen input, min-of-rounds. The build column is
// the near-sorted one, i.e. every frame after the first, because TimeGrid runs
// a pass before it times the build; the first frame's build is the unsorted
// column of the #177 table below and is several times this one.
//
// The threaded rows fan only the pass. The build stays serial in v1, which is
// what risk 2 and its contingency are about, so it is added back unchanged into
// every threaded frame. A frame counting only the part that scaled would answer
// a question nobody asked.
if (haveAvx2)
{
    const uint n1M = 1_048_576; // the ABI's maximum n, and decision 3's headline count
    (string Name, float RMax)[] scenes = [("headline", 1f / 512f), ("dense", 1f / 256f)];

    Console.WriteLine();
    Console.WriteLine($"The 1M baseline (#176): serial grid frame at n={n1M}");
    Console.WriteLine();
    Console.WriteLine(
        $"{"scene",9} {"rmax",9} {"g",5} {"build ms",10} {"pass ms",10} {"frame ms",10} {"fps",8}");
    Console.WriteLine(new string('-', 66));

    var serialFrame = new Dictionary<string, (double Build, double Pass)>();
    foreach (var (name, rmax) in scenes)
    {
        var (buildMs, passMs) = TimeGrid(n1M, rmax);
        serialFrame[name] = (buildMs, passMs);
        double frameMs = buildMs + passMs;
        Console.WriteLine(
            $"{name,9} {rmax,9:0.000000} {GridDim(rmax),5} {buildMs,10:0.000} {passMs,10:0.000} " +
            $"{frameMs,10:0.000} {1000.0 / frameMs,8:0.0}");
    }
    Console.WriteLine();
    Console.WriteLine("build = near-sorted (every frame after the first); pass = 3x3 neighbourhood over frozen sorted IN.");

    Console.WriteLine();
    Console.WriteLine($"The 1M baseline (#176): threaded pass at n={n1M}, serial build added back");
    Console.WriteLine();
    Console.WriteLine($"{"scene",9} {"T",5} {"pass ms",10} {"frame ms",10} {"fps",8} {"pass x",8}");
    Console.WriteLine(new string('-', 54));
    foreach (var (name, rmax) in scenes)
    {
        (double buildMs, double serialPassMs) = serialFrame[name];
        foreach (int t in new[] { 1, 2, 4, 8, 0 })
        {
            int actual = Native.swarm_pool_init(t);
            try
            {
                double passMs = TimeGridPassMt(n1M, rmax);
                double frameMs = buildMs + passMs;
                string label = t == 0 ? $"{actual}*" : actual.ToString();
                Console.WriteLine(
                    $"{name,9} {label,5} {passMs,10:0.000} {frameMs,10:0.000} " +
                    $"{1000.0 / frameMs,8:0.0} {serialPassMs / passMs,7:0.00}x");
            }
            finally { Native.swarm_pool_shutdown(); }
        }
    }
    Console.WriteLine();
    Console.WriteLine("frame = serial build + threaded pass; pass x = serial pass / threaded pass.");
    Console.WriteLine("* = physical-core count (auto). 60 fps needs frame <= 16.67 ms.");
}

// --- Risk 2: the serial counting-sort build at 1M (issue #177) --------------
// Masterplan open-risk 2 estimates the histogram pass's same-address dependent
// chain at 8-12 cycles/particle and calls anything materially above ~4.5 ms at
// 1M an erosion of the frame margin. That estimate decides whether the build
// stays serial, and it has never been checked at 1M. The contingency it gates
// is the per-thread per-bucket-cursor parallel scatter.
//
// Build only: swarm_build over a frozen arena, min-of-rounds like every other
// figure here. Both grid dimensions the 1M scenes use are measured, because the
// build is O(g^2) in its zero-and-prefix half and the two differ four-fold in
// cell count. n = 1,048,576 is the ABI's maximum n, and the count decision 3's
// headline scene names.
//
// Both input states are measured. Risk 2 states its estimate for NEAR-SORTED
// input, which is every frame after the first; the unsorted column is the first
// frame, and the two differ by more than the budget, so reporting one of them
// as "the build cost" would decide the risk by choosing a column. 500k is
// carried as the control: its near-sorted figure has to reproduce the build
// column already in docs/BENCHMARKS.md, and its unsorted figure the M3
// section's worst-case build, or this instrument is measuring something else.
{
    const double RefGhz = 4.9; // as in the #59 section below; recorded per-host
    Console.WriteLine();
    Console.WriteLine("Serial grid build (#177): risk 2 estimates 8-12 cycles/particle on near-sorted input, ~4.5 ms at 1M");
    Console.WriteLine();
    Console.WriteLine(
        $"{"n",9} {"rmax",8} {"g",5} {"sorted ms",10} {"cyc/part",9} {"unsorted ms",12} {"cyc/part",9}");
    Console.WriteLine(new string('-', 70));
    foreach (uint n in new uint[] { 500_000, 1_048_576 })
    {
        foreach (float rmax in new[] { 1f / 256f, 1f / 512f })
        {
            double sortedMs = TimeGridBuild(n, rmax, nearSorted: true);
            double coldMs = TimeGridBuild(n, rmax);
            Console.WriteLine(
                $"{n,9} {rmax,8:0.00000} {GridDim(rmax),5} {sortedMs,10:0.000} " +
                $"{sortedMs * 1e6 / n * RefGhz,9:0.0} {coldMs,12:0.000} {coldMs * 1e6 / n * RefGhz,9:0.0}");
        }
    }
    Console.WriteLine();
    Console.WriteLine($"cyc/particle derived at RefGhz = {RefGhz:0.0}; ms is the clock-free primitive.");
    Console.WriteLine("sorted = OUT already cell-ordered (every frame after the first); unsorted = the first frame.");
}

// --- Risk 3: scatter locality under an energetic scene at 1M (issue #178) ----
// Masterplan open-risk 3 says the scatter estimate assumes temporal coherence,
// and that a hot matrix at the v_max clamp degrades write locality. Its probe is
// named there: an adversarial preset, all |a| = 1 and high force, against the
// coherent scene. Its fallback is its own, a two-pass radix over cell row then
// cell, and is not risk 2's parallel scatter.
//
// The scenes differ in the matrix and force_scale and in nothing else. Same n,
// same rmax, so the same g and the same cell count: the O(g^2) zero-and-prefix
// half of the build is identical between them by construction, and a difference
// that shows up is the scatter half. Choosing a different rmax for the hostile
// scene would have confounded exactly the thing being measured.
//
// Three scenes rather than two, because the scene every other row here uses is
// NOT a calm control. Measured below, it already sits with most of its velocity
// components at the clamp, so a two-scene probe would compare energetic against
// energetic and report the difference as an answer.
//
// All three are STEPPED before they are timed, which is the whole point. A scene
// is not energetic at frame 0; it is energetic after the matrix has had time to
// drive velocities to the clamp and pull the population into clumps and voids.
// Timing frame 0 would compare three identical uniform-random distributions and
// find, correctly and uselessly, no difference.
//
// Repeats are interleaved rather than blocked, so host drift lands on all three
// scenes instead of on whichever ran last. The spread within one scene is what
// says whether a difference between scenes means anything, and it is printed
// rather than collapsed into a mean.
//
// The build is timed on near-sorted input, which is what risk 3 is about: every
// frame after the first, where the previous frame's ordering is supposed to make
// the scatter cheap. The pass is timed alongside it because clumping moves k as
// well, and a build that held while the pass doubled would be a different
// finding than a build that collapsed.
if (haveAvx2)
{
    const uint nRisk3 = 1_048_576;
    const float rmaxRisk3 = 1f / 512f; // g = 512 for every scene
    const int settleSteps = 120; // 2.4 s of sim at dt = 0.02
    const int repeats = 3;
    (string Name, float Scale, bool Hostile)[] risk3 =
        [("calm", 1f, false), ("coherent", 10f, false), ("adversarial", 100f, true)];

    Console.WriteLine();
    Console.WriteLine(
        $"Scatter locality under an energetic scene (#178): n={nRisk3}, g={GridDim(rmaxRisk3)}, {settleSteps} steps before timing");
    Console.WriteLine();
    Console.WriteLine(
        $"{"rep",4} {"scene",12} {"force_scale",12} {"at v_max",9} {"build ms",10} {"pass ms",10} {"frame ms",10}");
    Console.WriteLine(new string('-', 72));

    for (int rep = 1; rep <= repeats; rep++)
    {
        foreach (var (name, scale, hostile) in risk3)
        {
            var (buildMs, passMs, forceScale, clampedPct) =
                TimeSettledGrid(nRisk3, rmaxRisk3, scale, hostile, settleSteps);
            Console.WriteLine(
                $"{rep,4} {name,12} {forceScale,12:0.0} {clampedPct,8:0.0}% {buildMs,10:0.000} " +
                $"{passMs,10:0.000} {buildMs + passMs,10:0.000}");
        }
    }
    Console.WriteLine();
    Console.WriteLine("adversarial = every matrix cell +-1 at the grammar's force_scale ceiling; calm and coherent keep the varied matrix and differ only in force_scale.");
    Console.WriteLine("at v_max = share of the 2n velocity components at the per-axis clamp after the settle, the premise risk 3 rests on.");
    Console.WriteLine("build is near-sorted (the frame after the previous frame's ordering), which is what risk 3 estimates.");
    Console.WriteLine("Read the spread across reps before reading a difference across scenes.");
}

// --- AVX2 force inner loop: cycles/candidate + throughput-vs-latency (#59) ---
// The premise the masterplan force-cost analysis (decision 3 / open-risk-1) and
// the #38 rsqrt design both rest on: what does one candidate pair cost in the
// AVX2 force group, and is that group THROUGHPUT-bound (execution units
// saturated) or LATENCY-bound on a loop-carried dependency chain?
//
// It reuses the same brute AVX2 pass the table above times; there is no separate
// kernel entry point to isolate the group, so the isolation is arithmetic: at a
// large n the O(n^2) inner loop is ~all of the pass (the once-per-i integrate
// tail is 1/n of the work), so ms/pass / n^2 is the per-candidate inner-loop
// cost. At n=16384 the tail is ~0.006% of the pass, so that row is clean. The
// force group processes 8 candidate lanes and runs exactly one vsqrtps + one
// vdivps, so cost/group = 8 x cost/candidate.
//
// ns/candidate is the clock-free measured primitive; cycles are derived at the
// recorded single-core sustained-AVX2 boost clock (per-machine, like every other
// number here) - edit RefGhz to your host.
//
// The throughput-vs-latency verdict rests on the DEPENDENCY STRUCTURE, not the
// n-sweep. The only loop-carried dependency across force groups is the fx/fy
// accumulator add (step.inc: vaddps ymm6/ymm7), ~3-4 cyc/group; the
// vsqrtps/vdivps chain is recomputed each group from independent neighbour loads
// and does NOT carry. Measured cost/group (~31 cyc) far exceeds that ~3-4 cyc
// carried floor, so the binding constraint is execution-unit THROUGHPUT, not the
// dependency chain. A flat cost/candidate as n grows is NOT a discriminator: a
// carried-chain-bound loop shows the same flat curve (extra iterations add no
// exploitable ILP), so the n-sweep below only bounds the per-i amortization
// term. A true empirical discriminator (e.g. a split-accumulator variant) would
// need a kernel edit, out of scope for this kernel-read-only bench.
if (haveAvx2)
{
    const double RefGhz = 4.9; // 5950X single-core boost under sustained AVX2; recorded per-host
    const int nSmall = 1024, nLarge = 16384;

    double smallMs = TimePass(nSmall, Avx2);
    double largeMs = TimePass(nLarge, Avx2);
    double smallNs = smallMs * 1e6 / ((double)nSmall * nSmall); // ns / candidate pair
    double largeNs = largeMs * 1e6 / ((double)nLarge * nLarge);
    double largeCyc = largeNs * RefGhz;      // cycles / candidate at RefGhz
    double largeGroupCyc = largeCyc * 8;     // cycles / 8-lane force group

    Console.WriteLine();
    Console.WriteLine("AVX2 force inner loop (#59): cycles/candidate, throughput vs latency");
    Console.WriteLine($"  representative n            : {nLarge}  (inner loop is 1 - 1/n of the pass)");
    Console.WriteLine(
        $"  measured                    : {largeNs:0.000} ns/candidate-pair  ({1e3 / largeNs:0.0} M/s)");
    Console.WriteLine(
        $"  per 8-lane force group      : {largeNs * 8:0.00} ns  (1x vsqrtps + 1x vdivps per group)");
    Console.WriteLine(
        $"  at RefGhz = {RefGhz:0.0} GHz          : {largeCyc:0.00} cycles/candidate = {largeGroupCyc:0.0} cycles/group");
    Console.WriteLine();
    Console.WriteLine("  Throughput-bound, not latency-bound (from the dependency structure):");
    Console.WriteLine(
        "    loop-carried floor = the fx/fy accumulator add, the only carried dep  ~3-4 cyc/group");
    Console.WriteLine(
        $"    measured ~{largeGroupCyc:0} cyc/group >> ~3-4 cyc -> execution-unit throughput binds, not the");
    Console.WriteLine(
        "    vsqrtps/vdivps chain (per-group, recomputed from independent loads). Matches ~1.25 G/s.");
    Console.WriteLine();
    Console.WriteLine("  Per-i amortization (NOT a latency/throughput discriminator - a flat curve is");
    Console.WriteLine("  the same signature for both bound classes):");
    Console.WriteLine(
        $"    n={nSmall,-6} : {smallNs:0.000} ns/candidate   ({1e3 / smallNs:0.0} M/s)");
    Console.WriteLine(
        $"    n={nLarge,-6} : {largeNs:0.000} ns/candidate   ({1e3 / largeNs:0.0} M/s)");
    Console.WriteLine(
        $"    the ~{(smallNs / largeNs - 1) * 100:0.#}% span is per-i pipeline serialization at the integrate barrier +");
    Console.WriteLine(
        "    the VEX<->SSE tail transition; the exact cause is not isolated here.");
    Console.WriteLine();
    Console.WriteLine(
        $"  The measured ~{largeGroupCyc:0} cyc/group is ~2x the published Zen 3 vsqrtps+vdivps ymm divide-pipe");
    Console.WriteLine(
        "  throughput (~11-15 cyc/group), so the divide unit is roughly half the loop, not >90%:");
    Console.WriteLine(
        "  the ~33 non-divide FP ops co-limit throughput. Isolating the exact divide fraction needs");
    Console.WriteLine(
        "  per-execution-port counters or a kernel-edit differential (out of scope) - see BENCHMARKS.md.");
}
return 0;

// --- helpers ---------------------------------------------------------------

// Best-of-rounds per-pass time in milliseconds. The minimum, not the mean:
// a force pass is a fixed amount of arithmetic, so the fastest observed round
// is the one least perturbed by scheduling and turbo transitions - the honest
// lower bound on the kernel's cost.
static unsafe double TimePass(uint n, uint forcePath)
{
    SwarmParams p = MakeParams(n, forcePath);
    ulong bytes = Native.swarm_layout_bytes(in p);
    if (bytes == 0)
        throw new InvalidOperationException($"layout rejected n={n} path={forcePath}");

    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        int rc = Native.swarm_init(arena, bytes, in p);
        if (rc != 0)
            throw new InvalidOperationException($"init failed rc={rc} n={n} path={forcePath}");
        Native.swarm_build(arena); // freeze IN; every timed pass recomputes from it

        for (int i = 0; i < 3; i++)
            Native.swarm_pass(arena, 0, n); // warm caches and clock ramp

        // Size each round to ~120 ms so the Stopwatch resolution is negligible.
        var est = Stopwatch.StartNew();
        Native.swarm_pass(arena, 0, n);
        est.Stop();
        double oneMs = Math.Max(est.Elapsed.TotalMilliseconds, 1e-3);
        int perRound = Math.Clamp((int)(120.0 / oneMs), 1, 100_000);

        double best = double.MaxValue;
        for (int round = 0; round < 9; round++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < perRound; i++)
                Native.swarm_pass(arena, 0, n);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds / perRound);
        }
        return best;
    }
    finally
    {
        NativeMemory.AlignedFree(arena);
    }
}

static SwarmParams MakeParams(uint n, uint forcePath)
{
    var p = new SwarmParams
    {
        Version = 1,
        N = n,
        SpeciesN = 6,
        Seed = 0x5EED,
        RMax = 0.05f,
        Beta = 0.3f,
        Dt = 0.02f,
        Friction = 0.71f,
        ForceScale = 10f,
        ForcePath = forcePath,
        Flags = 0,
    };
    // varied, deterministic matrix in [-1,1] so the attraction path is exercised
    for (uint a = 0; a < 6; a++)
        for (uint b = 0; b < 6; b++)
            p.Matrix[(int)(a * 8 + b)] = MathF.Sin(a * 3.1f + b * 1.7f);
    return p;
}

// Grid frame cost, split into the counting-sort build and the 3x3
// neighbourhood pass, each timed over frozen input so the work is identical
// every round (build once then repeat the pass over the sorted IN; repeat the
// build over the frozen OUT bank).
static unsafe (double build, double pass) TimeGrid(uint n, float rmax)
{
    SwarmParams p = MakeGridParams(n, rmax);
    ulong bytes = Native.swarm_layout_bytes(in p);
    if (bytes == 0)
        throw new InvalidOperationException($"layout rejected n={n} rmax={rmax}");

    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        int rc = Native.swarm_init(arena, bytes, in p);
        if (rc != 0)
            throw new InvalidOperationException($"init failed rc={rc} n={n} rmax={rmax}");

        Native.swarm_build(arena); // sort IN once; the pass then recomputes from it
        for (int i = 0; i < 3; i++)
            Native.swarm_pass(arena, 0, n);
        double passMs = MinOfRounds(() => Native.swarm_pass(arena, 0, n));

        for (int i = 0; i < 3; i++)
            Native.swarm_build(arena);
        double buildMs = MinOfRounds(() => Native.swarm_build(arena));

        return (buildMs, passMs);
    }
    finally
    {
        NativeMemory.AlignedFree(arena);
    }
}

// Serial grid pass (min-of-rounds over the frozen sorted IN bank), for the M3
// worker-pool comparison. Mirrors TimeGrid's pass timing in isolation.
static unsafe double TimeGridPass(uint n, float rmax)
{
    SwarmParams p = MakeGridParams(n, rmax);
    ulong bytes = Native.swarm_layout_bytes(in p);
    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException($"init failed n={n} rmax={rmax}");
        Native.swarm_build(arena);
        for (int i = 0; i < 3; i++)
            Native.swarm_pass(arena, 0, n);
        return MinOfRounds(() => Native.swarm_pass(arena, 0, n));
    }
    finally { NativeMemory.AlignedFree(arena); }
}

// Serial counting-sort build (min-of-rounds over the frozen OUT bank).
//
// The state of OUT is the whole measurement, so it is a parameter rather than
// an accident of call order. The build scatters OUT into cell order, and how
// far OUT already is from that order sets the write locality:
//
//   nearSorted: false - OUT is the id-ordered initial frame, so the scatter
//     writes land all over the IN bank. This is the FIRST frame of a run, and
//     the worst case.
//   nearSorted: true - a pass has run over sorted IN, so OUT is written at the
//     same indices and is already cell-ordered. This is every frame after the
//     first, and it is the "near-sorted input" masterplan risk 2 estimates.
static unsafe double TimeGridBuild(uint n, float rmax, bool nearSorted = false)
{
    SwarmParams p = MakeGridParams(n, rmax);
    ulong bytes = Native.swarm_layout_bytes(in p);
    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException($"init failed n={n} rmax={rmax}");
        if (nearSorted)
        {
            Native.swarm_build(arena); // IN becomes cell-ordered ...
            Native.swarm_pass(arena, 0, n); // ... and the pass carries that order into OUT
        }
        for (int i = 0; i < 3; i++)
            Native.swarm_build(arena);
        return MinOfRounds(() => Native.swarm_build(arena));
    }
    finally { NativeMemory.AlignedFree(arena); }
}

// Grid build and pass for a world that has been STEPPED first, so the timed
// state is the one the scene actually settles into rather than the uniform
// random draw every other figure here starts from.
//
// hostile replaces the matrix with every cell at +-1, which is risk 3's "all
// |a| = 1"; forceScale is the other half of "high force" and is a parameter so
// a calm control can exist. Everything else, n and rmax and therefore g and the
// cell count, is identical across scenes, so the O(g^2) half of the build
// cannot account for a difference between them.
static unsafe (double Build, double Pass, float ForceScale, double ClampedPct) TimeSettledGrid(
    uint n, float rmax, float forceScale, bool hostile, int steps)
{
    SwarmParams p = MakeGridParams(n, rmax);
    p.ForceScale = forceScale; // (0, 100] per the grammar
    if (hostile)
    {
        for (uint a = 0; a < 6; a++)
            for (uint b = 0; b < 6; b++)
                p.Matrix[(int)(a * 8 + b)] = ((a + b) & 1) == 0 ? 1f : -1f;
    }

    ulong bytes = Native.swarm_layout_bytes(in p);
    if (bytes == 0)
        throw new InvalidOperationException($"layout rejected n={n} rmax={rmax}");

    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException($"init failed n={n} rmax={rmax} hostile={hostile}");

        Native.swarm_step(arena, (uint)steps); // let the scene become what it is

        // "Energetic" is the premise risk 3 rests on, so it is measured rather
        // than assumed: the share of velocity components sitting at the v_max
        // clamp after the settle. A hostile scene that turned out to be calm
        // would make the rest of the row meaningless without saying so.
        double clampedPct = ClampedFraction(arena, n, rmax / p.Dt) * 100.0;

        Native.swarm_build(arena);
        for (int i = 0; i < 3; i++)
            Native.swarm_pass(arena, 0, n);
        double passMs = MinOfRounds(() => Native.swarm_pass(arena, 0, n));

        for (int i = 0; i < 3; i++)
            Native.swarm_build(arena);
        double buildMs = MinOfRounds(() => Native.swarm_build(arena));

        return (buildMs, passMs, p.ForceScale, clampedPct);
    }
    finally { NativeMemory.AlignedFree(arena); }
}

// Share of the 2n velocity components at the per-axis v_max clamp. The clamp is
// a hard saturation, so equality is the right test and no tolerance is needed.
static unsafe double ClampedFraction(void* arena, uint n, float vmax)
{
    float* x = (float*)NativeMemory.Alloc(n, sizeof(float));
    float* y = (float*)NativeMemory.Alloc(n, sizeof(float));
    float* vx = (float*)NativeMemory.Alloc(n, sizeof(float));
    float* vy = (float*)NativeMemory.Alloc(n, sizeof(float));
    int* s = (int*)NativeMemory.Alloc(n, sizeof(int));
    try
    {
        if (Native.swarm_read_state(arena, x, y, vx, vy, s) != 0)
            throw new InvalidOperationException("swarm_read_state reported a dropped id; the copy-out is untrustworthy");

        long clamped = 0;
        for (uint i = 0; i < n; i++)
        {
            if (MathF.Abs(vx[i]) >= vmax) clamped++;
            if (MathF.Abs(vy[i]) >= vmax) clamped++;
        }
        return clamped / (2.0 * n);
    }
    finally
    {
        NativeMemory.Free(x); NativeMemory.Free(y);
        NativeMemory.Free(vx); NativeMemory.Free(vy); NativeMemory.Free(s);
    }
}

// Threaded grid pass (min-of-rounds over the frozen sorted IN bank). The pool
// must already be initialised by the caller (swarm_pool_init); the pass fans
// across it and is bit-identical to the serial pass for any T.
static unsafe double TimeGridPassMt(uint n, float rmax)
{
    SwarmParams p = MakeGridParams(n, rmax);
    ulong bytes = Native.swarm_layout_bytes(in p);
    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException($"init failed n={n} rmax={rmax}");
        Native.swarm_build(arena);
        for (int i = 0; i < 3; i++)
            Native.swarm_pass_mt(arena);
        return MinOfRounds(() => Native.swarm_pass_mt(arena));
    }
    finally { NativeMemory.AlignedFree(arena); }
}

// Best (min) per-call time in ms over 9 rounds, each round sized to run for at
// least ~120 ms so the Stopwatch resolution is negligible. The minimum is the
// honest lower bound on a fixed-work kernel call (least perturbed by scheduling
// and clock transitions).
static double MinOfRounds(Action work)
{
    var est = Stopwatch.StartNew();
    work();
    est.Stop();
    double oneMs = Math.Max(est.Elapsed.TotalMilliseconds, 1e-3);
    int perRound = Math.Clamp((int)(120.0 / oneMs), 1, 100_000);

    double best = double.MaxValue;
    for (int round = 0; round < 9; round++)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < perRound; i++)
            work();
        sw.Stop();
        best = Math.Min(best, sw.Elapsed.TotalMilliseconds / perRound);
    }
    return best;
}

// Grid dimension for a preset, mirroring arena_dims_core (layout.inc): the
// largest power of two with 1/g >= rmax, clamped to [4, 512].
static int GridDim(float rmax)
{
    int g = 4;
    while (g < 512 && 1f / (2 * g) >= rmax)
        g *= 2;
    return g;
}

static SwarmParams MakeGridParams(uint n, float rmax)
{
    var p = new SwarmParams
    {
        Version = 1,
        N = n,
        SpeciesN = 6,
        Seed = 0x5EED,
        RMax = rmax,
        Beta = 0.3f,
        Dt = 0.02f,
        Friction = 0.71f,
        ForceScale = 10f,
        ForcePath = 1, // AVX2
        Flags = 1, // FLAG_GRID
    };
    for (uint a = 0; a < 6; a++)
        for (uint b = 0; b < 6; b++)
            p.Matrix[(int)(a * 8 + b)] = MathF.Sin(a * 3.1f + b * 1.7f);
    return p;
}

// Assemble the kernel exactly as build.ps1 does, so the benchmarked binary is
// the shipping binary. Returns the absolute DLL path.
static string EnsureBuilt()
{
    string? root = null;
    for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
    {
        if (File.Exists(Path.Combine(d, "build.ps1")))
        {
            root = d;
            break;
        }
    }
    if (root is null)
        throw new InvalidOperationException("repo root (the directory holding build.ps1) not found");

    var psi = new ProcessStartInfo("powershell")
    {
        WorkingDirectory = root,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    psi.ArgumentList.Add("-NoProfile");
    psi.ArgumentList.Add("-ExecutionPolicy");
    psi.ArgumentList.Add("Bypass");
    psi.ArgumentList.Add("-File");
    psi.ArgumentList.Add(Path.Combine(root, "build.ps1"));

    using var proc =
        Process.Start(psi) ?? throw new InvalidOperationException("could not start powershell to assemble");
    var err = new System.Text.StringBuilder();
    proc.OutputDataReceived += (_, _) => { };
    proc.ErrorDataReceived += (_, e) =>
    {
        if (e.Data is not null)
            err.AppendLine(e.Data);
    };
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    if (!proc.WaitForExit(300_000))
    {
        proc.Kill(entireProcessTree: true);
        throw new InvalidOperationException("build.ps1 did not finish within 5 minutes");
    }
    proc.WaitForExit();
    if (proc.ExitCode != 0)
        throw new InvalidOperationException($"build.ps1 failed (exit {proc.ExitCode}):\n{err}");

    return Path.Combine(root, "build", "swarm.kernel.dll");
}

// --- the grid-dimension sweep (#148) ---------------------------------------

// Total frame time at the headline count as a function of the cell dimension,
// across the rmax values where the ceiling in src/kernel/layout.inc starts to
// bind. The force pass gets cheaper as cells get finer and fewer candidates
// fall inside the 3x3 neighbourhood; the build pays for the cells themselves,
// zeroing and prefixing g*g+1 entries every frame. Both halves are timed here
// and added, because the crossover is only visible in the total.
//
// The dimension is not an input. It follows from rmax by the layout rule and
// then meets the ceiling, so raising the ceiling means assembling a different
// kernel. What the run prints as g is read out of the arena header (AH_G in
// src/kernel/abi.inc), never recomputed here, so the table cannot disagree
// with the build that produced it.
static unsafe void GridSweep()
{
    const uint N = 1_048_576;
    float[] rmaxes = [0.002f, 0.001f, 0.0007f, 0.0004f];

    Console.WriteLine($"Grid dimension sweep at n={N} across the rmax ceiling (#148)");
    Console.WriteLine($"  ceiling in this build : g = {BuiltGridCeiling()}");
    Console.WriteLine($"  seed                  : 0x{MakeGridParams(N, rmaxes[0]).Seed:X}, 6 species, force_path=1, FLAG_GRID");
    Console.WriteLine();
    Console.WriteLine(
        $"{"rmax",9} {"g",6} {"cand/pt",9} {"build ms",10} {"pass ms",10} {"frame ms",10} {"fps",7} {"ns/cand",9}");
    Console.WriteLine(new string('-', 80));

    foreach (float rmax in rmaxes)
    {
        var (buildMs, passMs, g, candidates) = TimeGridWithCandidates(N, rmax);
        double frameMs = buildMs + passMs;
        double nsPerCandidate = candidates > 0 ? passMs * 1e6 / (N * candidates) : 0;
        Console.WriteLine(
            $"{rmax,9:0.000000} {g,6} {candidates,9:0.00} {buildMs,10:0.000} {passMs,10:0.000} " +
            $"{frameMs,10:0.000} {1000.0 / frameMs,7:0.0} {nsPerCandidate,9:0.000}");
    }

    Console.WriteLine();
    Console.WriteLine("frame = serial near-sorted build + serial pass, each min-of-rounds over frozen input.");
    Console.WriteLine("cand/pt = mean particles in the 3x3 wrapped neighbourhood of a particle's own cell.");
}

// The dimension the assembled kernel will not go past, asked of the kernel
// rather than assumed: an rmax far below any cell edge the ceiling permits
// resolves to the ceiling itself.
static unsafe int BuiltGridCeiling()
{
    SwarmParams p = MakeGridParams(4096, 1e-6f);
    ulong bytes = Native.swarm_layout_bytes(in p);
    if (bytes == 0)
        throw new InvalidOperationException("layout rejected the ceiling probe");

    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException("init rejected the ceiling probe");
        return (int)*(uint*)((byte*)arena + 36); // AH_G (abi.inc)
    }
    finally { NativeMemory.AlignedFree(arena); }
}

// TimeGrid's two figures, plus the dimension the kernel actually chose and the
// candidate count that explains the pass time.
static unsafe (double Build, double Pass, int G, double Candidates) TimeGridWithCandidates(uint n, float rmax)
{
    SwarmParams p = MakeGridParams(n, rmax);
    ulong bytes = Native.swarm_layout_bytes(in p);
    if (bytes == 0)
        throw new InvalidOperationException($"layout rejected n={n} rmax={rmax}");

    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException($"init failed at n={n} rmax={rmax}");

        int g = (int)*(uint*)((byte*)arena + 36); // AH_G (abi.inc)

        Native.swarm_build(arena);
        double candidates = MeanCandidatesPerParticle(arena, n, g);

        for (int i = 0; i < 3; i++)
            Native.swarm_pass(arena, 0, n);
        double passMs = MinOfRounds(() => Native.swarm_pass(arena, 0, n));

        for (int i = 0; i < 3; i++)
            Native.swarm_build(arena);
        double buildMs = MinOfRounds(() => Native.swarm_build(arena));

        return (buildMs, passMs, g, candidates);
    }
    finally { NativeMemory.AlignedFree(arena); }
}

// Mean population of the 3x3 neighbourhood a particle's force loop walks.
//
// Counted from the copied-out positions with the kernel's own cell rule,
// cx = int(x*g) & (g-1) (grid.inc), and its own wrap: the neighbourhood is a
// torus, so no cell is short of neighbours at an edge and none is counted
// twice while g >= 4. The figure includes the particle itself, because the
// pass walks its own cell whole.
static unsafe double MeanCandidatesPerParticle(void* arena, uint n, int g)
{
    float* x = (float*)NativeMemory.Alloc(n, sizeof(float));
    float* y = (float*)NativeMemory.Alloc(n, sizeof(float));
    float* vx = (float*)NativeMemory.Alloc(n, sizeof(float));
    float* vy = (float*)NativeMemory.Alloc(n, sizeof(float));
    int* s = (int*)NativeMemory.Alloc(n, sizeof(int));
    var count = new int[(long)g * g];
    try
    {
        if (Native.swarm_read_state(arena, x, y, vx, vy, s) != 0)
            throw new InvalidOperationException("swarm_read_state reported a dropped id");

        int mask = g - 1;
        for (uint i = 0; i < n; i++)
        {
            int cx = (int)(x[i] * g) & mask;
            int cy = (int)(y[i] * g) & mask;
            count[(long)cy * g + cx]++;
        }

        double weighted = 0;
        for (int cy = 0; cy < g; cy++)
        {
            for (int cx = 0; cx < g; cx++)
            {
                int here = count[(long)cy * g + cx];
                if (here == 0)
                    continue;

                int neighbourhood = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    long row = (long)((cy + dy) & mask) * g;
                    for (int dx = -1; dx <= 1; dx++)
                        neighbourhood += count[row + ((cx + dx) & mask)];
                }
                weighted += (double)here * neighbourhood;
            }
        }
        return weighted / n;
    }
    finally
    {
        NativeMemory.Free(x); NativeMemory.Free(y);
        NativeMemory.Free(vx); NativeMemory.Free(vy); NativeMemory.Free(s);
    }
}

// --- the build's n-independent half (#243) ---------------------------------
//
// grid_sort is four phases over one (g*g + 1)-dword block: zero, histogram,
// inclusive prefix, backward scatter (src/kernel/grid.inc). Two of them never
// read n - the zero and the prefix walk every bucket whatever the population
// is - and two are proportional to it. The parallel-scatter contingency divides
// the O(n) half by the worker count and multiplies the O(g^2) half by it, so
// which of the two carries the 7.049 ms recorded for the serial build at 1M
// decides whether that trade can pay at all. Nothing else here splits them, and
// subtracting adjacent rows of the #148 sweep does not: rows doing identical
// build work there spread by 19% of the smallest.
//
// The split is taken without a timer inside grid.inc, which would cost
// src/kernel/ its purity. g is a function of rmax alone - the largest power of
// two with 1/g >= rmax, clamped to [4, 512] (src/kernel/layout.inc) - so
// holding rmax fixed and moving n holds the O(g^2) work exactly constant and
// moves only the O(n) work. The ladder's bottom rung is then the O(g^2) half
// plus a residue that the rungs above it measure rather than assume.
//
// What this does NOT separate is zero from prefix, or histogram from scatter.
// Each of those needs a clock inside the kernel. The contingency's question
// does not ask for them: it moves the two groups in opposite directions, and
// the groups are what this measures.
//
// Both input states are carried, because the O(n) half is the one whose cost
// depends on them and the O(g^2) half is the one that cannot. Two dimensions
// are carried for the same reason in the other direction: g = 512 is the
// headline scene and g = 256 the dense one, a four-fold difference in cell
// count, so the bottom rungs of the two ladders are a check on the instrument
// rather than a second result.
static unsafe void BuildSplit()
{
    Console.WriteLine();
    Console.WriteLine("The build's n-independent half (#243): O(g^2) zero+prefix against O(n) histogram+scatter");
    Console.WriteLine();

    uint[] ladder = [1024, 2048, 4096, 8192, 16384, 65536, 262144, 500_000, 1_048_576];

    foreach (float rmax in new[] { 1f / 512f, 1f / 256f })
    {
        int g = GridDim(rmax);
        Console.WriteLine($"rmax = {rmax:0.00000}, g = {g}, cells = {(long)g * g}");
        Console.WriteLine($"{"n",9} {"sorted ms",10} {"unsorted ms",12}");
        Console.WriteLine(new string('-', 34));

        var sorted = new double[ladder.Length];
        for (int i = 0; i < ladder.Length; i++)
        {
            sorted[i] = TimeGridBuild(ladder[i], rmax, nearSorted: true);
            double cold = TimeGridBuild(ladder[i], rmax);
            Console.WriteLine($"{ladder[i],9} {sorted[i],10:0.000} {cold,12:0.000}");
        }

        // A least-squares line through the bottom four rungs, extrapolated to
        // n = 0, is the O(g^2) half on its own. Four points rather than two
        // because at g = 256 the whole quantity is tens of microseconds and a
        // two-point slope there is drawn through the host's noise; the fit is
        // over the rungs where the O(n) term is small enough not to bend it.
        //
        // It is an extrapolation and is printed beside the rung it starts from,
        // so a reader can see how far it moved. The per-particle cost at 1024
        // is not the per-particle cost at 1M, which is why the intercept and
        // not the slope is what is read off here.
        int fit = 4;
        double mx = 0, my = 0;
        for (int i = 0; i < fit; i++) { mx += ladder[i]; my += sorted[i]; }
        mx /= fit;
        my /= fit;
        double sxy = 0, sxx = 0;
        for (int i = 0; i < fit; i++)
        {
            sxy += (ladder[i] - mx) * (sorted[i] - my);
            sxx += (ladder[i] - mx) * (ladder[i] - mx);
        }
        double intercept = my - sxy / sxx * mx;
        double top = sorted[^1];

        Console.WriteLine();
        Console.WriteLine($"  {$"O(g^2) half (bottom {fit} rungs fitted back to n = 0)",-52} : {intercept,6:0.000} ms");
        Console.WriteLine($"  {$"bottom rung, n = {ladder[0]}",-52} : {sorted[0],6:0.000} ms");
        Console.WriteLine($"  {$"build at n = {ladder[^1]}",-52} : {top,6:0.000} ms");
        Console.WriteLine(
            $"  {"O(n) half there",-52} : {top - intercept,6:0.000} ms"
            + $"  ({(top - intercept) / top * 100:0.0}% of the build)");
        Console.WriteLine();
    }

    Console.WriteLine("sorted = OUT already cell-ordered (every frame after the first); unsorted = the first frame.");
    Console.WriteLine("min-of-rounds, as every figure in this harness; ms is the clock-free primitive.");
}

// --- native surface + the ABI-mirrored params struct -----------------------

internal static unsafe class Native
{
    [DllImport("swarm.kernel.dll")]
    internal static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    internal static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    internal static extern void swarm_build(void* arena);

    [DllImport("swarm.kernel.dll")]
    internal static extern void swarm_pass(void* arena, uint first, uint last);

    [DllImport("swarm.kernel.dll")]
    internal static extern int swarm_cpu_paths();

    // M3 worker pool (issue #68). swarm_pool_init(0) auto-detects physical cores
    // and returns the actual worker count; swarm_pass_mt fans the pass over the
    // pool; swarm_pool_shutdown joins and closes the threads.
    [DllImport("swarm.kernel.dll")]
    internal static extern int swarm_pool_init(int requested);

    [DllImport("swarm.kernel.dll")]
    internal static extern void swarm_pass_mt(void* arena);

    [DllImport("swarm.kernel.dll")]
    internal static extern void swarm_pool_shutdown();

    // n_steps x (build + pass + bank swap). Used only to advance a world into
    // the state a scene actually reaches, before anything about it is timed.
    [DllImport("swarm.kernel.dll")]
    internal static extern void swarm_step(void* arena, uint nSteps);

    // Id-ordered copy-out. Used here to check that a scene claimed to be
    // energetic actually is, rather than to time anything. Returns 1 if any id
    // fell outside [0, n), in which case the copy is untrustworthy.
    [DllImport("swarm.kernel.dll")]
    internal static extern int swarm_read_state(
        void* arena, float* x, float* y, float* vx, float* vy, int* species);
}

// 1:1 mirror of the native SwarmParams seam struct (src/kernel/abi.inc):
// sequential, Pack=4, 304 bytes - Pack=4 places the u64 seed at offset 12,
// matching the asm. Kept identical to the copy in Swarm.Tests on purpose;
// this project stays standalone (no cross-reference to the MTP test assembly).
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SwarmParams
{
    public uint Version;
    public uint N;
    public uint SpeciesN;
    public ulong Seed;
    public float RMax;
    public float Beta;
    public float Dt;
    public float Friction;
    public float ForceScale;
    public uint ForcePath;
    public uint Flags;
    public Matrix64 Matrix;
}

[System.Runtime.CompilerServices.InlineArray(64)]
internal struct Matrix64
{
    private float _e0;
}
