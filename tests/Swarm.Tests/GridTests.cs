using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The M2 grid build (FLAG_GRID): the counting-sort reorders the population
/// cell-sorted before the pass. Slice 1 keeps the force pass whole-array, so
/// grid mode computes the SAME neighbour set as brute — only the summation
/// order differs — and must match brute within the FP-reordering epsilon after
/// one step (particle life is chaotic, so a multi-step cross-path comparison is
/// meaningless; determinism WITHIN the grid path is checked separately). The
/// grid neighbourhood force (the O(n.k) speedup) is a later slice.
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
    private static extern void swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    private const float RMax = 0.08f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f;

    private static SwarmParams Make(uint n, uint species, ulong seed, uint forcePath, uint flags)
    {
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = species, Seed = seed,
            RMax = RMax, Beta = Beta, Dt = Dt, Friction = Friction, ForceScale = ForceScale,
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

    // Grid and brute compute the same neighbour set (whole-array in slice 1);
    // only the cell-sorted summation order differs, so after ONE step the
    // id-matched states agree to the FP-reordering floor (observed ~1e-7).
    [Theory]
    [InlineData(500u, 4u, 0x1234ul, 1u)]
    [InlineData(500u, 4u, 0x1234ul, 3u)]
    [InlineData(1000u, 6u, 0xBEEFul, 1u)]
    [InlineData(4096u, 4u, 0x5EEDul, 1u)]
    [InlineData(31u, 8u, 0x99ul, 3u)] // not a lane multiple, high species count
    public void GridBuildMatchesBruteAfterOneStep(uint n, uint species, ulong seed, uint forcePath)
    {
        _ = NativeKernel.Handle;
        var (bx, by, bvx, bvy) = RunRead(Make(n, species, seed, forcePath, 0), 1);
        var (gx, gy, gvx, gvy) = RunRead(Make(n, species, seed, forcePath, FlagGrid), 1);

        double maxPos = 0, maxVel = 0;
        for (int i = 0; i < n; i++)
        {
            maxPos = Math.Max(maxPos, Math.Abs(bx[i] - gx[i]));
            maxPos = Math.Max(maxPos, Math.Abs(by[i] - gy[i]));
            maxVel = Math.Max(maxVel, Math.Abs(bvx[i] - gvx[i]));
            maxVel = Math.Max(maxVel, Math.Abs(bvy[i] - gvy[i]));
        }
        // Generous vs the observed ~1e-7: a real build bug (wrong particle set,
        // dropped/duplicated particle, corrupted id) shifts forces well past this.
        Assert.True(maxPos < 1e-4, $"position drift {maxPos:E3} exceeds the FP-reordering floor");
        Assert.True(maxVel < 1e-4, $"velocity drift {maxVel:E3} exceeds the FP-reordering floor");
    }

    // The stable counting sort pins the iteration order as a pure function of
    // (seed, params, step count): grid mode is bit-exact run-to-run.
    [Fact]
    public void GridIsDeterministic()
    {
        _ = NativeKernel.Handle;
        var p = Make(1000, 4, 0xC0FFEE, 1, FlagGrid);
        var a = RunRead(p, 12);
        var b = RunRead(p, 12);
        Assert.Equal(a.x, b.x);
        Assert.Equal(a.y, b.y);
        Assert.Equal(a.vx, b.vx);
        Assert.Equal(a.vy, b.vy);
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
        var p = Make(100, 4, 0x1, 1, flags);
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
