using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Open risk 6: the p >= 1.0 wrap canonicalization is proven for the
/// floor-subtract path, but the other producers of boundary values (the
/// velocity clamp interacting with dt, minimum image at exactly 0.5) had never
/// been swept. A missed producer corrupts cell binning or writes past the
/// framebuffer, which is the failure class the canonicalization exists to
/// prevent, so this sweep asserts those two consequences directly rather than
/// asserting the canonicalization's internals.
///
/// The parameters are chosen to maximise boundary traffic rather than to look
/// like a scene, and they sweep `rmax` so that g is 4, 16 and 512 - the
/// coarsest binning, a middling one, and the clamp.
///
/// WHAT THIS CANNOT DO, stated because it bounds the result: the export surface
/// has no state-write entry, so a test cannot seed an exact boundary value into
/// the arena. Every position here is one the engine produced. The sweep reaches
/// the boundary by construction rather than by injection, and
/// <see cref="BoundaryCoverageIsNotVacuous"/> exists to prove it arrives -
/// without it a green run here would be indistinguishable from a sweep whose
/// particles never came near an edge.
/// </summary>
public sealed unsafe class BoundaryPropertyTests
{
    private const uint FlagGrid = 1;

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
    private static extern void swarm_plot(void* arena, uint* bgra, uint w, uint h);

    private const uint N = 512, Species = 5, Steps = 40;
    private static readonly ulong[] Seeds = [1, 2, 3, 5, 8, 13, 21, 34];

    // The validated ceilings (src/kernel/parse.inc, kr_table): dt <= 0.1,
    // force_scale <= 100, friction >= 0, rmax <= 0.25, beta in [0.05, 0.95].
    // Two families, because they reach the boundary from opposite directions.
    // The hot scenes cross the edges every step but their pre-wrap position is
    // large, so `p - floor(p)` has coarse resolution and cannot land near the
    // edge; the gentle scenes cross rarely but cross by a hair, and `p - 1.0`
    // is exact there, so they are the ones that reach the ulp neighbourhood.
    private static readonly (float RMax, float Beta, float Dt, float Friction, float ForceScale)[] Scenes =
    [
        (0.25f, 0.5f, 0.1f, 0f, 100f),
        (0.05f, 0.05f, 0.1f, 0f, 100f),
        (0.001f, 0.95f, 0.1f, 0f, 100f),
        (0.05f, 0.3f, 0.005f, 0.5f, 10f),
        (0.25f, 0.7f, 0.001f, 0.9f, 3f),
    ];

    private static SwarmParams Make(
        ulong seed, (float RMax, float Beta, float Dt, float Friction, float ForceScale) scene, uint forcePath, uint flags)
    {
        var p = new SwarmParams
        {
            Version = 1, N = N, SpeciesN = Species, Seed = seed,
            RMax = scene.RMax, Beta = scene.Beta, Dt = scene.Dt,
            Friction = scene.Friction, ForceScale = scene.ForceScale,
            ForcePath = forcePath, Flags = flags,
        };
        for (uint a = 0; a < Species; a++)
        {
            for (uint b = 0; b < Species; b++)
            {
                p.Matrix[(int)(a * 8 + b)] = a == b ? -1f : 1f; // the hottest matrix the grammar allows
            }
        }

        return p;
    }

    /// <summary>g, exactly as arena_dims_core derives it (src/kernel/layout.inc).</summary>
    private static uint GridDim(float rmax)
    {
        uint g = 4;
        while (g < 512 && 1.0f / (g * 2) >= rmax)
        {
            g *= 2;
        }

        return g;
    }

    public static TheoryData<uint, uint> Paths() =>
        new() { { 1u, 0u }, { 1u, FlagGrid }, { 3u, 0u }, { 3u, FlagGrid } };

