using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The M3 worker-pool determinism gate (issue #68). The force+integrate pass is
/// a pure map — OUT[i] = f(i, IN, cell_start, params) with IN and cell_start
/// frozen by the serial build before any worker runs and disjoint one-writer
/// ranges covering [0, n) — so the threaded result must be BIT-IDENTICAL to the
/// serial path for any thread count T, and equal across T. This is the machine-
/// checked form of <see cref="GridTests.GridPassSplitInvariance"/>, driving the
/// REAL asm pool at several T instead of decomposing on .NET threads.
///
/// The comparison is EXACT equality, never epsilon: it is the same code path,
/// just partitioned. A drift here is a real bug — most likely a missed
/// per-thread MXCSR pin (denormal FTZ/DAZ divergence) or a torn/false-shared
/// boundary write — not a floating-point reordering, so loosening to epsilon
/// would mask exactly what the gate exists to catch.
///
/// The pool is process-global mutable state, so this class is a
/// non-parallelised collection: its cases run one at a time, each doing its own
/// pool_init/pool_shutdown. Other test classes never touch the pool exports, so
/// they still run in parallel with this one.
/// </summary>
[Collection(PoolCollection.Name)]
public sealed unsafe class ThreadingTests
{
    private const uint FlagGrid = 1;

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_build(void* arena);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_pass(void* arena, uint first, uint last);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    // The M3 pool seam. swarm_pool_init(0) auto-detects physical cores and
    // returns the actual worker count; swarm_step_mt / swarm_pass_mt drive the
    // pool; swarm_pool_shutdown joins and closes the threads.
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_pool_init(int requested);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step_mt(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_pass_mt(void* arena);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_pool_shutdown();

    // The thread counts the gate sweeps: 1 (no worker threads), small powers,
    // and 0 (= auto: the machine's physical-core count, the production T).
    private static readonly int[] ThreadCounts = [1, 2, 4, 0];

    private const float Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f;

    private static SwarmParams Make(uint n, uint species, ulong seed, float rmax, uint forcePath, uint flags)
    {
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = species, Seed = seed,
            RMax = rmax, Beta = Beta, Dt = Dt, Friction = Friction, ForceScale = ForceScale,
            ForcePath = forcePath, Flags = flags,
        };
        for (uint a = 0; a < species; a++)
            for (uint b = 0; b < species; b++)
                p.Matrix[(int)(a * 8 + b)] = MathF.Sin(a * 3.1f + b * 1.7f); // in [-1, 1]
        return p;
    }

    // The full id-ordered state as a flat array, so Assert.Equal compares every
    // particle's x/y/vx/vy bit-for-bit.
    private static float[] ReadStateInto(void* arena, uint n)
    {
        var x = new float[n]; var y = new float[n];
        var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];
        swarm_read_state(arena, x, y, vx, vy, sp);
        var flat = new float[n * 4];
        for (uint i = 0; i < n; i++)
        {
            flat[i * 4 + 0] = x[i];
            flat[i * 4 + 1] = y[i];
            flat[i * 4 + 2] = vx[i];
            flat[i * 4 + 3] = vy[i];
        }
        return flat;
    }

    // Serial reference: swarm_step (build + pass(0, n)) on the main thread.
    private static float[] SerialStep(SwarmParams p, uint steps)
    {
        ulong size = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, size);
        void* a = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(a, size, in p));
            swarm_step(a, steps);
            return ReadStateInto(a, p.N);
        }
        finally { NativeMemory.AlignedFree(a); }
    }

    // Threaded: swarm_step_mt (serial build + parallel pass) at thread count t.
    private static float[] ThreadedStep(SwarmParams p, uint steps, int t)
    {
        Assert.True(swarm_pool_init(t) >= 1, $"pool_init({t}) failed");
        try
        {
            ulong size = swarm_layout_bytes(in p);
            void* a = NativeMemory.AlignedAlloc((nuint)size, 64);
            try
            {
                Assert.Equal(0, swarm_init(a, size, in p));
                swarm_step_mt(a, steps);
                return ReadStateInto(a, p.N);
            }
            finally { NativeMemory.AlignedFree(a); }
        }
        finally { swarm_pool_shutdown(); }
    }

    // The M3 gate: the threaded frame (swarm_step_mt) is bit-identical to the
    // serial frame (swarm_step) at every thread count, and therefore across
    // thread counts. Grid and brute; AVX2 and scalar; sizes that spread real
    // work across the static 16-aligned partition (interior boundaries land on
    // 64-byte lines, so no OUT array is false-shared).
    [Theory]
    [InlineData(5000u, 4u, 0x1234ul, 0.10f, 1u, FlagGrid)]   // AVX2 grid
    [InlineData(5000u, 4u, 0x1234ul, 0.10f, 3u, FlagGrid)]   // scalar grid
    [InlineData(20000u, 6u, 0xBEEFul, 0.05f, 1u, FlagGrid)]  // larger, g=16
    [InlineData(3000u, 4u, 0x0077ul, 0.08f, 1u, 0u)]         // brute, single run
    [InlineData(100000u, 6u, 0x5EEDul, 1f / 512f, 1u, FlagGrid)] // g=512, many chunks
    public void PassParallelMatchesSerial(uint n, uint species, ulong seed, float rmax, uint forcePath, uint flags)
    {
        _ = NativeKernel.Handle;
        const uint steps = 6;
        var p = Make(n, species, seed, rmax, forcePath, flags);

        float[] serial = SerialStep(p, steps);
        foreach (int t in ThreadCounts)
        {
            float[] threaded = ThreadedStep(p, steps, t);
            Assert.Equal(serial, threaded); // exact — same code path, partitioned
        }
    }

    // Tighter isolation of the pool's fan-out: the parallel pass over a frozen IN
    // bank (swarm_pass_mt) equals the serial pass (swarm_pass(0, n)) bit-for-bit,
    // independent of the step/build loop. Directly gates the partition arithmetic
    // (disjoint, covering [0, n)) and the per-thread MXCSR pin.
    [Theory]
    [InlineData(5000u, 4u, 0x1234ul, 0.10f, 1u, FlagGrid)]
    [InlineData(20000u, 6u, 0xBEEFul, 0.05f, 3u, FlagGrid)]
    [InlineData(50000u, 6u, 0x5EEDul, 1f / 512f, 1u, FlagGrid)]
    public void PassMtMatchesSerialPass(uint n, uint species, ulong seed, float rmax, uint forcePath, uint flags)
    {
        _ = NativeKernel.Handle;
        var p = Make(n, species, seed, rmax, forcePath, flags);
        ulong size = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, size);

        // Serial pass over the frozen IN bank.
        float[] serial;
        void* a = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(a, size, in p));
            swarm_build(a);
            swarm_pass(a, 0, n);
            serial = ReadStateInto(a, n);
        }
        finally { NativeMemory.AlignedFree(a); }

        foreach (int t in ThreadCounts)
        {
            Assert.True(swarm_pool_init(t) >= 1, $"pool_init({t}) failed");
            try
            {
                void* b = NativeMemory.AlignedAlloc((nuint)size, 64);
                try
                {
                    Assert.Equal(0, swarm_init(b, size, in p));
                    swarm_build(b);       // identical frozen IN bank
                    swarm_pass_mt(b);     // parallel pass over [0, n)
                    Assert.Equal(serial, ReadStateInto(b, n));
                }
                finally { NativeMemory.AlignedFree(b); }
            }
            finally { swarm_pool_shutdown(); }
        }
    }
}

/// <summary>
/// Serialises the pool tests: the worker pool is process-global mutable state,
/// so its cases must not run concurrently with each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PoolCollection
{
    public const string Name = "swarm-pool";
}
