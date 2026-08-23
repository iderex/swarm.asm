using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The managed baseline (#153) is published as a competitor number, so the
/// thing a reader is entitled to challenge is not its speed but whether it did
/// the same work. These tests are that challenge, answered in the suite rather
/// than in prose: the baseline is held to <see cref="TestOracle"/>, the same
/// reference the kernel is checked against, over one force+integrate pass.
///
/// A baseline that got faster by dropping a branch, skipping the wrap, or
/// truncating the neighbour sweep reds here. That is the property, and it is
/// what makes the published ratio a comparison instead of an assertion.
///
/// THE SUBJECT IS THE FORCE LAW, NOT ONE SCENE. The scenes below are this
/// test's own fixtures rather than a copy of the benchmark's, because a copy
/// would be a second statement of the scene that can drift from the first. Two
/// of them are chosen to put a different share of the population on each side
/// of the beta knee, so neither branch of the force curve can go unexercised.
///
/// The baseline needs no native library, so nothing here skips when Smart App
/// Control blocks a load.
/// </summary>
public class ManagedBaselineParityTests
{
    /// <summary>
    /// The whole difference between the two implementations is FTZ: the seam
    /// pins MXCSR to 0x9FC0 and TestOracle models that flush, while plain
    /// managed code cannot and does not. Outside the subnormal range the two
    /// are the same arithmetic in the same order, so the expected divergence
    /// is zero and the measured one is asserted at zero rather than inside a
    /// tolerance that would hide a real difference.
    /// </summary>
    private const float Tolerance = 0f;

    public static TheoryData<string, int, int, ulong, float, float, float, float, float> Scenes =>
        new()
        {
            // The benchmark's parameters by value, so the published row's
            // configuration is one of the ones held to the oracle.
            { "bench", 1024, 6, 0x5EED, 0.05f, 0.3f, 0.02f, 0.71f, 10f },
            // A wide rmax and a high beta: most pairs in range, most of them
            // inside the knee, so the repulsion branch dominates.
            { "wide-knee", 512, 3, 0xC0FFEE, 0.25f, 0.7f, 0.01f, 0.9f, 4f },
            // A narrow rmax and a low beta: few pairs in range and nearly all
            // of them past the knee, so the matrix branch dominates.
            { "narrow-tail", 512, 8, 0x1234, 0.03f, 0.1f, 0.05f, 0.5f, 25f },
        };

    [Theory]
    [MemberData(nameof(Scenes))]
    public void SoaBaselineMatchesTheOracleOverOnePass(
        string name, int n, int speciesN, ulong seed,
        float rmax, float beta, float dt, float friction, float forceScale)
    {
        float[] matrix = Matrix();
        var oracle = new TestOracle.World(
            n, speciesN, seed, rmax, beta, dt, friction, forceScale, matrix);
        var baseline = new ManagedBaseline.Soa(new ManagedBaseline.Scene(
            n, speciesN, seed, rmax, beta, dt, friction, forceScale, matrix));

        oracle.Step();
        baseline.Pass();

        AssertAgrees(name, n, oracle, baseline.OutX, baseline.OutY, baseline.OutVx, baseline.OutVy);
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void AosBaselineMatchesTheOracleOverOnePass(
        string name, int n, int speciesN, ulong seed,
        float rmax, float beta, float dt, float friction, float forceScale)
    {
        float[] matrix = Matrix();
        var oracle = new TestOracle.World(
            n, speciesN, seed, rmax, beta, dt, friction, forceScale, matrix);
        var baseline = new ManagedBaseline.Aos(new ManagedBaseline.Scene(
            n, speciesN, seed, rmax, beta, dt, friction, forceScale, matrix));

        oracle.Step();
        baseline.Pass();

        float[] x = new float[n], y = new float[n], vx = new float[n], vy = new float[n];
        baseline.CopyOut(x, y, vx, vy);

        AssertAgrees(name, n, oracle, x, y, vx, vy);
    }

    /// <summary>
    /// The two layouts are the same arithmetic over the same values in the same
    /// order, so they are held to each other exactly. Without this, a layout
    /// that quietly reordered the accumulation would still pass the oracle
    /// comparison above at its tolerance, and the report would be quoting the
    /// faster of two things that are not the same computation.
    /// </summary>
    [Fact]
    public void TheTwoLayoutsAgreeBitForBit()
    {
        const int n = 1024;
        float[] matrix = Matrix();
        var scene = new ManagedBaseline.Scene(n, 6, 0x5EED, 0.05f, 0.3f, 0.02f, 0.71f, 10f, matrix);

        var soa = new ManagedBaseline.Soa(scene);
        var aos = new ManagedBaseline.Aos(scene);
        soa.Pass();
        aos.Pass();

        float[] x = new float[n], y = new float[n], vx = new float[n], vy = new float[n];
        aos.CopyOut(x, y, vx, vy);

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(soa.OutX[i]), BitConverter.SingleToInt32Bits(x[i]));
            Assert.Equal(BitConverter.SingleToInt32Bits(soa.OutY[i]), BitConverter.SingleToInt32Bits(y[i]));
            Assert.Equal(BitConverter.SingleToInt32Bits(soa.OutVx[i]), BitConverter.SingleToInt32Bits(vx[i]));
            Assert.Equal(BitConverter.SingleToInt32Bits(soa.OutVy[i]), BitConverter.SingleToInt32Bits(vy[i]));
        }
    }

    /// <summary>
    /// The population itself, before any force is computed. The comparison is
    /// only between two engines if both start from the same particles, and the
    /// seeded draw is the one place where that could silently stop being true.
    /// </summary>
    [Fact]
    public void TheBaselineDrawsTheSamePopulationAsTheOracle()
    {
        const int n = 4096;
        const int speciesN = 6;
        const ulong seed = 0x5EED;

        var baseline = new ManagedBaseline.Soa(new ManagedBaseline.Scene(
            n, speciesN, seed, 0.05f, 0.3f, 0.02f, 0.71f, 10f, Matrix()));

        var rng = new TestOracle.SplitMix64(seed);
        for (int i = 0; i < n; i++)
        {
            var (x, y, s) = TestOracle.DrawParticle(rng, speciesN);
            Assert.Equal(x, baseline.InX[i]);
            Assert.Equal(y, baseline.InY[i]);
            Assert.Equal((int)s, baseline.InSpecies[i]);
        }
    }

    private static void AssertAgrees(
        string name, int n, TestOracle.World oracle,
        float[] x, float[] y, float[] vx, float[] vy)
    {
        float worst = 0f;
        int worstIndex = -1;
        string worstField = "";

        for (int i = 0; i < n; i++)
        {
            Track(oracle.X[i], x[i], i, "x");
            Track(oracle.Y[i], y[i], i, "y");
            Track(oracle.Vx[i], vx[i], i, "vx");
            Track(oracle.Vy[i], vy[i], i, "vy");
        }

        Assert.True(
            worst <= Tolerance,
            $"scene {name}: managed baseline diverges from the oracle by {worst:E9} " +
            $"at {worstField}[{worstIndex}] (tolerance {Tolerance:E9})");

        void Track(float expected, float actual, int i, string field)
        {
            float d = MathF.Abs(expected - actual);
            if (d > worst)
            {
                worst = d;
                worstIndex = i;
                worstField = field;
            }
        }
    }

    /// <summary>The 8x8 attraction matrix the benchmark uses, varied and
    /// deterministic so both branches of the force curve see a range of
    /// coefficients including negative ones.</summary>
    private static float[] Matrix()
    {
        var m = new float[64];
        for (int a = 0; a < 8; a++)
        {
            for (int b = 0; b < 8; b++)
            {
                m[(a * 8) + b] = MathF.Sin((a * 3.1f) + (b * 1.7f));
            }
        }
        return m;
    }
}