    [Theory]
    [MemberData(nameof(Paths))]
    public void PositionsStayCanonicalAndBinInRange(uint forcePath, uint flags)
    {
        _ = NativeKernel.Handle;

        foreach (var scene in Scenes)
        {
            uint g = GridDim(scene.RMax);
            foreach (var seed in Seeds)
            {
                var p = Make(seed, scene, forcePath, flags);
                ulong size = swarm_layout_bytes(in p);
                Assert.NotEqual(0ul, size);
                void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
                try
                {
                    Assert.Equal(0, swarm_init(arena, size, in p));
                    for (uint step = 1; step <= Steps; step++)
                    {
                        swarm_step(arena, 1);
                        var (x, y) = ReadPositions(arena);
                        var where = $"step {step}, path {forcePath}, flags {flags}, rmax {scene.RMax}, seed {seed}";
                        for (uint i = 0; i < N; i++)
                        {
                            // The canonicalization's own claim.
                            Assert.True(x[i] >= 0f && x[i] < 1f, $"x[{i}] = {x[i]:R} left [0,1) at {where}");
                            Assert.True(y[i] >= 0f && y[i] < 1f, $"y[{i}] = {y[i]:R} left [0,1) at {where}");

                            // The consequence that corrupts memory: the bin
                            // index, derived with the same multiply-truncate.
                            int cx = (int)(x[i] * g), cy = (int)(y[i] * g);
                            Assert.True(
                                cx >= 0 && cx < g && cy >= 0 && cy < g,
                                $"({x[i]:R}, {y[i]:R}) bins to ({cx}, {cy}) outside [0,{g})^2 at {where}");
                        }
                    }
                }
                finally
                {
                    NativeMemory.AlignedFree(arena);
                }
            }
        }
    }

    /// <summary>
    /// The other consequence: a boundary position that bins in range can still
    /// be rasterized out of the framebuffer. The buffer is unmanaged with a
    /// canary margin on both sides, and w and h are odd, prime and unequal so a
    /// stride mistake lands in a margin rather than in another row.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void PlotNeverWritesOutsideTheFramebuffer(uint forcePath, uint flags)
    {
        _ = NativeKernel.Handle;

        const uint W = 97, H = 61, Margin = 64, Canary = 0xDEADBEEF;
        uint total = W * H + (2 * Margin);

        foreach (var scene in Scenes)
        {
            foreach (var seed in Seeds)
            {
                var p = Make(seed, scene, forcePath, flags);
                ulong size = swarm_layout_bytes(in p);
                void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
                uint* block = (uint*)NativeMemory.AlignedAlloc((nuint)total * sizeof(uint), 64);
                try
                {
                    Assert.Equal(0, swarm_init(arena, size, in p));
                    for (uint step = 1; step <= Steps; step++)
                    {
                        swarm_step(arena, 1);
                        for (uint i = 0; i < total; i++)
                        {
                            block[i] = Canary;
                        }

                        swarm_plot(arena, block + Margin, W, H);

                        var where = $"step {step}, path {forcePath}, flags {flags}, rmax {scene.RMax}, seed {seed}";
                        for (uint i = 0; i < Margin; i++)
                        {
                            Assert.True(
                                block[i] == Canary,
                                $"plot wrote {block[i]:X8} {Margin - i} words before the framebuffer at {where}");
                            Assert.True(
                                block[Margin + (W * H) + i] == Canary,
                                $"plot wrote {block[Margin + (W * H) + i]:X8} {i + 1} words past the framebuffer at {where}");
                        }
                    }
                }
                finally
                {
                    NativeMemory.AlignedFree(block);
                    NativeMemory.AlignedFree(arena);
                }
            }
        }
    }

