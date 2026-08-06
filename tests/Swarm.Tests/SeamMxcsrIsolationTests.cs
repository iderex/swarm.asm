using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The seam isolates the caller's FP mode in both directions (#156). What the
/// caller set does not reach the engine, and what the caller set is exactly
/// what it gets back.
///
/// `docs/MASTERPLAN.md` decision 2 has claimed for a while that "the harness
/// scrambles MXCSR before a call and asserts an identical state hash plus a
/// restored caller MXCSR". That harness did not exist. This is it.
///
/// The scramble is installed by `swarm_call_under_mxcsr` rather than from here,
/// and that is not a convenience. A control word with the exception masks
/// cleared faults on the first inexact result, which nearly every floating
/// point operation produces, so managed code cannot run under one at all. The
/// native helper narrows the hostile window to the call itself.
///
/// The three cases are one claim with three faces. A seam that isolates
/// results but leaks the restore, or that isolates the serial path but not the
/// pooled one, is a single defect either way.
///
/// The pool exports are process-global mutable state, so this class joins the
/// same non-parallel collection <see cref="ThreadingTests"/> uses.
/// </summary>
[Collection(PoolCollection.Name)]
public sealed unsafe class SeamMxcsrIsolationTests
{
    private const uint FlagGrid = 1;

    /// <summary>
    /// The hostile word, field by field. FTZ (bit 15) and DAZ (bit 6) clear, so
    /// subnormals behave the way the engine's pin says they must not. Rounding
    /// (bits 14:13) set to 11, round-toward-zero. All six exception masks
    /// (bits 12:7) clear, so an escaped operation faults rather than returning
    /// a quiet answer. Nothing about it overlaps SEAM_MXCSR = 0x9FC0.
    /// </summary>
    private const uint Hostile = 0x6000;

    private const uint SeamMxcsr = 0x9FC0;

    [DllImport("swarm.kernel.dll")]
    private static extern uint swarm_call_under_mxcsr(
        uint word, nint target, out ulong ret, nint a1, nint a2, nint a3, nint a4);

    [DllImport("swarm.kernel.dll")]
    private static extern uint swarm_mxcsr();

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_pool_init(int requested);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_pool_shutdown();

    private static nint Export(string name) => NativeLibrary.GetExport(NativeKernel.Handle, name);

    /// <summary>
    /// Call an export with the hostile word installed and return the word it
    /// left behind. Unused trailing arguments are passed as zero and never
    /// reach the target, which reads only as many as its own contract names.
    /// </summary>
    private static uint UnderHostile(string export, out ulong ret, nint a1 = 0, nint a2 = 0, nint a3 = 0, nint a4 = 0) =>
        swarm_call_under_mxcsr(Hostile, Export(export), out ret, a1, a2, a3, a4);

    private static uint UnderHostile(string export, nint a1 = 0, nint a2 = 0, nint a3 = 0, nint a4 = 0) =>
        UnderHostile(export, out _, a1, a2, a3, a4);

