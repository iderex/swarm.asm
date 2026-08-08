using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Subnormal-range parity between the scalar path and the reference, asserted
/// BIT-EXACT rather than inside an epsilon (#160).
///
/// The two implementations used to disagree here by construction. The seam
/// pins FTZ and DAZ; .NET honours IEEE subnormals; and every parity case in
/// the tree compares within 1e-4, which is thirty-four orders of magnitude
/// wider than the entire subnormal range. So the one place the engine and its
/// reference were known to differ was the one place nothing could see the
/// difference. <see cref="TestOracle.World.Ftz"/> closes that, and these are
/// the assertions that make the closure worth something.
///
/// Exact equality is the right bar and not an ambitious one: the scalar path
/// already reproduces the reference bit for bit at every count and horizon
/// measured in <see cref="OracleDivergenceSweep"/>. Anything less than exact
/// here would mean the model is wrong somewhere, not that the comparison is
/// too strict.
///
/// Both cases seed the state directly instead of decaying into the range,
/// because reaching it by friction alone takes thousands of steps and the
/// direct seed also covers DAZ on input, which decay does not reach until it
/// arrives.
///
/// WHICH CASES ACTUALLY CARRY THE CLAIM, measured rather than assumed. Running
/// this file against the unmodelled oracle reddens the two force-free cases
/// and TheSeededStateIsSubnormalAndTheFlushIsWhatEndsIt, and leaves the two
/// interacting ones green. So the rmax 0.2 pair does NOT discriminate: the
/// force term lifts every velocity out of the subnormal range on the first
/// step, and after that there is nothing left to flush. They are kept because
/// they cost nothing and they do enter the force expression with subnormal
/// velocities in the state, but they are not evidence for the model and this
/// sentence exists so nobody reads them as such.
/// </summary>
public sealed unsafe class SubnormalOracleParityTests
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

    private const uint N = 64, Species = 4;
    private const uint PathScalar = 3;
    private const float Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f;

    /// <summary>Smallest positive normal float; below it and above zero is
    /// the subnormal range.</summary>
    private const float MinNormal = 1.17549435e-38f;

    /// <summary>
    /// Every velocity starts strictly inside the subnormal range. 6e-39 is
    /// about half of MinNormal, so it is subnormal on the way in (DAZ) and
    /// every product of it with friction stays subnormal (FTZ).
    /// </summary>
    private const float SubnormalV = 6e-39f;

    private static float[] Matrix()
    {
        var m = new float[64];
        for (uint a = 0; a < Species; a++)
            for (uint b = 0; b < Species; b++)
                m[a * 8 + b] = MathF.Sin(a * 3.1f + b * 1.7f);
        return m;
    }

    private static SwarmParams Make(float rmax)
    {
        var p = new SwarmParams
        {
            Version = 1, N = N, SpeciesN = Species, Seed = 0x5EED,
            RMax = rmax, Beta = Beta, Dt = Dt, Friction = Friction, ForceScale = ForceScale,
            ForcePath = PathScalar, Flags = 0,   // brute force: no cell ids to keep consistent
        };
        var m = Matrix();
        for (int c = 0; c < 64; c++) p.Matrix[c] = m[c];
        return p;
    }

    /// <summary>The lattice both sides are seeded with: 8x8 positions, one
    /// subnormal velocity everywhere, species by index.</summary>
    private static (float X, float Y, uint S) Site(uint i) =>
        (0.0625f + (i % 8) * 0.125f, 0.0625f + (i / 8) * 0.125f, i % Species);

    /// <summary>
    /// Writes the lattice straight into bank OUT, which is what swarm_step
    /// copies into IN before the next pass. The offsets are the ones
    /// <see cref="SubnormalPinTests"/> uses.
    /// </summary>
    private static void SeedArena(void* arena)
    {
        uint padded = *(uint*)((byte*)arena + 32);
        long stride = padded * 4L;
        var x = (float*)((byte*)arena + 512);
        var y = (float*)((byte*)arena + 512 + stride);
        var vx = (float*)((byte*)arena + 512 + 2 * stride);
        var vy = (float*)((byte*)arena + 512 + 3 * stride);
        var sp = (uint*)((byte*)arena + 512 + 4 * stride);

        for (uint i = 0; i < N; i++)
        {
            var (px, py, s) = Site(i);
            x[i] = px; y[i] = py;
            vx[i] = SubnormalV; vy[i] = SubnormalV;
            sp[i] = s;
        }
    }

    private static void SeedOracle(TestOracle.World w)
    {
        for (uint i = 0; i < N; i++)
        {
            var (px, py, s) = Site(i);
            w.X[i] = px; w.Y[i] = py;
            w.Vx[i] = SubnormalV; w.Vy[i] = SubnormalV;
            w.S[i] = s;
        }
    }

    /// <summary>
    /// rmax 0.02 leaves every pair outside the interaction radius, so the whole
    /// velocity update is v * friction and the case is about FTZ on that one
    /// product and on the position update that follows it. rmax 0.2 puts the
    /// 0.125-spaced neighbours inside the radius, so the force expression runs
    /// with subnormal velocities in the state, which is where DAZ on input is
    /// what the two sides have to agree about.
    /// </summary>
    [Theory]
    [InlineData(0.02f, 4)]
    [InlineData(0.02f, 40)]
    [InlineData(0.2f, 4)]
    [InlineData(0.2f, 40)]
    public void ScalarPathMatchesTheOracleBitExactlyInTheSubnormalRange(float rmax, int steps)
    {
        _ = NativeKernel.Handle;

        var p = Make(rmax);
        var oracle = new TestOracle.World(
            (int)N, (int)Species, 0x5EED, rmax, Beta, Dt, Friction, ForceScale, Matrix());
        SeedOracle(oracle);
        for (int k = 0; k < steps; k++) oracle.Step();

        ulong size = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, size);
        void* a = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(a, size, in p));
            SeedArena(a);
            swarm_step(a, (uint)steps);

            var x = new float[N]; var y = new float[N];
            var vx = new float[N]; var vy = new float[N]; var sp = new uint[N];
            Assert.Equal(0, swarm_read_state(a, x, y, vx, vy, sp));

            for (uint i = 0; i < N; i++)
            {
                AssertSameBits(oracle.Vx[i], vx[i], $"vx[{i}] rmax {rmax} steps {steps}");
                AssertSameBits(oracle.Vy[i], vy[i], $"vy[{i}] rmax {rmax} steps {steps}");
                AssertSameBits(oracle.X[i], x[i], $"x[{i}] rmax {rmax} steps {steps}");
                AssertSameBits(oracle.Y[i], y[i], $"y[{i}] rmax {rmax} steps {steps}");
            }
        }
        finally { NativeMemory.AlignedFree(a); }
    }

    /// <summary>
    /// Non-vacuity for the scenario, not for the comparison. If the engine
    /// simply flushed everything to a clean zero on the first step, the
    /// equality above would hold for an uninteresting reason. This asserts the
    /// force-free case really does start subnormal and really does reach zero
    /// through the flush, so the range under test is the one named.
    /// </summary>
    [Fact]
    public void TheSeededStateIsSubnormalAndTheFlushIsWhatEndsIt()
    {
        _ = NativeKernel.Handle;

        Assert.True(SubnormalV > 0f && SubnormalV < MinNormal, "the seed must be subnormal");

        var p = Make(0.02f);
        var before = new TestOracle.World(
            (int)N, (int)Species, 0x5EED, 0.02f, Beta, Dt, Friction, ForceScale, Matrix());
        SeedOracle(before);

        // One step of v * friction on a subnormal is a subnormal result, which
        // the pin flushes to exactly zero rather than to a smaller subnormal.
        before.Step();
        Assert.Equal(0f, before.Vx[0]);
        Assert.Equal(0f, before.Vy[0]);

        // And without the model the same product is a subnormal, not zero, so
        // the two sides genuinely had something to disagree about.
        float unflushed = SubnormalV * Friction;
        Assert.True(unflushed > 0f && unflushed < MinNormal,
            "the IEEE result of the same product must still be subnormal, or this scenario "
                + "is not exercising the flush at all");
    }

    private static void AssertSameBits(float expected, float actual, string what)
    {
        Assert.True(
            BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
            $"{what}: reference {expected:E9} (0x{BitConverter.SingleToUInt32Bits(expected):X8}) "
                + $"vs engine {actual:E9} (0x{BitConverter.SingleToUInt32Bits(actual):X8}). "
                + "In the subnormal range these are asserted bit-exact, because the reference "
                + "models the seam's FTZ and DAZ pin. A difference here is either a change to "
                + "that pin or a gap in the model in TestOracle.World.");
    }
}
