using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The horizon <c>force_path = 1</c> actually holds the 1e-5 / 1e-4 oracle
/// pair at, measured over the seed set it is asserted on (issue #162).
///
/// This file replaces <c>FusedPathTests</c>, which asserted the same claim
/// against <c>force_path = 4</c> while that id carried the fused body beside
/// an unfused path 1. The fusion moved into path 1 itself on 2026-08-31 and
/// the id was retired, so the claim has one subject again.
///
/// WHY THE HORIZON HERE IS LOWER THAN THE ONE IN <see cref="StepTests"/>,
/// which is the part to read before raising it. Fusing does not move the
/// kernel closer to the oracle. The oracle is an unfused f32 op sequence, so
/// single rounding is a DIFFERENT distance from it, not a smaller one, and it
/// adds a divergence source on top of the summation-order difference the AVX2
/// path already carried. Measured on this kernel at n = 200, 100 seeds from
/// 0x5EED0000, species 5, rmax 0.2, beta 0.3, dt 0.02, friction 0.71,
/// force_scale 10, brute force - the parameters
/// <see cref="OracleDivergenceSweep"/> uses - before and after the fusion:
///
/// <code>
/// unfused  n 200 S=6 pos 6.556511E-07  vel 2.670288E-05
/// unfused  n 200 S=8 pos 3.1590462E-06 vel 6.9618225E-05   over 1e-4 vel: 0 seeds
/// fused    n 200 S=6 pos 1.0728836E-06 vel 2.9176474E-05
/// fused    n 200 S=7 pos 1.3113022E-06 vel 2.9854476E-05
/// fused    n 200 S=8 pos 2.5629997E-06 vel 1.257062E-04    over 1e-4 vel: 1 seed
/// </code>
///
/// So the 1e-5 / 1e-4 pair does NOT hold at S = 8 any more: one seed in a
/// hundred carries velocity to 1.26e-04, and it is seed <c>0x5EED002B</c>,
/// named because the case below prints it rather than because anyone guessed.
/// The pair holds with margin at S = 6 - 9.3x on position and 3.4x on velocity
/// - and that is the horizon asserted below. Raising it means re-running the
/// measurement first, exactly as the note at the top of <see cref="StepTests"/>
/// says about raising n there.
///
/// The concrete parity cases in <see cref="StepTests"/> keep their S = 8, and
/// that is not an inconsistency: they run at their own seeds, where the fused
/// drift stays more than an order of magnitude inside the pair. What moved is
/// the WORST case over a hundred seeds, which is the figure the tolerance is
/// justified by rather than the figure any one case measures.
///
/// WHAT IS NOT MEASURED. Nothing here is a speed figure. The fusion was built
/// to remove co-limiting FP uops and whether it does is a benchmark, which
/// issue #162 holds open for a quiet machine.
/// </summary>
public sealed unsafe class Avx2ParityHorizonTests
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

    private const uint PathAvx2 = 1;
    private const uint CpuAvx2 = 1;          // abi.inc CPU_AVX2
    private const uint Species = 5;
    private const float RMax = 0.2f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f;

    /// <summary>The horizon the tolerances below are measured at. See the class
    /// header for the figures and for why it is not 8.</summary>
    private const int MeasuredHorizon = 6;

    private static bool HasAvx2 => (swarm_cpu_paths() & CpuAvx2) != 0;

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
    public void Avx2MatchesOracleWithinTheMeasuredHorizon()
    {
        _ = NativeKernel.Handle;
        if (!HasAvx2)
        {
            Assert.Skip("this host reports no CPU_AVX2, so force_path=1 does not initialise here");
            return;
        }

        const uint n = 200;
        const int seeds = 100;
        float worstPos = 0f, worstVel = 0f;
        ulong worstPosSeed = 0, worstVelSeed = 0;

        for (int k = 0; k < seeds; k++)
        {
            ulong seed = (ulong)(0x5EED0000 + k);
            var (p, matrix) = Make(n, seed, PathAvx2);
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
            $"path 1 n {n} S={MeasuredHorizon} over {seeds} seeds from 0x5EED0000: "
            + $"worst pos {worstPos:R} (seed 0x{worstPosSeed:X}), "
            + $"worst vel {worstVel:R} (seed 0x{worstVelSeed:X})";

        Assert.True(worstPos < 1e-5f, $"AVX2 position drift left the 1e-5 tolerance. {report}");
        Assert.True(worstVel < 1e-4f, $"AVX2 velocity drift left the 1e-4 tolerance. {report}");
    }

    private static void Widen(ref float worst, ref ulong worstSeed, float candidate, ulong seed)
    {
        if (candidate > worst)
        {
            worst = candidate;
            worstSeed = seed;
        }
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
