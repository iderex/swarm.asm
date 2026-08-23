using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
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

// The contingency's own re-measurement (#243), behind an argument for the same
// two reasons as the split above, plus a third: it holds a live worker pool, and
// a pool running through the rest of the report would change every figure after
// it.
if (args.Contains("--buildmt"))
{
    BuildMt();
    return 0;
}

// The plot phase (#125), behind an argument for the first of those reasons and
// one of its own: it is the only section here that allocates a framebuffer as
// well as an arena, and at 1M it settles a scene with swarm_step first, which
// takes seconds of work none of the other sections want in front of them.
if (args.Contains("--plot"))
{
    PlotPhase();
    return 0;
}

// The committed headline scene at the seam (#125), behind an argument for the
// same two reasons as the plot mode above and one of its own: it settles a 1M
// scene 600 steps at a time before it times anything, which is minutes of work
// that belongs in front of nothing else.
if (args.Contains("--scene"))
{
    SceneFrame();
    return 0;
}

// The 500k claim re-examined at settle depth (#5), behind an argument for the
// same reasons as the scene mode above; it is that mode's instrument pointed at
// a different count and a different scene.
if (args.Contains("--m3settle"))
{
    M3Settle();
    return 0;
}

// The README assets (#131), behind an argument because it is the only mode
// here that writes files into the tree and measures nothing at all. It renders
// the shipped executable's own scene through swarm_plot and encodes the
// framebuffer, so the picture in the README is the kernel's raster rather than
// a screenshot of one.
if (args.Contains("--asset"))
{
    ReadmeAssets();
    return 0;
}

// The managed baseline of the competitor comparison (#153), behind an argument
// because it is the only section here that spends most of its runtime outside
// the kernel: the managed pass is several times the cost of the one it is
// compared against, at every count, so running it in front of the default
// report would put minutes of another engine's work ahead of this one's.
if (args.Contains("--managed"))
{
    ManagedReport();
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
// The threaded rows fan both halves. The pass fans over the particle range and
// the build fans across W = clamp(n/(g*g), 1, T) workers (#243), and the frame
// column is the two measured at the same T in the same run, never one taken
// here and one carried in from another section. Risk 2 and its contingency are
// about that build, so it is timed rather than assumed: a frame counting only
// the part that scaled would answer a question nobody asked, and a frame
// carrying a serial build the shipped binary no longer runs would answer a
// question nobody has any more.
//
// The serial-build column stays beside it. The rows recorded in
// docs/BENCHMARKS.md under "The 1M baseline" were taken before the parallel
// build existed and add the serial build back, so printing both is what lets a
// reader tell the two generations of row apart instead of subtracting across
// runs.
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
    Console.WriteLine($"The 1M threaded frame (#125): parallel build and threaded pass at n={n1M}");
    Console.WriteLine();
    Console.WriteLine(
        $"{"scene",9} {"T",5} {"build ms",10} {"pass ms",10} {"frame ms",10} {"fps",8} {"build x",8} {"pass x",8} {"serial-build frame ms",22}");
    Console.WriteLine(new string('-', 96));
    foreach (var (name, rmax) in scenes)
    {
        (double serialBuildMs, double serialPassMs) = serialFrame[name];
        foreach (int t in new[] { 1, 2, 4, 8, 0 })
        {
            int actual = Native.swarm_pool_init(t);
            if (actual < 1)
                throw new InvalidOperationException($"pool_init({t}) failed");
            try
            {
                double passMs = TimeGridPassMt(n1M, rmax);
                double buildMs = TimeGridBuildMt(n1M, rmax, nearSorted: true);
                double frameMs = buildMs + passMs;
                string label = t == 0 ? $"{actual}*" : actual.ToString();
                Console.WriteLine(
                    $"{name,9} {label,5} {buildMs,10:0.000} {passMs,10:0.000} {frameMs,10:0.000} " +
                    $"{1000.0 / frameMs,8:0.0} {serialBuildMs / buildMs,7:0.00}x {serialPassMs / passMs,7:0.00}x " +
                    $"{serialBuildMs + passMs,22:0.000}");
            }
            finally { Native.swarm_pool_shutdown(); }
        }
    }
    Console.WriteLine();
    Console.WriteLine("frame = parallel build + threaded pass, both at that T and both from this run.");
    Console.WriteLine("build/pass x = serial ms / parallel ms at that T; build is near-sorted, i.e. every frame after the first.");
    Console.WriteLine("serial-build frame ms = the same threaded pass with the serial build added back, the shape the");
    Console.WriteLine("  recorded 'The 1M baseline' rows carry; printed so the two generations are not subtracted across runs.");
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
// The parallel build at a given thread count, on the same two input states and
// with the same warm-up and min-of-rounds discipline TimeGridBuild uses, so the
// two columns are subtractable. The pool must already be initialised by the
// caller; swarm_build_mt falls back to the serial build when there is none, and
// a figure taken through that fallback would read as a parallel one.
static unsafe double TimeGridBuildMt(uint n, float rmax, bool nearSorted)
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
            Native.swarm_build(arena);
            Native.swarm_pass(arena, 0, n);
        }
        for (int i = 0; i < 3; i++)
            Native.swarm_build_mt(arena);
        return MinOfRounds(() => Native.swarm_build_mt(arena));
    }
    finally { NativeMemory.AlignedFree(arena); }
}

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
static string RepoRoot()
{
    for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
        if (File.Exists(Path.Combine(d, "build.ps1")))
            return d;
    throw new InvalidOperationException("repo root (the directory holding build.ps1) not found");
}

