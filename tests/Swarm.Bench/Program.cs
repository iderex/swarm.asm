using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

// A repo artifact is English: format every number with '.' as the decimal
// point regardless of the dev machine's locale, so the table is reproducible.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// swarm.asm — force-kernel micro-benchmark (see docs/BENCHMARKS.md).
//
// Measures one force+integrate pass (swarm_pass over the whole population) for
// the scalar reference path and the AVX2 gather path, across a range of
// particle counts. swarm_pass is the O(n^2) hot loop the SIMD path
// accelerates; timing it in isolation — build once, then repeat the pass over
// the frozen IN bank — keeps the measured work identical every iteration and
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
Console.WriteLine("ms = best (min) per-pass time over 9 rounds; per-machine — record in docs/BENCHMARKS.md.");

// --- M2 grid: build (counting sort) + 3x3 neighbourhood pass at scale -------
// The grid replaces the O(n^2) sweep with O(n*k). g = the largest power of two
// with 1/g >= rmax (clamped [4,512]); a small rmax gives a large g, so cells
// are sparse and k (neighbours per particle) is small — that is the regime the
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
return 0;

// --- helpers ---------------------------------------------------------------

// Best-of-rounds per-pass time in milliseconds. The minimum, not the mean:
// a force pass is a fixed amount of arithmetic, so the fastest observed round
// is the one least perturbed by scheduling and turbo transitions — the honest
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
}

// 1:1 mirror of the native SwarmParams seam struct (src/kernel/abi.inc):
// sequential, Pack=4, 304 bytes — Pack=4 places the u64 seed at offset 12,
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
