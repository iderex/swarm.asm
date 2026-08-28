using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// <c>force_path = 4</c>, the fused AVX2 body (issue #162).
///
/// It is <c>pass_avx2</c>'s neighbourhood, lane hygiene and reduction order
/// with five mul-then-add/sub pairs rounded once instead of twice, reached
/// through its own opt-in id so that <c>PATH_AVX2</c>'s bits are untouched and
/// the two numerics stand side by side rather than one replacing the other.
///
/// WHY THE PARITY CASE HERE STOPS AT A LOWER HORIZON THAN <see cref="StepTests"/>,
/// which is the part to read before raising it. Fusing does not move the kernel
/// closer to the oracle. The oracle is an unfused f32 op sequence, so single
/// rounding is a DIFFERENT distance from it, not a smaller one, and it adds a
/// divergence source on top of the summation-order difference path 1 already
/// carries. Measured on this kernel at n = 200, 100 seeds from 0x5EED0000,
/// species 5, rmax 0.2, beta 0.3, dt 0.02, friction 0.71, force_scale 10,
/// brute force - the sweep's own parameters:
///
/// <code>
/// path 1 n 200 S=6 pos 6.556511E-07  vel 2.670288E-05
/// path 1 n 200 S=8 pos 3.1590462E-06 vel 6.9618225E-05   over 1e-4 vel: 0 seeds
/// path 4 n 200 S=6 pos 1.0728836E-06 vel 2.9176474E-05
/// path 4 n 200 S=7 pos 1.3113022E-06 vel 2.9854476E-05
/// path 4 n 200 S=8 pos 2.5629997E-06 vel 1.257062E-04    over 1e-4 vel: 1 seed
/// </code>
///
/// The unfused arm of that run reproduces the 2026-08-05 figures recorded in
/// <see cref="OracleDivergenceSweep"/> digit for digit at S = 1, 4 and 8, which
/// is what makes the fused arm beside it readable rather than merely printed.
///
/// So the 1e-5 / 1e-4 pair does NOT hold for path 4 at S = 8: one seed in a
/// hundred carries velocity to 1.26e-04, and it is seed <c>0x5EED002B</c>,
/// named because the case below prints it rather than because anyone guessed.
/// The pair holds with margin at S = 6 - 9.3x on position and 3.4x on velocity
/// - and that is the horizon asserted below. Raising it means re-running the
/// measurement first, exactly as the note at the top of <see cref="StepTests"/>
/// says about raising n there.
///
/// WHAT IS NOT MEASURED, and it is the larger half. Nothing here is a speed
/// figure: the fusion was built to remove co-limiting FP uops and whether it
/// does is a benchmark, which #162 holds open for a quiet machine. Divergence
/// is deterministic and load-independent, which is why it could be taken now
/// and the timing could not. And the envelope above n = 200 is not measured for
/// path 4 at all - <see cref="OracleDivergenceSweep"/> records n = 1024 and
/// 4096 for paths 1 and 3 only, and this change does not extend it.
/// </summary>
public sealed unsafe class FusedPathTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern uint swarm_cpu_paths();

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    private const uint PathAvx2 = 1, PathAvx2Fma = 4;
    private const uint CpuAvx2 = 1;          // abi.inc CPU_AVX2
    private const int AhPath = 12;           // abi.inc AH_PATH
    private const uint Species = 5;
    private const float RMax = 0.2f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f;

    /// <summary>The horizon the tolerances below are measured at. See the class
    /// header for the figures and for why it is not 8.</summary>
    private const int MeasuredHorizon = 6;

    private static bool HasAvx2 => (swarm_cpu_paths() & CpuAvx2) != 0;

    [Fact]
    public void InitAcceptsTheFusedPathAndStoresIt()
    {
        _ = NativeKernel.Handle;
        if (!HasAvx2)
        {
            Assert.Skip("this host reports no CPU_AVX2, so force_path=4 is correctly refused here and the accept arm is unreachable");
            return;
        }

        var (p, _) = Make(200, 0x77, PathAvx2Fma);
        WithArena(p, arena =>
        {
            Assert.Equal(0, swarm_init((void*)arena, swarm_layout_bytes(in p), in p));
            Assert.Equal(PathAvx2Fma, *(uint*)((byte*)arena + AhPath));
        });
    }

    /// <summary>
    /// The fused body is actually REACHED. Without this, every other assertion
    /// in this file would pass just as well against a dispatch that quietly
    /// routed id 4 to <c>pass_avx2</c> - the parity tiers are bounds, and a
    /// bound cannot tell two bodies apart. Bit inequality against path 1 is the
    /// only thing that can, and it is the sharpest claim available: the two
    /// paths agree on the neighbourhood and the reduction order, so any
    /// difference at all is the rounding this change introduces.
    /// </summary>
    [Fact]
    public void FusedPathIsNotTheUnfusedBody()
    {
        _ = NativeKernel.Handle;
        if (!HasAvx2)
        {
            Assert.Skip("this host reports no CPU_AVX2, so neither force_path=1 nor force_path=4 initialises here");
            return;
        }

        var unfused = RunSteps(PathAvx2, MeasuredHorizon);
        var fused = RunSteps(PathAvx2Fma, MeasuredHorizon);

        Assert.False(
            unfused.SequenceEqual(fused),
            "force_path=4 produced bit-identical state to force_path=1: the fused body is "
                + "not being reached, or force_group's fused arm assembled to the unfused "
                + "instructions (issue #162)");
    }

    /// <summary>Same id, same seed, same bits - twice. The fused path is a code
    /// path like any other and owes the determinism the masterplan requires of
    /// all of them.</summary>
    [Fact]
    public void FusedPathIsDeterministic()
    {
        _ = NativeKernel.Handle;
        if (!HasAvx2)
        {
            Assert.Skip("this host reports no CPU_AVX2, so force_path=4 does not initialise here");
            return;
        }

        Assert.Equal(RunSteps(PathAvx2Fma, MeasuredHorizon), RunSteps(PathAvx2Fma, MeasuredHorizon));
    }

    /// <summary>
    /// The parity claim, asserted over THE SAME SEED SET IT WAS MEASURED ON.
    ///
    /// This ran on one seed first and that was wrong in a way worth recording:
    /// raising <see cref="MeasuredHorizon"/> to 8, where the sweep says one
    /// seed in a hundred carries velocity to 1.26e-04, left the case GREEN,
    /// because seed 0x77 is not that seed. A horizon bound whose fixture cannot
    /// reach the breach it is bounding is decoration. Sweeping the hundred
    /// makes the assertion and its evidence the same set, and raising the
    /// horizon past 6 now reddens here for the reason the header names.
    /// </summary>
    [Fact]
    public void FusedPathMatchesOracleWithinTheMeasuredHorizon()
    {
        _ = NativeKernel.Handle;
        if (!HasAvx2)
        {
            Assert.Skip("this host reports no CPU_AVX2, so force_path=4 does not initialise here");
            return;
        }

        const uint n = 200;
        const int seeds = 100;
        float worstPos = 0f, worstVel = 0f;
        ulong worstPosSeed = 0, worstVelSeed = 0;

        for (int k = 0; k < seeds; k++)
        {
            ulong seed = (ulong)(0x5EED0000 + k);
            var (p, matrix) = Make(n, seed, PathAvx2Fma);
            var oracle = new TestOracle.World(
                (int)n, (int)Species, seed, RMax, Beta, Dt, Friction, ForceScale, matrix);
            for (int step = 0; step < MeasuredHorizon; step++)
            {
                oracle.Step();
            }

            WithArena(p, arena =>
            {
                void* a = (void*)arena;
                Assert.Equal(0, swarm_init(a, swarm_layout_bytes(in p), in p));
                swarm_step(a, MeasuredHorizon);
                var x = new float[n]; var y = new float[n];
                var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];
                Assert.Equal(0, swarm_read_state(a, x, y, vx, vy, sp));
                for (uint i = 0; i < n; i++)
                {
                    Widen(ref worstPos, ref worstPosSeed, TorusDist(x[i], oracle.X[i]), seed);
                    Widen(ref worstPos, ref worstPosSeed, TorusDist(y[i], oracle.Y[i]), seed);
                    Widen(ref worstVel, ref worstVelSeed, MathF.Abs(vx[i] - oracle.Vx[i]), seed);
                    Widen(ref worstVel, ref worstVelSeed, MathF.Abs(vy[i] - oracle.Vy[i]), seed);
                }
            });
        }

        var report =
            $"path 4 n {n} S={MeasuredHorizon} over {seeds} seeds from 0x5EED0000: "
            + $"worst pos {worstPos:R} (seed 0x{worstPosSeed:X}), "
            + $"worst vel {worstVel:R} (seed 0x{worstVelSeed:X})";

        Assert.True(worstPos < 1e-5f, $"fused position drift left the 1e-5 tolerance. {report}");
        Assert.True(worstVel < 1e-4f, $"fused velocity drift left the 1e-4 tolerance. {report}");
    }

    private static void Widen(ref float worst, ref ulong worstSeed, float candidate, ulong seed)
    {
        if (candidate > worst)
        {
            worst = candidate;
            worstSeed = seed;
        }
    }

    private static float[] RunSteps(uint forcePath, int steps)
    {
        const uint n = 200;
        var (p, _) = Make(n, 0xBEEF, forcePath);
        var result = new float[4 * n];
        WithArena(p, arena =>
        {
            void* a = (void*)arena;
            Assert.Equal(0, swarm_init(a, swarm_layout_bytes(in p), in p));
            swarm_step(a, (uint)steps);
            var x = new float[n]; var y = new float[n];
            var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];
            Assert.Equal(0, swarm_read_state(a, x, y, vx, vy, sp));
            x.CopyTo(result, 0);
            y.CopyTo(result, (int)n);
            vx.CopyTo(result, (int)(2 * n));
            vy.CopyTo(result, (int)(3 * n));
        });
        return result;
    }

    private static float TorusDist(float a, float b)
    {
        float d = MathF.Abs(a - b);
        return d > 0.5f ? 1f - d : d;
    }

    private static (SwarmParams, float[]) Make(uint n, ulong seed, uint forcePath)
    {
        var matrix = new float[64];
        for (uint a = 0; a < Species; a++)
        {
            for (uint b = 0; b < Species; b++)
            {
                matrix[a * 8 + b] = MathF.Sin((a * 3.1f) + (b * 1.7f));
            }
        }

        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = Species, Seed = seed,
            RMax = RMax, Beta = Beta, Dt = Dt, Friction = Friction, ForceScale = ForceScale,
            ForcePath = forcePath, Flags = 0,
        };
        for (int i = 0; i < 64; i++)
        {
            p.Matrix[i] = matrix[i];
        }

        return (p, matrix);
    }

    private static void WithArena(in SwarmParams p, Action<nint> body)
    {
        ulong size = swarm_layout_bytes(in p);
        Assert.True(size > 0, "swarm_layout_bytes refused the params");
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            body((nint)arena);
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }
}