static string EnsureBuilt()
{
    string root = RepoRoot();

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
// --- Risk 2's contingency, measured (#243) ---------------------------------
// The parallel scatter against the serial build it replaces, at the count and
// the two dimensions the 1M scenes use, on both input states.
//
// The serial column is re-measured in the same run rather than quoted from the
// section above, because the two figures are only subtractable if the host was
// in the same state for both. The thread sweep is the whole result: the
// contingency divides the O(n) half by T and multiplies the O(g^2) half by it,
// so a win at one T is not a win at another, and the crossing point is the thing
// the masterplan risk wants recorded.
//
// Both input states, because the risk is stated for near-sorted input and the
// unsorted column is the first frame, and they differ by more than the frame
// budget.
static unsafe void BuildMt()
{
    Console.WriteLine();
    Console.WriteLine("Risk 2's contingency (#243): parallel scatter vs the serial build at 1M");
    Console.WriteLine();

    const uint N = 1_048_576;
    foreach (float rmax in new[] { 1f / 512f, 1f / 256f })
    {
        int g = GridDim(rmax);
        double serialSorted = TimeGridBuild(N, rmax, nearSorted: true);
        double serialCold = TimeGridBuild(N, rmax);
        Console.WriteLine($"rmax = {rmax:0.00000}, g = {g}, cells = {(long)g * g}");
        Console.WriteLine($"  serial: sorted {serialSorted:0.000} ms   unsorted {serialCold:0.000} ms");
        Console.WriteLine();
        Console.WriteLine($"{"T",4} {"sorted ms",10} {"x",7} {"unsorted ms",12} {"x",7}");
        Console.WriteLine(new string('-', 44));
        foreach (int t in new[] { 1, 2, 4, 8, 16, 0 })
        {
            int actual = Native.swarm_pool_init(t);
            if (actual < 1)
                throw new InvalidOperationException($"pool_init({t}) failed");
            try
            {
                double sorted = TimeGridBuildMt(N, rmax, nearSorted: true);
                double cold = TimeGridBuildMt(N, rmax, nearSorted: false);
                Console.WriteLine(
                    $"{actual,4} {sorted,10:0.000} {serialSorted / sorted,6:0.00}x " +
                    $"{cold,12:0.000} {serialCold / cold,6:0.00}x");
            }
            finally { Native.swarm_pool_shutdown(); }
        }
        Console.WriteLine();
    }
    Console.WriteLine("T is the pool's ACTUAL worker count (0 asks for the physical-core count).");
    Console.WriteLine("x = serial ms / parallel ms at that T; below 1.00 the contingency costs more than it saves.");
    Console.WriteLine("ms = best of 9 rounds, as every other figure here; the host was not quiesced unless said so.");
}

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

// The plot phase (#125). Decision 11's acceptance asks for a frame broken into
// build / pass / plot / blit, and plot is the one of the four that no figure in
// docs/BENCHMARKS.md covers at any count.
//
// plot_core is two pieces of different shape. The clear is a rep stosd over w*h
// pixels and does not depend on n at all; the raster is one scattered dword
// store per particle and depends both on n and on where the particles are. They
// are separated here the way the build's two halves are (#243), by fitting the
// bottom rungs of an n ladder back to n = 0, because the grammar accepts no n
// small enough to time a clear on its own.
//
// w and h are the shipped exe's framebuffer, FRAME_W = FRAME_H = 1024 in
// src/swarm.asm, so the number is the one the live loop pays rather than one
// for a buffer nothing draws. FLAG_SPLAT is off in every row, which is the
// 1-pixel raster the rest of this document publishes.
static unsafe void PlotPhase()
{
    const uint W = 1024, H = 1024; // FRAME_W / FRAME_H, src/swarm.asm
    const uint N = 1_048_576;
    const int Warmup = 600; // the warm-up decision 11's acceptance discards
    const float Headline = 1f / 512f, Dense = 1f / 256f;

    Console.WriteLine();
    Console.WriteLine($"The plot phase at the shipped framebuffer ({W}x{H}, 1-pixel raster; #125)");
    Console.WriteLine();

    uint[] ladder = [1024, 2048, 4096, 8192, 16384, 65536, 262144, 500_000, 1_048_576];

    Console.WriteLine($"n ladder at rmax = {Headline:0.000000} (g = {GridDim(Headline)}), OUT cell-ordered");
    Console.WriteLine($"{"n",9} {"plot ms",9} {"lit px %",9}");
    Console.WriteLine(new string('-', 29));

    var ms = new double[ladder.Length];
    for (int i = 0; i < ladder.Length; i++)
    {
        (ms[i], double litPct) = TimePlot(ladder[i], Headline, PlotState.Ordered, W, H);
        Console.WriteLine($"{ladder[i],9} {ms[i],9:0.000} {litPct,9:0.0}");
    }

    // Four rungs rather than two, for the reason the build split gives: at the
    // bottom the whole quantity is the clear plus a few thousand stores, and a
    // two-point slope there is drawn through the host's noise. It is an
    // extrapolation and is printed beside the rung it starts from.
    int fit = 4;
    double mx = 0, my = 0;
    for (int i = 0; i < fit; i++)
    {
        mx += ladder[i];
        my += ms[i];
    }
    mx /= fit;
    my /= fit;
    double sxy = 0, sxx = 0;
    for (int i = 0; i < fit; i++)
    {
        sxy += (ladder[i] - mx) * (ms[i] - my);
        sxx += (ladder[i] - mx) * (ladder[i] - mx);
    }
    double clear = my - sxy / sxx * mx;
    double top = ms[^1];

    Console.WriteLine();
    Console.WriteLine($"  {$"clear, w*h-bound (bottom {fit} rungs fitted back to n = 0)",-54} : {clear,6:0.000} ms");
    Console.WriteLine($"  {$"plot at n = {ladder[^1]}",-54} : {top,6:0.000} ms");
    Console.WriteLine(
        $"  {"raster there",-54} : {top - clear,6:0.000} ms"
        + $"  ({(top - clear) / top * 100:0.0}% of the plot)"
    );
    Console.WriteLine();

    // The state of bank OUT is the whole measurement on the raster side, so it
    // is a parameter rather than an accident of call order, exactly as the
    // build's input state is. Ordered and id-order are the same three banks in
    // two orders; settled is a different scene, and the lit-pixel share is what
    // says so without anybody having to look at it.
    Console.WriteLine($"OUT state at n = {N}, at each of the two rmax values the committed scenes pin");
    Console.WriteLine($"{"rmax",9} {"g",4} {"state",10} {"plot ms",9} {"lit px %",9}");
    Console.WriteLine(new string('-', 45));

    foreach (float rmax in new[] { Headline, Dense })
    {
        foreach (
            (string label, PlotState state, int steps) in new[]
            {
                ("ordered", PlotState.Ordered, 0),
                ("id-order", PlotState.IdOrder, 0),
                ("settled", PlotState.Settled, Warmup),
            }
        )
        {
            (double plotMs, double litPct) = TimePlot(N, rmax, state, W, H, steps);
            Console.WriteLine(
                $"{rmax,9:0.000000} {GridDim(rmax),4} {label,10} {plotMs,9:0.000} {litPct,9:0.0}"
            );
        }
    }

    Console.WriteLine();
    Console.WriteLine("rmax     = the two values presets/headline.txt and presets/dense.txt pin. ONLY rmax is taken");
    Console.WriteLine("           from them: the seed, species count, matrix and force scale are this harness's own");
    Console.WriteLine("           standard set, as in every other section here, so no row is a committed scene.");
    Console.WriteLine("ordered  = build then pass, so OUT sits at cell-ordered indices: every frame after the first.");
    Console.WriteLine("id-order = the initial draw, never built: the first frame only.");
    Console.WriteLine($"settled  = swarm_step run {Warmup} times first, the warm-up decision 11's acceptance discards.");
    Console.WriteLine("lit px % = share of the framebuffer left non-background, so a clustered scene reads as a low one.");
    Console.WriteLine("min-of-rounds, as every figure in this harness; the host was not quiesced unless said so.");
}

// One swarm_plot into a caller-owned w*h framebuffer, plus the share of that
// buffer the raster left non-background. The lit share is measured rather than
// described because it is what separates two rows that differ only in where the
// particles are: 1M single pixels thrown at 1M pixels light about 63% of them
// when the draw is uniform, and progressively fewer as the scene clusters and
// the stores fall repeatedly on the same lines.
static unsafe (double Ms, double LitPct) TimePlot(
    uint n,
    float rmax,
    PlotState state,
    uint w,
    uint h,
    int steps = 0
)
{
    SwarmParams p = MakeGridParams(n, rmax);
    ulong bytes = Native.swarm_layout_bytes(in p);
    if (bytes == 0)
        throw new InvalidOperationException($"layout rejected n={n} rmax={rmax}");

    // PLOT_BG in src/kernel/plot.inc, mirrored the way PlotTests.cs mirrors it;
    // that suite asserts an untouched pixel equals this value, so a drift here
    // reds a test rather than quietly moving a number in this table.
    const uint PlotBackground = 0x001A1A22;

    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    uint* fb = (uint*)NativeMemory.AlignedAlloc((nuint)w * h * sizeof(uint), 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException($"init failed n={n} rmax={rmax}");

        switch (state)
        {
            case PlotState.Ordered:
                Native.swarm_build(arena); // IN becomes cell-ordered ...
                Native.swarm_pass(arena, 0, n); // ... and the pass carries that order into OUT
                break;
            case PlotState.IdOrder:
                break; // the initial draw, never built
            case PlotState.Settled:
                Native.swarm_step(arena, (uint)steps);
                break;
        }

        for (int i = 0; i < 3; i++)
            Native.swarm_plot(arena, fb, w, h);
        double best = MinOfRounds(() => Native.swarm_plot(arena, fb, w, h));

        // Counted off the buffer the timed calls left behind, so it describes
        // the raster that was measured and not a second one taken for the count.
        long lit = 0;
        for (long i = 0; i < (long)w * h; i++)
            if (fb[i] != PlotBackground)
                lit++;

        return (best, lit * 100.0 / ((double)w * h));
    }
    finally
    {
        NativeMemory.AlignedFree(fb);
        NativeMemory.AlignedFree(arena);
    }
}

// --- the committed headline scene at the seam (#125) ------------------------
//
// Every seam figure in docs/BENCHMARKS.md is taken on this harness's own params
// - 6 species, seed 0x5EED, a deterministic sin-filled matrix - and on a bank
// that has never been stepped. The live capture rows are taken on
// presets/headline.txt over 3600 consecutive frames from process start. Those
// are two different worlds, and the document says so: it bounds the plot, reads
// the threaded seam frame as 11.894 ms worst-run, and then records a worst p99
// of 150.849 ms for the live frame while naming "whatever the live pass costs
// on an evolved scene" as unattributed.
//
// This measures the seam on the committed scene's own params, along the settle
// depth the live run actually walks: the uniform opening field, decision 11's
// 600 discarded warm-up frames, and on to 3600, which is the length of a
// capture. One arena per run carries the whole ladder, so a row is the same
// world the row above it left behind rather than a fresh scene stepped further.
//
// The candidate count is carried beside every timing because it is the
// mechanism a clustering scene would work through: the force loop walks the 3x3
// cell neighbourhood, so the same n costs whatever the occupancy of those nine
// cells is. A count that did not move would rule that mechanism out instead of
// leaving it as a story told about the timings.
//
// The settle uses swarm_step_mt, whose contract states it is bit-identical to
// swarm_step for any T, so the scene a row measures is the scene the serial
// stepper would have reached. Nothing is timed while the pool is up except the
// two mt columns, and the pool is shut down around every serial figure, because
// idle workers are not free.
//
// The blit is NOT here and cannot be. BitBlt is platform code the seam does not
// reach, so this mode covers three of the four phases decision 11 asks for and
// says nothing about the fourth.
static unsafe void SceneFrame()
{
    Console.WriteLine();
    Console.WriteLine("The committed headline scene at the seam (#125): the frame along the settle depth");
    Console.WriteLine();

    int[] depths = [0, 600, 1200, 1800, 2400, 3000, 3600];
    const uint FrameW = 1024,
        FrameH = 1024; // src/swarm.asm FRAME_W / FRAME_H

    SwarmParams p = HeadlinePreset();
    int g = GridDim(p.RMax);
    if (Native.swarm_layout_bytes(in p) == 0)
        throw new InvalidOperationException("layout rejected the committed headline scene");

    Console.WriteLine(
        $"presets/headline.txt: n = {p.N}, species {p.SpeciesN}, rmax = {p.RMax:0.000000}, "
        + $"g = {g}, seed 0x{p.Seed:X16}"
    );
    Console.WriteLine(
        $"framebuffer {FrameW}x{FrameH}, FLAG_GRID, force_path = {p.ForcePath} (auto); "
        + $"600 is decision 11's warm-up and 3600 is one capture"
    );

    // WHAT A RUNG COSTS THE SCENE IT MEASURES, measured rather than reasoned
    // about. build_core is OUT -> IN and pass_core is IN -> OUT, so a rung's
    // timing rounds do not leave the world where they found it: they advance
    // it, and the ladder's later rungs therefore sit slightly past the label in
    // their first column. This control settles a FRESH arena to nearby depths
    // with nothing else touching it, so a reader can see which of them the
    // ladder's 600 rung reproduces and read the offset off the table instead of
    // taking a number on trust. The candidate count is the right probe for it:
    // it is a property of the state alone, with no clock in it.
    Console.WriteLine();
    Console.WriteLine("drift control: cand/p on a fresh arena advanced by swarm_step_mt and nothing else");
    foreach (int steps in new[] { 600, 601, 602, 603 })
        Console.WriteLine($"  {steps,4} steps : cand/p = {SettleOnlyCandidates(p, g, steps),8:0.0}");

    for (int run = 1; run <= 3; run++)
    {
        Console.WriteLine();
        Console.WriteLine($"run {run}");
        Console.WriteLine(
            $"{"steps",6} {"cand/p",8} {"lit %",7} {"build ms",9} {"pass ms",10} {"plot ms",8} "
            + $"{"frame ms",9} {"mt build",9} {"mt pass",9} {"mt frame",9} {"fps",6} {"T",4}"
        );
        Console.WriteLine(new string('-', 110));
        SceneLadder(p, g, depths, FrameW, FrameH);
    }

    Console.WriteLine();
    Console.WriteLine("steps = swarm_step_mt calls the arena has taken; 0 is the field swarm_init leaves.");
    Console.WriteLine("cand/p = mean candidates per particle over the 3x3 cell neighbourhood the force loop walks.");
    Console.WriteLine("lit % = share of the framebuffer the raster left non-background, off the timed buffer.");
    Console.WriteLine("mt columns run on the pool at its auto-detected physical-core count, printed as T.");
    Console.WriteLine("ms = best of 9 rounds, as every figure in this harness.");
    Console.WriteLine("frame = build + pass + plot. THE BLIT IS NOT IN IT; the seam does not reach it.");
}

// One arena walked up the ladder, printing a row per depth as it goes, because
// a run takes minutes and a reader watching it should not have to wait for the
// last rung to see the first.
//
// The rounds are not neutral and the control above is what prices that. Repeated
// passes over one IN bank all produce the same OUT, so a round adds nothing to
// the round beside it; what does move the world is the pair, because build_core
// is OUT -> IN and pass_core is IN -> OUT. Each rung therefore leaves the scene
// a small fixed number of steps further on than it found it, and the label in
// the first column is the settle alone.
static unsafe void SceneLadder(SwarmParams p, int g, int[] depths, uint w, uint h)
{
    const uint PlotBackground = 0x001A1A22; // PLOT_BG, src/kernel/plot.inc

    ulong bytes = Native.swarm_layout_bytes(in p);
    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    uint* fb = (uint*)NativeMemory.AlignedAlloc((nuint)w * h * sizeof(uint), 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException("init failed on the committed headline scene");

        int taken = 0;
        foreach (int depth in depths)
        {
            if (depth > taken)
            {
                if (Native.swarm_pool_init(0) < 1)
                    throw new InvalidOperationException("pool_init(0) failed for the settle");
                try
                {
                    Native.swarm_step_mt(arena, (uint)(depth - taken));
                }
                finally
                {
                    Native.swarm_pool_shutdown();
                }
                taken = depth;
            }

            double candidates = MeanCandidatesPerParticle(arena, p.N, g);

            Native.swarm_build(arena);
            for (int i = 0; i < 3; i++)
                Native.swarm_pass(arena, 0, p.N);
            double passMs = MinOfRounds(() => Native.swarm_pass(arena, 0, p.N));

            for (int i = 0; i < 3; i++)
                Native.swarm_build(arena);
            double buildMs = MinOfRounds(() => Native.swarm_build(arena));

            for (int i = 0; i < 3; i++)
                Native.swarm_plot(arena, fb, w, h);
            double plotMs = MinOfRounds(() => Native.swarm_plot(arena, fb, w, h));

            // Counted off the buffer the timed calls left behind, so it
            // describes the raster that was measured and not a second one taken
            // for the count.
            long lit = 0;
            for (long i = 0; i < (long)w * h; i++)
                if (fb[i] != PlotBackground)
                    lit++;

            int workers = Native.swarm_pool_init(0);
            if (workers < 1)
                throw new InvalidOperationException("pool_init(0) failed");
            double buildMtMs,
                passMtMs;
            try
            {
                Native.swarm_build_mt(arena);
                for (int i = 0; i < 3; i++)
                    Native.swarm_pass_mt(arena);
                passMtMs = MinOfRounds(() => Native.swarm_pass_mt(arena));

                for (int i = 0; i < 3; i++)
                    Native.swarm_build_mt(arena);
                buildMtMs = MinOfRounds(() => Native.swarm_build_mt(arena));
            }
            finally
            {
                Native.swarm_pool_shutdown();
            }

            double frame = buildMs + passMs + plotMs;
            double mtFrame = buildMtMs + passMtMs + plotMs;
            Console.WriteLine(
                $"{depth,6} {candidates,8:0.0} {lit * 100.0 / ((double)w * h),7:0.0} "
                + $"{buildMs,9:0.000} {passMs,10:0.000} {plotMs,8:0.000} {frame,9:0.000} "
                + $"{buildMtMs,9:0.000} {passMtMs,9:0.000} {mtFrame,9:0.000} "
                + $"{1000.0 / mtFrame,6:0.0} {workers,4}"
            );
        }
    }
    finally
    {
        NativeMemory.AlignedFree(fb);
        NativeMemory.AlignedFree(arena);
    }
}

// A fresh arena advanced and then read, with no timed work anywhere near it.
// This is the control the ladder is compared against; it exists to price the
// ladder's own footprint, so it must not carry one of its own.
static unsafe double SettleOnlyCandidates(SwarmParams p, int g, int steps)
{
    ulong bytes = Native.swarm_layout_bytes(in p);
    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException("init failed on the committed headline scene");
        if (Native.swarm_pool_init(0) < 1)
            throw new InvalidOperationException("pool_init(0) failed for the control settle");
        try
        {
            Native.swarm_step_mt(arena, (uint)steps);
        }
        finally
        {
            Native.swarm_pool_shutdown();
        }
        return MeanCandidatesPerParticle(arena, p.N, g);
    }
    finally
    {
        NativeMemory.AlignedFree(arena);
    }
}

