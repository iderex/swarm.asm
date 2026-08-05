using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Open risk 5, the probe the risk specified and nobody had run: the oracle
/// tolerances (1e-5 on positions, 1e-4 on velocities, horizons S in {1, 4, 8})
/// rested on ASSUMED divergence growth between the asm engine and the unfused
/// .NET reference. This measures it.
///
/// It is opt-in. The sweep is n = 4096 brute force against an O(n^2) scalar
/// reference for 8 steps on each of 100 seeds and both forced paths, which is
/// minutes rather than seconds, and the risk itself says it does not need to
/// run per pull request. Without <c>SWARM_LONG_RUN=1</c> it skips, and the
/// skip names the variable so a reader is never left guessing why a sweep they
/// came looking for did not run.
///
/// The measurement it produced is recorded in
/// <see cref="MeasuredDivergence"/>, and the assertions below are set from
/// that measurement rather than from the tolerances they are checking - a probe
/// that only re-asserts the number it was sent to justify proves nothing.
/// </summary>
public sealed unsafe class OracleDivergenceSweep
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    private const uint PathAvx2 = 1, PathScalar = 3;
    private static readonly uint[] Counts = [200, 1024, 4096];

    // The horizons the risk names, and the AVX2 figures recorded at them on
    // 2026-08-05. Only path 1 has an envelope: path 3 is asserted exactly.
    private static readonly int[] Horizons = [1, 4, 8];
    private static readonly Dictionary<uint, (float[] Pos, float[] Vel)> RecordedAvx2 = new()
    {
        [200] = ([5.9604645e-08f, 2.9802322e-07f, 3.1590462e-06f], [2.9802322e-07f, 5.3048134e-06f, 6.9618225e-05f]),
        [1024] = ([1.1920929e-07f, 2.8014183e-06f, 5.2899122e-05f], [1.3113022e-06f, 9.214878e-05f, 2.5424957e-03f]),
        [4096] = ([1.1920929e-07f, 8.5912645e-05f, 7.598579e-03f], [5.722046e-06f, 4.2147636e-03f, 3.0829597e-01f]),
    };
    private const uint Species = 5;
    private const int Seeds = 100, Horizon = 8;
    private const float RMax = 0.2f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f;

    // MEASURED 2026-08-05, 100 seeds, rmax 0.2, beta 0.3, dt 0.02,
    // friction 0.71, force_scale 10, brute force, on the reference machine.
    // Max per-component drift against the oracle, by particle count and step:
    //
    //   path 3 (scalar): 0 at EVERY n and EVERY S. The scalar path reproduces
    //   the C# reference bit for bit, so the epsilon does no work there at all.
    //
    //   path 1 (AVX2), pos / vel:
    //     n=200   S=1 5.96e-08 / 2.98e-07   S=4 2.98e-07 / 5.30e-06
    //             S=6 6.56e-07 / 2.67e-05   S=8 3.16e-06 / 6.96e-05
    //     n=1024  S=1 1.19e-07 / 1.31e-06   S=4 2.80e-06 / 9.21e-05
    //             S=6 1.31e-05 / 5.64e-04   S=8 5.29e-05 / 2.54e-03
    //     n=4096  S=1 1.19e-07 / 5.72e-06   S=4 8.59e-05 / 4.21e-03
    //             S=6 1.08e-03 / 5.37e-02   S=8 7.60e-03 / 3.08e-01
    //
    // THE READING. The 1e-5 / 1e-4 pair holds at n=200 and nowhere above it.
    // At n=200, S=8 it holds with 3.2x margin on position and 1.4x on
    // velocity - thin, and worth knowing, because that is the size the suite
    // actually asserts at. At n=1024 position leaves the tolerance by S=6 and
    // velocity by S=5; at n=4096 velocity leaves by S=2 and position by S=3.
    //
    // So the pair is CONFIRMED FOR THE DOMAIN THE HARNESS USES IT IN and is
    // not a general statement about the engine. Every parity case in the tree
    // runs at n <= 200, so nothing asserted today sits outside it. What was
    // wrong was the masterplan stating the tolerance without the n it is
    // bounded by, which is repaired in the same change.
    //
    // Growth is roughly an order of magnitude per step once it starts, which
    // is Lyapunov separation amplifying a summation-order difference, not an
    // error accumulating. Bigger n means more terms per sum, so the seed
    // difference is larger and the same growth starts from higher up.

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public void PerStepDivergenceAgainstTheOracle(uint forcePath)
    {
        if (Environment.GetEnvironmentVariable("SWARM_LONG_RUN") != "1")
        {
            Assert.Skip("long-run sweep: set SWARM_LONG_RUN=1 to measure oracle divergence growth");
        }

        _ = NativeKernel.Handle;

        var lines = new List<string>();
        var envelope = new List<(string Label, float Measured, float Recorded)>();
        float worst = 0f;
        foreach (uint n in Counts)
        {
            var maxPos = new float[Horizon + 1];
            var maxVel = new float[Horizon + 1];

            for (int k = 0; k < Seeds; k++)
            {
                ulong seed = (ulong)(0x5EED0000 + k);
                var (p, matrix) = Make(n, seed, forcePath);
                var oracle = new TestOracle.World(
                    (int)n, (int)Species, seed, RMax, Beta, Dt, Friction, ForceScale, matrix);

                ulong size = swarm_layout_bytes(in p);
                void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
                try
                {
                    Assert.Equal(0, swarm_init(arena, size, in p));
                    var x = new float[n]; var y = new float[n];
                    var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];

                    for (int step = 1; step <= Horizon; step++)
                    {
                        oracle.Step();
                        swarm_step(arena, 1);
                        Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, sp));

                        for (uint i = 0; i < n; i++)
                        {
                            Widen(ref maxPos[step], TorusDist(x[i], oracle.X[i]));
                            Widen(ref maxPos[step], TorusDist(y[i], oracle.Y[i]));
                            Widen(ref maxVel[step], MathF.Abs(vx[i] - oracle.Vx[i]));
                            Widen(ref maxVel[step], MathF.Abs(vy[i] - oracle.Vy[i]));
                        }
                    }
                }
                finally
                {
                    NativeMemory.AlignedFree(arena);
                }
            }

            for (int step = 1; step <= Horizon; step++)
            {
                lines.Add($"path {forcePath} n {n} S={step} pos {maxPos[step]:R} vel {maxVel[step]:R}");
                Widen(ref worst, maxPos[step]);
                Widen(ref worst, maxVel[step]);
            }

            if (forcePath == PathAvx2)
            {
                var (recPos, recVel) = RecordedAvx2[n];
                for (int h = 0; h < Horizons.Length; h++)
                {
                    envelope.Add(($"n {n} S={Horizons[h]} pos", maxPos[Horizons[h]], recPos[h]));
                    envelope.Add(($"n {n} S={Horizons[h]} vel", maxVel[Horizons[h]], recVel[h]));
                }
            }
        }

        var report = $"{Seeds} seeds | " + string.Join(" | ", lines);

        // Path 3 is the strongest claim the data supports, so it is asserted
        // as an equality rather than as a bound: the scalar path and the
        // reference agree bit for bit at every count and every horizon.
        if (forcePath == PathScalar)
        {
            Assert.True(
                worst == 0f,
                $"the scalar path no longer reproduces the oracle exactly; worst component drift {worst:R}. {report}");
        }

        // Path 1 is asserted against the recorded envelope rather than against
        // the harness tolerances, because at n > 200 it exceeds those
        // tolerances by design and re-asserting them here would only encode
        // the assumption this sweep was written to test. Four times the
        // recorded figure, so an ordinary re-baseline passes and a change of
        // regime does not: growth here is about an order of magnitude per
        // step, so a shift of one step in when it starts is a factor of ten.
        for (int k = 0; k < envelope.Count; k++)
        {
            var (label, measured, recorded) = envelope[k];
            Assert.True(
                measured <= recorded * 4f,
                $"divergence at {label} is {measured:R}, more than 4x the 2026-08-05 figure {recorded:R}. "
                    + $"That is a change of regime rather than a re-baseline. {report}");
        }
    }

    private static void Widen(ref float slot, float v)
    {
        if (v > slot)
        {
            slot = v;
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
}