    private static SwarmParams Params(uint n, uint flags)
    {
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = 4, Seed = 0xC0FFEEul,
            RMax = 0.05f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f,
            ForcePath = 0, Flags = flags,
        };
        for (uint a = 0; a < 4; a++)
            for (uint b = 0; b < 4; b++)
                p.Matrix[(int)(a * 8 + b)] = MathF.Sin(a * 3.1f + b * 1.7f);
        return p;
    }

    private static float[] ReadStateInto(void* arena, uint n)
    {
        var x = new float[n]; var y = new float[n];
        var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];
        Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, sp));
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

    /// <summary>
    /// The helper itself, before anything is concluded from it. A helper that
    /// silently failed to install the word would make every assertion below
    /// pass while measuring nothing, so it is checked against a target whose
    /// answer is known: `swarm_mxcsr` reports the word in force inside its own
    /// seam, which must be the pin and not the hostile word installed around
    /// the call, and the word handed back must be the hostile one.
    /// </summary>
    [Fact]
    public void TheScrambleReachesTheCallAndNotTheCore()
    {
        _ = NativeKernel.Handle;

        uint left = UnderHostile("swarm_mxcsr", out ulong seen);

        Assert.Equal(SeamMxcsr, (uint)seen);   // the core saw the pin
        Assert.Equal(Hostile, left);           // the caller got its word back

        // And the harness thread is not left holding it.
        Assert.Equal(SeamMxcsr, swarm_mxcsr());
        Assert.NotEqual(Hostile, SeamMxcsr);
    }

    /// <summary>
    /// Every seam export leaves the caller's control word byte for byte as it
    /// found it, asserted from a scrambled starting value rather than from the
    /// clean one a passing test could get for free.
    /// </summary>
    [Fact]
    public void CallerMxcsrIsRestoredExactly()
    {
        _ = NativeKernel.Handle;

        var p = Params(2048, FlagGrid);
        ulong size = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, size);

        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        var bgra = new uint[64 * 64];
        var preset = System.Text.Encoding.ASCII.GetBytes("n = 1024\n");
        var parsed = new SwarmParams();

        try
        {
            SwarmParams* pp = &p;          // locals are already fixed
            SwarmParams* outp = &parsed;
            fixed (uint* fb = bgra)
            fixed (byte* text = preset)
            {
                var left = new Dictionary<string, uint>
                {
                    ["swarm_layout_bytes"] = UnderHostile("swarm_layout_bytes", (nint)pp),
                    ["swarm_init"] = UnderHostile("swarm_init", out ulong init, (nint)arena, (nint)size, (nint)pp),
                    ["swarm_step"] = UnderHostile("swarm_step", (nint)arena, 2),
                    ["swarm_pass"] = UnderHostile("swarm_pass", (nint)arena, 0, (nint)p.N),
                    ["swarm_plot"] = UnderHostile("swarm_plot", (nint)arena, (nint)fb, 64, 64),
                    ["swarm_parse_preset"] = UnderHostile("swarm_parse_preset", (nint)text, preset.Length, (nint)outp),
                };

                Assert.Equal(0, (int)init); // the init above really ran, not just returned

                var leaked = left.Where(e => e.Value != Hostile)
                    .Select(e => $"{e.Key} left MXCSR at 0x{e.Value:X4}, not the caller's 0x{Hostile:X4}")
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();

                Assert.True(
                    leaked.Length == 0,
                    "a seam export did not put the caller's control word back. The seam saves it in "
                        + "seam_enter and restores it in seam_leave, so a leak here means an export that "
                        + "does not wear the frame, or a frame that lost its restore:\n  "
                        + string.Join("\n  ", leaked));
            }
        }
        finally { NativeMemory.AlignedFree(arena); }
    }

    /// <summary>
    /// Engine output does not depend on the caller's control word. Same params,
    /// same seed, one run entered from the hostile word and one from whatever
    /// the harness thread carries; the two states must be bit-identical, not
    /// close.
    /// </summary>
    [Theory]
    [InlineData(4096u, FlagGrid)]
    [InlineData(3000u, 0u)]
    public void ScrambledCallerMxcsrDoesNotChangeResults(uint n, uint flags)
    {
        _ = NativeKernel.Handle;

        var p = Params(n, flags);
        ulong size = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, size);

        float[] clean, scrambled;

        void* a = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(a, size, in p));
            swarm_step(a, 8);
            clean = ReadStateInto(a, n);
        }
        finally { NativeMemory.AlignedFree(a); }

        void* b = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            SwarmParams* pp = &p;          // a local: already fixed
            Assert.Equal(Hostile, UnderHostile("swarm_init", out ulong rc, (nint)b, (nint)size, (nint)pp));
            Assert.Equal(0, (int)rc);
            Assert.Equal(Hostile, UnderHostile("swarm_step", (nint)b, 8));
            scrambled = ReadStateInto(b, n);
        }
        finally { NativeMemory.AlignedFree(b); }

        Assert.Equal(clean, scrambled);
    }

    /// <summary>
    /// The same claim for the pooled entry points. <see cref="ThreadingTests"/>
    /// already compares serial and threaded output for exact equality, but it
    /// never scrambles first, so the per-thread pin in `pool_pass` was asserted
    /// only in comments. Here the pooled run is entered from the hostile word
    /// and compared against a clean serial run.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(0)]     // auto: the machine's physical core count
    public void WorkerThreadsPinTheirOwnMxcsr(int threads)
    {
        _ = NativeKernel.Handle;

        var p = Params(4096, FlagGrid);
        ulong size = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, size);

        float[] serial;
        void* a = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(a, size, in p));
            swarm_step(a, 6);
            serial = ReadStateInto(a, p.N);
        }
        finally { NativeMemory.AlignedFree(a); }

        Assert.Equal(Hostile, UnderHostile("swarm_pool_init", out ulong t, threads));
        Assert.True((int)t >= 1, $"pool_init({threads}) failed under a scrambled caller word");
        try
        {
            void* b = NativeMemory.AlignedAlloc((nuint)size, 64);
            try
            {
                Assert.Equal(0, swarm_init(b, size, in p));
                Assert.Equal(Hostile, UnderHostile("swarm_step_mt", (nint)b, 6));
                Assert.Equal(serial, ReadStateInto(b, p.N));

                // swarm_pass_mt over a frozen bank, from the hostile word too:
                // it is the export the benchmark drives and it reaches the same
                // per-thread pin by a different route.
                Assert.Equal(Hostile, UnderHostile("swarm_pass_mt", (nint)b));
            }
            finally { NativeMemory.AlignedFree(b); }
        }
        finally { swarm_pool_shutdown(); }
    }
}
