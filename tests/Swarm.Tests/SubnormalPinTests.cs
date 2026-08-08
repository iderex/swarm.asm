using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The FTZ/DAZ pin, exercised where it actually bites (issue #159).
///
/// The seam pins MXCSR to <c>SEAM_MXCSR</c> = 0x9FC0 across every core, which
/// sets flush-to-zero on subnormal results and denormals-are-zero on subnormal
/// inputs. Neither is IEEE behaviour, and friction multiplies every velocity
/// by a factor below one every frame, so the subnormal range is not an exotic
/// corner: it is where a decaying velocity ends up.
///
/// Every other case in this harness lives well above it. A parity case
/// compares against the C# oracle within an epsilon of 1e-4, and the entire
/// subnormal range is smaller than 1.2e-38, so an epsilon comparison cannot
/// tell a flushed velocity from a preserved one. That is why the assertions
/// here are exact rather than epsilon-bounded, and why they are asm-against-
/// itself rather than asm-against-oracle: this file predates the oracle's
/// FTZ/DAZ model. That model landed with #160, and the asm-against-oracle
/// half now lives in SubnormalOracleParityTests; what stays here is the
/// asm-against-itself half, which asks a different question and would still
/// be worth asking if the oracle vanished.
///
/// Two of the four theories here fail when the pin is reverted, and two do not.
/// Verified by setting SEAM_MXCSR to 0x1F80, the x64 default with neither bit
/// set, rebuilding and re-running: 8 of the 22 cases go red, and they are
/// every case of <see cref="SubnormalSeedVelocityIsFlushedToExactlyZero"/> and
/// <see cref="FrictionDecayIntoTheSubnormalRangeIsFlushed"/>. Those two carry
/// the pin.
///
/// The other two CANNOT be pin-sensitive and are not claimed to be.
/// <see cref="SubnormalStateIsDeterministic"/> compares a run against a second
/// run of the same build, and <see cref="GridAndBruteAgreeOnASubnormalState"/>
/// compares two neighbourhood modes inside one build. A control word is global
/// to the process, so reverting it moves both sides of either comparison
/// equally and neither can notice. What they cover is a different property:
/// that the engine is bit-exact in this range at all, which is the property a
/// flush-to-zero path could plausibly lose by taking a data-dependent branch.
/// Issue #159 asks for both kinds and for every case to fail with the pin off;
/// the second half is impossible for these two, and saying so is the honest
/// version of meeting it.
/// </summary>
public sealed unsafe class SubnormalPinTests
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

    private const uint FlagGrid = 1;
    private const uint N = 64, Species = 4;

    // The lattice below spaces particles 0.125 apart on both axes and RMax is
    // 0.02, so no pair is ever inside the interaction radius and the force
    // term is exactly zero for every particle. That is deliberate: it leaves
    // v' = v * Friction as the whole of the velocity update, which is the one
    // arithmetic operation this file is about. It also makes the case immune
    // to which neighbourhood the grid hands the pass, because a neighbour
    // outside RMax contributes zero however it was found.
    private const float RMax = 0.02f, Beta = 0.3f, Dt = 0.02f;
    private const float Friction = 0.71f, ForceScale = 10f;

    // Smallest positive normal float. Anything strictly below this and above
    // zero is subnormal, which is the range FTZ and DAZ are about.
    private const float MinNormal = 1.17549435e-38f;

    private static SwarmParams Make(uint forcePath, uint flags)
    {
        var p = new SwarmParams
        {
            Version = 1, N = N, SpeciesN = Species, Seed = 0x5EED,
            RMax = RMax, Beta = Beta, Dt = Dt, Friction = Friction, ForceScale = ForceScale,
            ForcePath = forcePath, Flags = flags,
        };
        for (uint a = 0; a < Species; a++)
            for (uint b = 0; b < Species; b++)
                p.Matrix[(int)(a * 8 + b)] = MathF.Sin(a * 3.1f + b * 1.7f);
        return p;
    }

    /// <summary>
    /// Writes an 8x8 lattice of positions and a uniform velocity straight into
    /// bank OUT, the way PlotTests seeds known positions. swarm_step copies
    /// OUT into IN before every pass, so this is the state the next step reads.
    ///
    /// The per-particle cell id is written too. grid_sort keys off the cached
    /// AR_CELLID array rather than recomputing it, so a state written behind
    /// swarm_init's back would otherwise be sorted by the ids seeded from the
    /// positions this overwrites. The force term is zero either way here, but
    /// a stale key is the kind of thing that makes a later reader distrust the
    /// case rather than the code.
    /// </summary>
    private static void SeedLattice(void* arena, float v)
    {
        uint padded = *(uint*)((byte*)arena + 32);
        uint g = *(uint*)((byte*)arena + 36);
        long stride = padded * 4L;
        var x = (float*)((byte*)arena + 512);
        var y = (float*)((byte*)arena + 512 + stride);
        var vx = (float*)((byte*)arena + 512 + 2 * stride);
        var vy = (float*)((byte*)arena + 512 + 3 * stride);
        var cell = (uint*)((byte*)arena + 512 + 12 * stride);

        for (uint i = 0; i < N; i++)
        {
            float px = 0.0625f + (i % 8) * 0.125f;
            float py = 0.0625f + (i / 8) * 0.125f;
            x[i] = px;
            y[i] = py;
            vx[i] = v;
            vy[i] = v;

            uint cx = (uint)(int)(px * g) & (g - 1);
            uint cy = (uint)(int)(py * g) & (g - 1);
            cell[i] = cy * g + cx;
        }
    }

    private static (float[] vx, float[] vy) RunSeeded(SwarmParams p, float v0, uint steps)
    {
        ulong size = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, size);
        void* a = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(a, size, in p));
            SeedLattice(a, v0);
            swarm_step(a, steps);

            var x = new float[N]; var y = new float[N];
            var vx = new float[N]; var vy = new float[N]; var sp = new uint[N];
            Assert.Equal(0, swarm_read_state(a, x, y, vx, vy, sp));
            return (vx, vy);
        }
        finally { NativeMemory.AlignedFree(a); }
    }

    // forcePath 1 = AVX2, 3 = the scalar reference; flags 0 = brute, 1 = grid.
    // Every combination, because the pin is a property of the seam rather than
    // of one body, and a path reached without a seam would keep the caller's
    // control word.
    public static TheoryData<uint, uint> Paths => new()
    {
        { 1u, 0u }, { 3u, 0u }, { 1u, FlagGrid }, { 3u, FlagGrid },
    };

    /// <summary>
    /// DAZ on input: a velocity that is already subnormal when the pass reads
    /// it. Under the pin the multiply sees zero and writes zero; without it the
    /// product is another subnormal and survives.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void SubnormalSeedVelocityIsFlushedToExactlyZero(uint forcePath, uint flags)
    {
        _ = NativeKernel.Handle;
        const float seedV = 1e-40f;                     // subnormal by construction
        Assert.True(seedV > 0f && seedV < MinNormal, "the seed value must be subnormal");

        var (vx, vy) = RunSeeded(Make(forcePath, flags), seedV, 1);

        for (uint i = 0; i < N; i++)
        {
            Assert.True(vx[i] == 0f, $"vx[{i}] = {vx[i]:E} after one step, path {forcePath} flags {flags}");
            Assert.True(vy[i] == 0f, $"vy[{i}] = {vy[i]:E} after one step, path {forcePath} flags {flags}");
        }
    }

    /// <summary>
    /// FTZ on output, reached the way the engine actually reaches it: a normal
    /// velocity decayed by friction until the product falls below the smallest
    /// normal float.
    ///
    /// 1e-30 * 0.71^k crosses 1.17e-38 at k = 53, so step 1 is still normal and
    /// nonzero and step 60 is not. Both are asserted. Without the first, a
    /// build that zeroed velocities for some unrelated reason would satisfy the
    /// second and the case would prove nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void FrictionDecayIntoTheSubnormalRangeIsFlushed(uint forcePath, uint flags)
    {
        _ = NativeKernel.Handle;
        const float seedV = 1e-30f;
        Assert.True(seedV > MinNormal, "the seed value must start normal");

        var early = RunSeeded(Make(forcePath, flags), seedV, 1);
        for (uint i = 0; i < N; i++)
        {
            Assert.True(early.vx[i] != 0f,
                $"vx[{i}] was already zero after one step, path {forcePath} flags {flags}: " +
                "the control that keeps the assertion below from being vacuous");
            Assert.True(MathF.Abs(early.vx[i]) >= MinNormal,
                $"vx[{i}] = {early.vx[i]:E} is not still normal after one step");
        }

        var late = RunSeeded(Make(forcePath, flags), seedV, 60);
        for (uint i = 0; i < N; i++)
        {
            Assert.True(late.vx[i] == 0f,
                $"vx[{i}] = {late.vx[i]:E} after 60 steps, path {forcePath} flags {flags}");
            Assert.True(late.vy[i] == 0f,
                $"vy[{i}] = {late.vy[i]:E} after 60 steps, path {forcePath} flags {flags}");
        }
    }

    /// <summary>
    /// Determinism in the range the pin governs, at the step counts issue #159
    /// names: same seeded state, same result, bit for bit, per code path. Step
    /// 1 is above the range, 60 is through it, 600 is far past it.
    /// </summary>
    [Theory]
    [InlineData(1u, 0u, 1u)]
    [InlineData(3u, 0u, 1u)]
    [InlineData(1u, FlagGrid, 1u)]
    [InlineData(3u, FlagGrid, 1u)]
    [InlineData(1u, 0u, 60u)]
    [InlineData(3u, 0u, 60u)]
    [InlineData(1u, FlagGrid, 60u)]
    [InlineData(3u, FlagGrid, 60u)]
    [InlineData(1u, 0u, 600u)]
    [InlineData(3u, 0u, 600u)]
    [InlineData(1u, FlagGrid, 600u)]
    [InlineData(3u, FlagGrid, 600u)]
    public void SubnormalStateIsDeterministic(uint forcePath, uint flags, uint steps)
    {
        _ = NativeKernel.Handle;
        var p = Make(forcePath, flags);
        var a = RunSeeded(p, 1e-40f, steps);
        var b = RunSeeded(p, 1e-40f, steps);
        Assert.Equal(a.vx, b.vx);
        Assert.Equal(a.vy, b.vy);
    }

    /// <summary>
    /// The grid and brute neighbourhoods agree on a subnormal state, exactly
    /// rather than within an epsilon. Nothing here is inside RMax, so the two
    /// modes examine different candidate sets and must still produce the same
    /// velocities; an epsilon comparison in this range would pass on any pair
    /// of values at all.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public void GridAndBruteAgreeOnASubnormalState(uint forcePath)
    {
        _ = NativeKernel.Handle;
        var brute = RunSeeded(Make(forcePath, 0), 1e-40f, 60);
        var grid = RunSeeded(Make(forcePath, FlagGrid), 1e-40f, 60);
        Assert.Equal(brute.vx, grid.vx);
        Assert.Equal(brute.vy, grid.vy);
    }
}