// presets/headline.txt, field for field. Typed out rather than parsed, because
// the parser is kernel code reached through the exe and this project talks to
// the DLL; a reader checks these nine values against the file. FLAG_GRID is
// what the exe applies to a preset (decision 10), so it is here too, and
// force_path stays 0 because the file names no path.
static SwarmParams HeadlinePreset()
{
    var p = new SwarmParams
    {
        Version = 1,
        N = 1_048_576,
        SpeciesN = 4,
        Seed = 0x9E3779B97F4A7C15,
        RMax = 0.001953f,
        Beta = 0.3f,
        Dt = 0.02f,
        Friction = 0.71f,
        ForceScale = 10f,
        ForcePath = 0,
        Flags = 1, // FLAG_GRID
    };
    float[] m =
    [
        0.5f, -0.2f, 0.3f, -0.5f,
        -0.3f, 0.6f, -0.4f, 0.2f,
        0.2f, 0.3f, -0.6f, 0.4f,
        -0.4f, 0.1f, 0.5f, 0.3f,
    ];
    for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
            p.Matrix[r * 8 + c] = m[r * 4 + c];
    return p;
}

// --- does "500k @ 60 fps reached" survive the settle (#5) --------------------
//
// The M3 pool section reads its own table as 500,000 particles clearing 60 fps,
// and the README's third acceptance clause on #5 is that every claim it makes is
// reproducible from the repository alone. The 1M ladder above gives a reason to
// re-examine the ground that reading stands on rather than the arithmetic in it.
//
// The reading takes the threaded pass off an unstepped bank and the build off
// the settled distribution, in the same sentence. Each half is the cheaper of
// the two states available to it, and no single frame is ever in both: a frame
// whose build is cheap because the scene has clustered has a pass that has
// clustered too. The 1M ladder measures what that costs there. This asks the
// same question at the count and the params the 500k claim is made on.
//
// Same ladder, same instrument, same disclosure. The scene is this harness's own
// - 6 species, seed 0x5EED, the sin-filled matrix, rmax = 1/512 - because that
// is what the row being examined was taken on, and a different scene would
// answer a different question.
//
// It runs at BOTH counts on that one scene, and the second count is what keeps
// the answer honest. The 1M ladder above moved the count and the params
// together, so by itself it cannot say which of the two the clustering follows.
// 1M here holds the params still and moves only the count; against the ladder
// above it holds the count still and moves only the params.
static unsafe void M3Settle()
{
    Console.WriteLine();
    Console.WriteLine("The harness scene along its settle depth, at both counts (#5)");
    Console.WriteLine();

    const float RMax = 1f / 512f;
    int[] depths = [0, 600, 1200, 1800, 2400, 3000, 3600];
    const uint FrameW = 1024,
        FrameH = 1024;

    // Both counts, on ONE set of params. The 1M ladder above changed the count
    // and the scene at the same time, so on its own it cannot say which of the
    // two the clustering follows. Holding the params fixed and moving only the
    // count answers that half; the other half is that ladder against the 1M row
    // here, which moves only the scene.
    Console.WriteLine("the row under examination: T = 16, pass 4.979 ms, frame 15.487 ms, 64.6 fps");

    foreach (uint n in new uint[] { 500_000, 1_048_576 })
    {
        SwarmParams p = MakeGridParams(n, RMax);
        p.ForcePath = 0; // auto, as a shipped run would resolve it
        int g = GridDim(RMax);
        if (Native.swarm_layout_bytes(in p) == 0)
            throw new InvalidOperationException($"layout rejected n={n} rmax={RMax}");

        Console.WriteLine();
        Console.WriteLine(
            $"harness scene: n = {p.N}, species {p.SpeciesN}, rmax = 1/512, g = {g}, "
            + $"seed 0x{p.Seed:X4}, sin-filled matrix"
        );

        for (int run = 1; run <= 3; run++)
        {
            Console.WriteLine();
            Console.WriteLine($"n = {p.N}, run {run}");
            Console.WriteLine(
                $"{"steps",6} {"cand/p",8} {"lit %",7} {"build ms",9} {"pass ms",10} {"plot ms",8} "
                + $"{"frame ms",9} {"mt build",9} {"mt pass",9} {"mt frame",9} {"fps",6} {"T",4}"
            );
            Console.WriteLine(new string('-', 110));
            SceneLadder(p, g, depths, FrameW, FrameH);
        }
    }

    Console.WriteLine();
    Console.WriteLine("Columns and caveats are the 1M ladder's; the rung offset it prices applies here too.");
    Console.WriteLine("frame = build + pass + plot. THE BLIT IS NOT IN IT; the seam does not reach it.");
}