    /// <summary>
    /// The non-vacuity control for this whole file. If the sweep never puts a
    /// position in the first or last ulp-neighbourhood of the interval, every
    /// assertion above is green for the uninteresting reason.
    ///
    /// The tallies are reported in the failure message rather than asserted at
    /// a number: the exact counts are a property of the engine's arithmetic and
    /// pinning them would turn any legitimate re-baseline into a failure here.
    /// </summary>
    [Fact]
    public void BoundaryCoverageIsNotVacuous()
    {
        _ = NativeKernel.Handle;

        const float Near = 1.0f / (1 << 20);
        long atZero = 0, nearZero = 0, nearOne = 0, samples = 0;
        float minToZero = 1f, minToOne = 1f;

        foreach (var scene in Scenes)
        {
            foreach (var seed in Seeds)
            {
                var p = Make(seed, scene, 1u, 0u);
                ulong size = swarm_layout_bytes(in p);
                void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
                try
                {
                    Assert.Equal(0, swarm_init(arena, size, in p));
                    for (uint step = 1; step <= Steps; step++)
                    {
                        swarm_step(arena, 1);
                        var (x, y) = ReadPositions(arena);
                        for (uint i = 0; i < N; i++)
                        {
                            Tally(x[i]);
                            Tally(y[i]);
                        }
                    }
                }
                finally
                {
                    NativeMemory.AlignedFree(arena);
                }
            }
        }

        // MEASURED 2026-08-05 on this sweep, the figures the threshold below is
        // set from: 1,638,400 samples, 0 exactly 0, 1 within 2^-20 of 0, 1
        // within 2^-20 of 1, closest approach to 0 = 8.060888e-07, gap to 1 =
        // 2.3841858e-07, which is two ulps at 1.0. Reproduce with
        //   dotnet test tests/Swarm.Tests -- --filter-method "*BoundaryCoverageIsNotVacuous*"
        // after replacing the assertion below with Assert.True(false, ...).
        //
        // The threshold is 1e-5 rather than the measured figure: the sweep is
        // deterministic, so the numbers only move when the engine's arithmetic
        // does, and a threshold sitting one percent above a measurement turns
        // every legitimate re-baseline into a failure here. The counts of
        // near-edge samples are NOT asserted at all - both are 1, and a
        // one-sample margin is not something to gate a build on.
        Assert.True(
            minToZero < 1e-5f && minToOne < 1e-5f,
            "the sweep no longer reaches the interval edges, so nothing else in this file proves anything: "
                + $"{samples} samples, {atZero} exactly 0, {nearZero} within 2^-20 of 0, {nearOne} within 2^-20 of 1, "
                + $"closest approach to 0 = {minToZero:R}, gap to 1 = {minToOne:R}. "
                + "Raise the step count, or add a scene that crosses an edge by a smaller margin.");

        void Tally(float v)
        {
            samples++;
            if (v == 0f) { atZero++; }
            if (v < Near) { nearZero++; }
            if (v > 1f - Near) { nearOne++; }
            if (v < minToZero) { minToZero = v; }
            if (1f - v < minToOne) { minToOne = 1f - v; }
        }
    }

