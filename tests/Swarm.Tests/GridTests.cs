using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The M2 grid path (FLAG_GRID): the counting-sort reorders the population
/// cell-sorted, then the force pass consumes ONLY the 3x3 cell neighbourhood of
/// each particle (the O(n.k) speedup) instead of the whole array. Because
/// g = the largest power of two with 1/g >= rmax, every neighbour within the
/// rmax cutoff lies in that 3x3 block, so grid and brute sum the SAME non-zero
/// terms in a different order/lane assignment: the id-matched states must agree
/// to the FP-reordering floor. This cross-check now validates the neighbour set
/// AND the cell order (unlike the whole-array slice it replaces), including the
/// count-derived tail mask (an unmasked seam over-read would double-count a real
/// neighbour) and the torus seam split. Multi-step comparison is bounded by
/// chaos, so the 1-step tight check is primary; determinism WITHIN the grid path
/// is bit-exact and checked separately.
/// </summary>
public sealed unsafe class GridTests
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

    private const float Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f;

    // rmax picks the grid dimension g (largest power of two with 1/g >= rmax,
    // clamped [4, 512]): 0.24/0.25 -> g=4, 0.1 -> g=8, 0.05 -> g=16, 0.03 -> g=32.
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

    private static (float[] x, float[] y, float[] vx, float[] vy) RunRead(SwarmParams p, uint steps)
    {
        float[] rx = [], ry = [], rvx = [], rvy = [];
        ulong size = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, size);
        void* a = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(a, size, in p));
            swarm_step(a, steps);
            uint n = p.N;
            var x = new float[n]; var y = new float[n];
            var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];
            swarm_read_state(a, x, y, vx, vy, sp);
            rx = x; ry = y; rvx = vx; rvy = vy;
        }
        finally { NativeMemory.AlignedFree(a); }
        return (rx, ry, rvx, rvy);
    }

    private static float TorusDist(float a, float b)
    {
        float d = MathF.Abs(a - b);
        return MathF.Min(d, 1f - d);
    }

    private static (double pos, double vel) MaxDrift(SwarmParams brute, SwarmParams grid, uint steps)
    {
        var (bx, by, bvx, bvy) = RunRead(brute, steps);
        var (gx, gy, gvx, gvy) = RunRead(grid, steps);
        double mp = 0, mv = 0;
        for (int i = 0; i < brute.N; i++)
        {
            mp = Math.Max(mp, TorusDist(bx[i], gx[i]));
            mp = Math.Max(mp, TorusDist(by[i], gy[i]));
            mv = Math.Max(mv, Math.Abs(bvx[i] - gvx[i]));
            mv = Math.Max(mv, Math.Abs(bvy[i] - gvy[i]));
        }
        return (mp, mv);
    }

    // Primary gate. After ONE step the 3x3-neighbourhood force must match the
    // brute reference, id-matched, over ranges of n / species / seed / rmax (so
    // g varies), both the AVX2 (1) and scalar (3) paths, including the boundary
    // rmax (0.25 -> g=4, the tightest cells), n that is not a lane multiple, and
    // the maximum species count. g=4 packs many particles per cell, so most
    // particles straddle a column/row seam and their runs are far from a
    // multiple of 8 - the tail mask and the seam split are exercised throughout.
    // The FP-reordering floor is ~1e-7 pos / ~3e-6 vel (measured); a wrong
    // neighbour set, a dropped/duplicated particle, or a tail-mask double-count
    // shifts a force by O(force_scale*dt) and blows past these bounds at step 1.
    [Theory]
    [InlineData(500u, 4u, 0x1234ul, 0.24f, 1u)]   // g=4 dense, AVX2
    [InlineData(500u, 4u, 0x1234ul, 0.24f, 3u)]   // g=4 dense, scalar
    [InlineData(500u, 4u, 0x1234ul, 0.10f, 1u)]   // g=8
    [InlineData(1000u, 6u, 0xBEEFul, 0.05f, 1u)]  // g=16
    [InlineData(1000u, 6u, 0xBEEFul, 0.03f, 3u)]  // g=32, scalar
    [InlineData(4096u, 4u, 0x5EEDul, 0.10f, 1u)]  // larger n, g=8
    [InlineData(2000u, 8u, 0x0099ul, 0.24f, 1u)]  // g=4 dense, 8 species, AVX2
    [InlineData(2000u, 8u, 0x0099ul, 0.24f, 3u)]  // g=4 dense, 8 species, scalar
    [InlineData(31u, 8u, 0x0099ul, 0.10f, 3u)]    // tiny, not a lane multiple
    [InlineData(777u, 5u, 0x0ABCul, 0.05f, 1u)]   // not a lane multiple, g=16
    [InlineData(600u, 3u, 0xF00Dul, 0.25f, 1u)]   // boundary rmax (max), g=4
    public void GridNeighbourhoodMatchesBruteOneStep(uint n, uint species, ulong seed, float rmax, uint forcePath)
    {
        _ = NativeKernel.Handle;
        var (pos, vel) = MaxDrift(Make(n, species, seed, rmax, forcePath, 0),
                                  Make(n, species, seed, rmax, forcePath, FlagGrid), 1);
        Assert.True(pos < 1e-5, $"position drift {pos:E3} exceeds the FP-reordering floor");
        Assert.True(vel < 1e-4, $"velocity drift {vel:E3} exceeds the FP-reordering floor");
    }

    // Secondary net. Over a few steps a wrong neighbour set diverges immediately
    // (its error is O(force) per step, then amplified), while a correct
    // neighbourhood force only drifts by FP reordering. Chaos bounds how far a
    // cross-path comparison can be pushed, so the epsilon is looser than the
    // 1-step gate and the step count stays low; measured drift at 3 steps is
    // ~9e-7 pos / ~4e-5 vel, so these bounds keep >20x margin over the floor and
    // far below any real-bug signal.
    [Theory]
    [InlineData(500u, 4u, 0x1234ul, 0.24f, 1u)]
    [InlineData(2000u, 8u, 0x0099ul, 0.24f, 1u)]
    [InlineData(1000u, 6u, 0xBEEFul, 0.05f, 3u)]
    [InlineData(4096u, 4u, 0x5EEDul, 0.10f, 1u)]
    public void GridNeighbourhoodMatchesBruteMultiStep(uint n, uint species, ulong seed, float rmax, uint forcePath)
    {
        _ = NativeKernel.Handle;
        var (pos, vel) = MaxDrift(Make(n, species, seed, rmax, forcePath, 0),
                                  Make(n, species, seed, rmax, forcePath, FlagGrid), 3);
        Assert.True(pos < 1e-4, $"position drift {pos:E3} over 3 steps");
        Assert.True(vel < 1e-3, $"velocity drift {vel:E3} over 3 steps");
    }

    // Targeted tail-mask / seam over-read gate. g=4 gives 16 cells; at these n a
    // cell holds far more than 8 particles, so a row's 3-column run is long and
    // almost never a multiple of 8 - its final AVX2 vector over-reads past the
    // run end. Half the columns (cx in {0, g-1}) straddle the torus seam, so the
    // over-read lands in the next cell's real particles: without the count mask
    // that genuine neighbour is double-counted and the id-matched state drifts
    // past the floor. (Verified locally: deliberately dropping the mask fails
    // this test.) Both paths.
    [Theory]
    [InlineData(500u, 4u, 0x1234ul, 1u)]
    [InlineData(500u, 4u, 0x1234ul, 3u)]
    [InlineData(1500u, 6u, 0xC0FFEEul, 1u)]
    [InlineData(1500u, 6u, 0xC0FFEEul, 3u)]
    public void TailMaskSeamOverReadMatchesBrute(uint n, uint species, ulong seed, uint forcePath)
    {
        _ = NativeKernel.Handle;
        var (pos, vel) = MaxDrift(Make(n, species, seed, 0.24f, forcePath, 0),
                                  Make(n, species, seed, 0.24f, forcePath, FlagGrid), 1);
        Assert.True(pos < 1e-5, $"position drift {pos:E3} - tail mask / seam over-read");
        Assert.True(vel < 1e-4, $"velocity drift {vel:E3} - tail mask / seam over-read");
    }

    // The stable counting sort pins the iteration order as a pure function of
    // (seed, params, step count): the grid path is bit-exact run-to-run,
    // including the neighbourhood force.
    [Fact]
    public void GridIsDeterministic()
    {
        _ = NativeKernel.Handle;
        var p = Make(1000, 4, 0xC0FFEE, 0.08f, 1, FlagGrid);
        var a = RunRead(p, 12);
        var b = RunRead(p, 12);
        Assert.Equal(a.x, b.x);
        Assert.Equal(a.y, b.y);
        Assert.Equal(a.vx, b.vx);
        Assert.Equal(a.vy, b.vy);
    }

    // The neighbourhood pass is a pure map (reads frozen IN + cell_start, writes
    // disjoint OUT[i]; each particle's 3x3 accumulation is independent of the
    // [first, last) split), so in grid mode too pass(0, n) must equal
    // pass(0, k) then pass(k, n) bit-for-bit - the M3 threading seam.
    [Theory]
    [InlineData(500u, 4u, 0x1234ul, 0.24f, 1u, 137u)]
    [InlineData(2000u, 8u, 0x0099ul, 0.24f, 1u, 8u)]
    [InlineData(1000u, 6u, 0xBEEFul, 0.05f, 3u, 500u)]
    [InlineData(777u, 5u, 0x0ABCul, 0.05f, 3u, 1u)]
    public void GridPassSplitInvariance(uint n, uint species, ulong seed, float rmax, uint forcePath, uint k)
    {
        _ = NativeKernel.Handle;
        var p = Make(n, species, seed, rmax, forcePath, FlagGrid);

        var whole = ReadAfter(p, a =>
        {
            swarm_build((void*)a);
            swarm_pass((void*)a, 0, n);
        });
        var split = ReadAfter(p, a =>
        {
            swarm_build((void*)a);
            swarm_pass((void*)a, 0, k);
            swarm_pass((void*)a, k, n);
        });
        Assert.Equal(whole, split);
    }

    private static float[] ReadAfter(SwarmParams p, Action<nint> act)
    {
        float[] result = [];
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));
            act((nint)arena);
            uint n = p.N;
            var x = new float[n]; var y = new float[n];
            var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];
            swarm_read_state(arena, x, y, vx, vy, sp);
            result = new float[n * 4];
            for (uint i = 0; i < n; i++)
            {
                result[i * 4 + 0] = x[i];
                result[i * 4 + 1] = y[i];
                result[i * 4 + 2] = vx[i];
                result[i * 4 + 3] = vy[i];
            }
        }
        finally { NativeMemory.AlignedFree(arena); }
        return result;
    }

    // FLAG_GRID (bit 0) is accepted; every other flag bit is reserved and
    // rejected fail-closed (init leaves the arena untouched).
    [Theory]
    [InlineData(1u, true)] // FLAG_GRID
    [InlineData(0u, true)] // brute
    [InlineData(2u, false)] // reserved bit
    [InlineData(0x80000000u, false)] // high reserved bit
    public void ReservedFlagBitsAreRejected(uint flags, bool valid)
    {
        _ = NativeKernel.Handle;
        var p = Make(100, 4, 0x1, 0.08f, 1, flags);
        ulong size = swarm_layout_bytes(in p);
        if (valid)
        {
            Assert.NotEqual(0ul, size);
            void* a = NativeMemory.AlignedAlloc((nuint)size, 64);
            try { Assert.Equal(0, swarm_init(a, size, in p)); }
            finally { NativeMemory.AlignedFree(a); }
        }
        else
        {
            Assert.Equal(0ul, size); // layout rejects -> caller cannot even size the arena
        }
    }
}