// --- the managed baseline of the competitor comparison (#153) ---------------

// The competitor set #5 names is two foreign engines plus a naive idiomatic C#
// port as the managed baseline. This section is that baseline's half of it,
// and it is deliberately the half that can be taken here: it needs no foreign
// toolchain, no third-party build, and no change to the machine every
// published number in this repository is taken on.
//
// EVERY COLUMN COMES OUT OF ONE RUN ON ONE HOST. That is the whole reason the
// managed port sits inside this harness instead of in a project of its own. A
// managed figure taken in one process and an assembly figure carried in from
// another report would be two measurements of two machine states presented as
// a comparison, which is the failure this section exists to avoid.
//
// The instrument is the one the kernel rows use, min-of-nine over a pass
// against frozen input, so the two sides are not merely on one host but on one
// clock. The managed side is warmed first because tiered compilation would
// otherwise time the interpreter's opinion of the loop; the reported figure is
// a minimum over nine rounds, so a round that ran before promotion cannot be
// the one quoted.
//
// The scene is MakeParams, the same one the default report's brute-force table
// uses, read out of it rather than restated, so the two tables cannot drift
// apart into a comparison of two different scenes.
static void ManagedReport()
{
    int[] ns = [1024, 2048, 4096, 8192, 16384];
    const uint Scalar = 3, Avx2 = 1;

    int paths = Native.swarm_cpu_paths();
    bool haveAvx2 = (paths & 1) != 0;

    Console.WriteLine("The managed baseline (#153): plain C# against the kernel, same scene, same run");
    Console.WriteLine();
    Console.WriteLine(
        $"{"n",9} {"C# SoA ms",12} {"C# AoS ms",12} {"scalar ms",12} {"avx2 ms",12} " +
        $"{"C# Mp/s",10} {"avx2 / C#",10}");
    Console.WriteLine(new string('-', 84));

    foreach (int n in ns)
    {
        ManagedBaseline.Scene scene = ManagedScene(n);

        var soa = new ManagedBaseline.Soa(scene);
        for (int i = 0; i < 3; i++)
            soa.Pass();
        double soaMs = MinOfRounds(soa.Pass);

        var aos = new ManagedBaseline.Aos(scene);
        for (int i = 0; i < 3; i++)
            aos.Pass();
        double aosMs = MinOfRounds(aos.Pass);

        double scalarMs = TimePass((uint)n, Scalar);
        double avx2Ms = haveAvx2 ? TimePass((uint)n, Avx2) : double.NaN;

        double pairs = (double)n * n;
        double soaMp = pairs / (soaMs * 1e3);
        string avxCol = haveAvx2 ? avx2Ms.ToString("0.000") : "n/a";
        string ratio = haveAvx2 ? $"{soaMs / avx2Ms:0.00}x" : "n/a";

        Console.WriteLine(
            $"{n,9} {soaMs,12:0.000} {aosMs,12:0.000} {scalarMs,12:0.000} {avxCol,12} " +
            $"{soaMp,10:0.0} {ratio,10}");
    }

    Console.WriteLine();
    Console.WriteLine("C# SoA = one float[] per field; C# AoS = one struct per particle. The comparison");
    Console.WriteLine("quotes the FASTER of the two, so the baseline is a floor on plain managed code");
    Console.WriteLine("rather than a strawman. Neither uses Vector<T> or the intrinsics - that would");
    Console.WriteLine("compare two vectorisations, not this engine against managed code.");
    Console.WriteLine();
    Console.WriteLine($"runtime: {Environment.Version}, server GC={System.Runtime.GCSettings.IsServerGC}, " +
        $"tiered PGO={Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default"}");
    Console.WriteLine("Per-machine, as every row here is - record in docs/BENCHMARKS.md with the host.");
}

// The managed side's scene, lifted out of MakeParams so one edit moves both
// sides. The matrix is copied out of the inline array because the managed port
// has no reason to carry the ABI's fixed-64 shape.
static ManagedBaseline.Scene ManagedScene(int n)
{
    SwarmParams p = MakeParams((uint)n, 3);
    var matrix = new float[64];
    for (int i = 0; i < 64; i++)
        matrix[i] = p.Matrix[i];

    return new ManagedBaseline.Scene(
        n, (int)p.SpeciesN, p.Seed, p.RMax, p.Beta, p.Dt, p.Friction, p.ForceScale, matrix);
}

// --- the README assets (#131) ----------------------------------------------

// One still and one short loop of the scene the shipped executable runs, both
// rendered by the kernel rather than captured off a screen. `swarm_plot` fills
// a BGRA framebuffer from bank OUT, so the same params give the same pixels on
// any machine that has the path the scene names, and the caption beside the
// picture can therefore say what produced it.
//
// WHY THE ENCODERS ARE HERE AND NOT IN A PACKAGE. The raster's whole colour
// range is the background plus the eight-entry species palette in
// src/kernel/plot.inc, nine colours by construction, so the expensive half of
// a GIF encoder - reducing an arbitrary image to 256 colours - is work this
// picture does not need. What is left is a global colour table copied straight
// out of that palette and an LZW coder. PNG is the same story: ZLibStream in
// System.IO.Compression is the compressor, and the chunk framing around it is
// a header, a CRC and one filter byte per row. Neither adds a dependency to a
// repository whose pitch is one executable and none.
static unsafe void ReadmeAssets()
{
    const uint StillW = 1024, StillH = 1024; // FRAME_W/FRAME_H, src/swarm.asm
    const uint LoopW = 384, LoopH = 384;
    const uint Warm = 600; // steps before either asset is taken
    const int LoopFrames = 72;
    const uint StepsPerFrame = 2;
    const int DelayCs = 4; // hundredths of a second between frames

    string root = RepoRoot();
    string dir = Path.Combine(root, "docs", "media");
    Directory.CreateDirectory(dir);

    SwarmParams p = ShippedScene();
    ulong bytes = Native.swarm_layout_bytes(in p);
    if (bytes == 0)
        throw new InvalidOperationException("layout rejected the shipped scene");

    Console.WriteLine("swarm.asm README assets (#131)");
    Console.WriteLine(
        $"  scene   : n={p.N}, {p.SpeciesN} species, seed 0x{p.Seed:X}, rmax={p.RMax}, "
            + $"force_path={p.ForcePath}, flags=0x{p.Flags:X}");
    Console.WriteLine($"  warm-up : {Warm} steps");
    Console.WriteLine();

    void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
    try
    {
        if (Native.swarm_init(arena, bytes, in p) != 0)
            throw new InvalidOperationException("init failed on the shipped scene");
        Native.swarm_step(arena, Warm);

        uint* fb = (uint*)NativeMemory.AlignedAlloc((nuint)StillW * StillH * sizeof(uint), 64);
        try
        {
            Native.swarm_plot(arena, fb, StillW, StillH);
            byte[] png = EncodePng(fb, StillW, StillH);
            string stillPath = Path.Combine(dir, "swarm-still.png");
            File.WriteAllBytes(stillPath, png);
            Console.WriteLine(
                $"  {Rel(root, stillPath)}  {StillW}x{StillH}  {png.Length} bytes");
        }
        finally
        {
            NativeMemory.AlignedFree(fb);
        }

        uint* lfb = (uint*)NativeMemory.AlignedAlloc((nuint)LoopW * LoopH * sizeof(uint), 64);
        try
        {
            var frames = new byte[LoopFrames][];
            for (int i = 0; i < LoopFrames; i++)
            {
                Native.swarm_plot(arena, lfb, LoopW, LoopH);
                frames[i] = IndexFrame(lfb, LoopW, LoopH);
                Native.swarm_step(arena, StepsPerFrame);
            }
            byte[] gif = EncodeGif(frames, LoopW, LoopH, DelayCs);
            string loopPath = Path.Combine(dir, "swarm-loop.gif");
            File.WriteAllBytes(loopPath, gif);
            Console.WriteLine(
                $"  {Rel(root, loopPath)}  {LoopW}x{LoopH}  {LoopFrames} frames  "
                    + $"{DelayCs}cs  {StepsPerFrame} steps/frame  {gif.Length} bytes");
        }
        finally
        {
            NativeMemory.AlignedFree(lfb);
        }
    }
    finally
    {
        NativeMemory.AlignedFree(arena);
    }
}

static string Rel(string root, string path) =>
    Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

// The default preset assembled into swarm.exe (src/swarm.asm, `sim_params`),
// field for field, except for two the picture has to pin. force_path is AVX2
// where the image leaves it on auto, because auto resolves per host and an
// asset committed to the repository has to name the path that produced it;
// PATH_AVX2 is the baseline every supported machine carries. FLAG_SPLAT is set
// where the image leaves it to the `-splat` toggle, because 8,192 single
// pixels in a million-pixel frame survive neither a README's display width nor
// a reader's screen. Both are stated in the caption beside the asset.
static SwarmParams ShippedScene()
{
    var p = new SwarmParams
    {
        Version = 1,
        N = 8192,
        SpeciesN = 4,
        Seed = 0x9E3779B97F4A7C15,
        RMax = 0.05f,
        Beta = 0.3f,
        Dt = 0.02f,
        Friction = 0.71f,
        ForceScale = 10f,
        ForcePath = 1, // PATH_AVX2, where the image says 0 (auto)
        Flags = 1 | 2, // FLAG_GRID | FLAG_SPLAT
    };
    float[] m =
    [
        0.5f, -0.2f, 0.3f, -0.5f,
        -0.3f, 0.6f, -0.4f, 0.2f,
        0.2f, 0.3f, -0.6f, 0.4f,
        -0.4f, 0.1f, 0.5f, 0.3f,
    ];
    for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
            p.Matrix[r * 8 + c] = m[r * 4 + c];
    return p;
}

// One byte per pixel, the index into Asset.Colours. Fail-closed: a colour the
// palette does not hold means the buffer did not come from swarm_plot, and
// guessing a nearest match would hide exactly that.
static unsafe byte[] IndexFrame(uint* bgra, uint w, uint h)
{
    var ix = new byte[(long)w * h];
    for (long i = 0; i < ix.LongLength; i++)
    {
        uint c = bgra[i] & 0x00FFFFFF;
        int k = Array.IndexOf(Asset.Colours, c);
        if (k < 0)
            throw new InvalidOperationException(
                $"pixel {i} is 0x{c:X6}, outside the plot palette");
        ix[i] = (byte)k;
    }
    return ix;
}

// --- PNG (RFC 2083, colour type 2, filter type 0) ---------------------------

static unsafe byte[] EncodePng(uint* bgra, uint w, uint h)
{
    // Filter byte + one RGB triple per pixel, per row. Filter 0 (None) is the
    // honest choice for this image: the raster is isolated pixels on a flat
    // background, so a predictor has nothing to predict and deflate handles
    // the background runs.
    var raw = new byte[(long)h * (1 + (long)w * 3)];
    long o = 0;
    for (uint y = 0; y < h; y++)
    {
        raw[o++] = 0;
        for (uint x = 0; x < w; x++)
        {
            uint c = bgra[(long)y * w + x];
            raw[o++] = (byte)(c >> 16);
            raw[o++] = (byte)(c >> 8);
            raw[o++] = (byte)c;
        }
    }

    byte[] idat;
    using (var ms = new MemoryStream())
    {
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        idat = ms.ToArray();
    }

    var png = new MemoryStream();
    png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    var ihdr = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), w);
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), h);
    ihdr[8] = 8; // bit depth
    ihdr[9] = 2; // colour type: truecolour
    PngChunk(png, "IHDR", ihdr);
    PngChunk(png, "IDAT", idat);
    PngChunk(png, "IEND", []);
    return png.ToArray();
}