    /// <summary>
    /// The case the pin exists for, found rather than assumed.
    ///
    /// `wrap_body` computes `p - floor(p)` and then forces the result to 0 when
    /// it is not below 1.0. That second half only acts when p is a small
    /// negative number: for p in (-2^-24, 0), `p - floor(p)` is `p + 1`, which
    /// rounds to exactly 1.0 in f32. A position of exactly 1.0 is what breaks
    /// binning and rasterization, so this is the producing case for the whole
    /// risk.
    ///
    /// MEASURED 2026-08-05, and the reason this test exists: with those two
    /// instructions deleted from `wrap_body`, the entire suite as it then stood
    /// - 214 tests, including every other test in this file - stayed green. The
    /// pin was carried on trust.
    ///
    /// The sweep above cannot reach it either, and the rate says why. A step
    /// lands in that window with probability about 1.2e-7 per particle-step
    /// whatever the scene's speed, so a sweep needs of the order of 1e7
    /// particle-steps before it expects one landing, and the sweep above spends
    /// 8.2e5. Raising it to that scale would put minutes into every run for one
    /// expected hit.
    ///
    /// So it was searched for instead, against a build with the pin removed:
    /// 1.688e8 particle-steps over twelve seeds produced four landings, and
    /// this is the cheapest of the four to reproduce. It is pinned by seed and
    /// step count rather than searched for again at run time, because the
    /// search costs minutes and the engine is deterministic.
    ///
    /// PROVEN TO BITE: with the two pin instructions removed, this test reports
    /// `y[3229] at step 1211 is 1, not 0`; with them present it reads 0. It is
    /// the only test in the tree that tells those two builds apart.
    /// </summary>
    [Fact]
    public void TheWrapPinIsReachedAndHoldsTheExactBoundaryCase()
    {
        _ = NativeKernel.Handle;

        const uint n = 4096, species = 5, hitStep = 1211, hitIndex = 3229;
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = species, Seed = 11,
            RMax = 0.05f, Beta = 0.3f, Dt = 0.001f, Friction = 0.9f, ForceScale = 3f,
            ForcePath = 1, Flags = FlagGrid,
        };
        for (uint a = 0; a < species; a++)
        {
            for (uint b = 0; b < species; b++)
            {
                p.Matrix[(int)(a * 8 + b)] = a == b ? -1f : 1f;
            }
        }

        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));
            swarm_step(arena, hitStep);
            var x = new float[n]; var y = new float[n];
            var vx = new float[n]; var vy = new float[n]; var s = new uint[n];
            Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, s));

            Assert.True(
                y[hitIndex] == 0f,
                $"the recorded landing moved: y[{hitIndex}] at step {hitStep} is {y[hitIndex]:R}, not 0. "
                    + "Either the wrap pin is gone (in which case this reads 1) or the engine's arithmetic "
                    + "changed and the search has to be re-run to find the new earliest landing.");

            for (uint i = 0; i < n; i++)
            {
                Assert.True(x[i] >= 0f && x[i] < 1f, $"x[{i}] = {x[i]:R} left [0,1) at the pinned landing");
                Assert.True(y[i] >= 0f && y[i] < 1f, $"y[{i}] = {y[i]:R} left [0,1) at the pinned landing");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    /// <summary>
    /// Oracle agreement under the same boundary-heavy scenes, at a one-step
    /// horizon. Deliberately short: this asks whether the boundary handling
    /// agrees with the reference, not how far chaos separates them afterwards.
    /// </summary>
    [Fact]
    public void BoundaryScenesStillAgreeWithTheOracle()
    {
        _ = NativeKernel.Handle;

        foreach (var scene in Scenes)
        {
            var p = Make(7, scene, 1u, 0u);
            var matrix = new float[64];
            for (int i = 0; i < 64; i++)
            {
                matrix[i] = p.Matrix[i];
            }

            var oracle = new TestOracle.World(
                (int)N, (int)Species, 7, scene.RMax, scene.Beta, p.Dt, p.Friction, p.ForceScale, matrix);
            oracle.Step();

            ulong size = swarm_layout_bytes(in p);
            void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
            try
            {
                Assert.Equal(0, swarm_init(arena, size, in p));
                swarm_step(arena, 1);
                var x = new float[N]; var y = new float[N];
                var vx = new float[N]; var vy = new float[N]; var s = new uint[N];
                Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, s));
                for (uint i = 0; i < N; i++)
                {
                    Assert.True(TorusDist(x[i], oracle.X[i]) < 1e-5f, $"x[{i}] rmax {scene.RMax}");
                    Assert.True(TorusDist(y[i], oracle.Y[i]) < 1e-5f, $"y[{i}] rmax {scene.RMax}");
                    Assert.True(MathF.Abs(vx[i] - oracle.Vx[i]) < 1e-4f, $"vx[{i}] rmax {scene.RMax}");
                    Assert.True(MathF.Abs(vy[i] - oracle.Vy[i]) < 1e-4f, $"vy[{i}] rmax {scene.RMax}");
                }
            }
            finally
            {
                NativeMemory.AlignedFree(arena);
            }
        }
    }

    private static float TorusDist(float a, float b)
    {
        float d = MathF.Abs(a - b);
        return d > 0.5f ? 1f - d : d;
    }

    private static (float[] x, float[] y) ReadPositions(void* arena)
    {
        var x = new float[N]; var y = new float[N];
        var vx = new float[N]; var vy = new float[N]; var s = new uint[N];
        // 0 = every id_out entry was in range, so each case also pins that the
        // counting sort kept id a permutation.
        Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, s));
        return (x, y);
    }
}