static void PngChunk(Stream s, string type, byte[] data)
{
    Span<byte> len = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
    s.Write(len);
    byte[] tag = System.Text.Encoding.ASCII.GetBytes(type);
    s.Write(tag);
    s.Write(data);
    Span<byte> crc = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(tag, data));
    s.Write(crc);
}

static uint Crc32(byte[] a, byte[] b)
{
    uint[] table = Asset.Crc32Table;
    uint c = 0xFFFFFFFF;
    foreach (byte x in a)
        c = table[(c ^ x) & 0xFF] ^ (c >> 8);
    foreach (byte x in b)
        c = table[(c ^ x) & 0xFF] ^ (c >> 8);
    return c ^ 0xFFFFFFFF;
}

// --- GIF89a -----------------------------------------------------------------

// A looping animation over a 16-entry global colour table. The table is
// Asset.Colours padded with black: GIF sizes its table by a power of two, and
// nine colours round up to sixteen. Padding entries are never indexed, which
// the frame indexer guarantees by refusing any colour outside the palette.
static byte[] EncodeGif(byte[][] frames, uint w, uint h, int delayCs)
{
    var g = new MemoryStream();
    g.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
    WriteLe16(g, (ushort)w);
    WriteLe16(g, (ushort)h);
    g.WriteByte(0xF3); // global table present, 8-bit source, 2^(3+1) entries
    g.WriteByte(0x00); // background colour index
    g.WriteByte(0x00); // no pixel aspect ratio
    for (int i = 0; i < 16; i++)
    {
        uint c = i < Asset.Colours.Length ? Asset.Colours[i] : 0u;
        g.WriteByte((byte)(c >> 16));
        g.WriteByte((byte)(c >> 8));
        g.WriteByte((byte)c);
    }

    // NETSCAPE2.0 application extension: loop forever. Without it a viewer
    // plays the animation once, which for a README asset is a defect.
    g.Write([0x21, 0xFF, 0x0B]);
    g.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
    g.Write([0x03, 0x01]);
    WriteLe16(g, 0); // 0 = forever
    g.WriteByte(0x00);

    foreach (byte[] frame in frames)
    {
        g.Write([0x21, 0xF9, 0x04, 0x04]); // graphic control, disposal 1, opaque
        WriteLe16(g, (ushort)delayCs);
        g.Write([0x00, 0x00]);
        g.WriteByte(0x2C); // image descriptor
        WriteLe16(g, 0);
        WriteLe16(g, 0);
        WriteLe16(g, (ushort)w);
        WriteLe16(g, (ushort)h);
        g.WriteByte(0x00); // no local table, not interlaced
        LzwCompress(g, frame, 4);
    }

    g.WriteByte(0x3B); // trailer
    return g.ToArray();
}

static void WriteLe16(Stream s, ushort v)
{
    s.WriteByte((byte)v);
    s.WriteByte((byte)(v >> 8));
}

// GIF's variable-width LZW, emitted into sub-blocks of at most 255 bytes. The
// dictionary is reset with a clear code when it fills, which keeps the code
// width inside the 12 bits the format allows.
static void LzwCompress(Stream s, byte[] indices, int minCodeSize)
{
    int clear = 1 << minCodeSize;
    int eoi = clear + 1;
    int codeSize = minCodeSize + 1;
    int next = eoi + 1;
    var dict = new Dictionary<(int Prefix, byte Suffix), int>();

    // The image data opens with the initial code width, before the first
    // sub-block; a decoder reads this byte as the width of everything after it.
    s.WriteByte((byte)minCodeSize);

    var block = new byte[255];
    int blockLen = 0;
    int bitBuf = 0,
        bitCount = 0;

    void Flush()
    {
        if (blockLen == 0)
            return;
        s.WriteByte((byte)blockLen);
        s.Write(block, 0, blockLen);
        blockLen = 0;
    }

    void Emit(int code)
    {
        bitBuf |= code << bitCount;
        bitCount += codeSize;
        while (bitCount >= 8)
        {
            block[blockLen++] = (byte)bitBuf;
            bitBuf >>= 8;
            bitCount -= 8;
            if (blockLen == 255)
                Flush();
        }
    }

    Emit(clear);
    int prefix = indices[0];
    for (int i = 1; i < indices.Length; i++)
    {
        byte k = indices[i];
        if (dict.TryGetValue((prefix, k), out int found))
        {
            prefix = found;
            continue;
        }
        Emit(prefix);
        // The width is checked BEFORE this entry is added, not after, because
        // the decoder builds the same table one code behind the encoder: it
        // learns an entry from the code that follows the one that created it.
        // Growing on the encoder's own count runs a code ahead of the reader
        // and every code after it is misread.
        if (next > (1 << codeSize) - 1 && codeSize < 12)
            codeSize++;
        if (next < 1 << 12)
        {
            dict[(prefix, k)] = next++;
        }
        else
        {
            // The table is full: reset both sides at the current width, which
            // is what the clear code tells the decoder to do.
            Emit(clear);
            dict.Clear();
            codeSize = minCodeSize + 1;
            next = eoi + 1;
        }
        prefix = k;
    }
    Emit(prefix);
    Emit(eoi);
    if (bitCount > 0)
    {
        block[blockLen++] = (byte)bitBuf;
        if (blockLen == 255)
            Flush();
    }
    Flush();
    s.WriteByte(0x00); // block terminator
}

// --- native surface + the ABI-mirrored params struct -----------------------

// Which bank OUT the plot is timed over. Named rather than boolean because
// there are three of them and two are not each other's negation.
internal enum PlotState
{
    Ordered,
    IdOrder,
    Settled,
}

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

    // Rasterizes bank OUT into a w*h BGRA buffer (decision 9). The caller owns
    // the pixels, so the buffer is a parameter and not part of the arena.
    [DllImport("swarm.kernel.dll")]
    internal static extern void swarm_plot(void* arena, uint* bgra, uint w, uint h);

    // M3 worker pool (issue #68). swarm_pool_init(0) auto-detects physical cores
    // and returns the actual worker count; swarm_pass_mt fans the pass over the
    // pool; swarm_pool_shutdown joins and closes the threads.
    [DllImport("swarm.kernel.dll")]
    internal static extern int swarm_pool_init(int requested);

    [DllImport("swarm.kernel.dll")]
    internal static extern void swarm_pass_mt(void* arena);

    [DllImport("swarm.kernel.dll")]
    internal static extern void swarm_build_mt(void* arena);

    // The threaded frame loop: parallel build, parallel pass, advance. Its
    // contract states it is bit-identical to swarm_step for any T, which is why
    // it may stand in for the serial stepper when a scene has to be advanced
    // thousands of frames before anything is timed.
    [DllImport("swarm.kernel.dll")]
    internal static extern void swarm_step_mt(void* arena, uint nSteps);

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

// The plot's whole colour range, and the CRC table the PNG chunks are stamped
// with. Both live on a type because a top-level program has statements and
// local functions and no fields, and Colours is read once per pixel.
internal static class Asset
{
    // PLOT_BG at index 0, then swarm_palette in its own order
    // (src/kernel/plot.inc). Index 0 is the GIF's background index too, so a
    // frame that is mostly background codes as one long run.
    internal static readonly uint[] Colours =
    [
        0x001A1A22,
        0x00FF4040,
        0x0040FF40,
        0x004080FF,
        0x00FFD040,
        0x00FF40FF,
        0x0040FFFF,
        0x00FF8020,
        0x00A060FF,
    ];

    internal static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
