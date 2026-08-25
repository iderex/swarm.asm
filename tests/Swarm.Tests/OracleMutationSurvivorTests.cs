using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The disposition of every survivor in the oracle's mutation baseline (#151),
/// and the assertions that kill the two a test can reach.
///
/// THE BASELINE IS THE SCHEDULED RUN, NOT A LOCAL ONE. The run recorded on
/// #150 reports 128 mutants over <c>tests/Swarm.Oracle/TestOracle.cs</c>: 106
/// killed, 8 ignored by Stryker's own block filter, 10 compile errors, 3
/// survived and 1 reached by no test. The last four are what this file is
/// about, and each one is dispositioned rather than counted:
///
/// <list type="table">
/// <item><description>line 32, <c>v1 >> 40</c> to <c>v1 >>> 40</c> - ACCEPTED,
/// equivalent. <c>v1</c> is a <c>ulong</c>, so both operators are the same
/// logical shift and no program can tell them apart.</description></item>
/// <item><description>line 112, <c>v &lt; 0f</c> to <c>v &lt;= 0f</c> -
/// ACCEPTED, equivalent. The enclosing branch is guarded by <c>v != 0f</c>, so
/// the case that separates the two operators cannot reach
/// them.</description></item>
/// <item><description>line 112, <c>-0f</c> to <c>+0f</c> - KILLED here by
/// <see cref="FlushingASubnormalKeepsTheSignOfTheResult"/>.</description></item>
/// <item><description>line 146, <c>r2 >= rmax2</c> to <c>r2 > rmax2</c> -
/// KILLED here by <see cref="APairAtExactlyRmaxContributesNothing"/>.</description></item>
/// <item><description>line 150, <c>xn &lt; _beta</c> to <c>xn &lt;= _beta</c> -
/// KILLED here by <see cref="AtExactlyTheKneeTheMatrixApplies"/>. This one is
/// not in the scheduled baseline, which reports it killed; a local run of the
/// same script on the reference machine reports it as a survivor. Killing it
/// costs one assertion and settles the disagreement instead of arguing about
/// which run to believe.</description></item>
/// </list>
///
/// WHY THE TWO ACCEPTED ONES ARE NOT SUPPRESSED WITH A STRYKER COMMENT, which
/// is the shape the question invites. <c>// Stryker disable once</c> takes a
/// LINE and a MUTATOR KIND, never a single mutant, and on both of these lines
/// a killed mutant shares the kind. Line 32 carries <c>v1 &lt;&lt; 40</c> and
/// line 112 carries <c>v > 0f</c>, both killed today; suppressing the
/// survivors would suppress those with them and trade two explained survivors
/// for two deleted guards. So the acceptance is recorded in
/// <see cref="Accepted"/> instead, where the next run is compared against it
/// by reading, and <see cref="AcceptedSurvivorsStillDescribeTheirSource"/>
/// refuses the drift that would make that record quietly wrong.
///
/// A NEW SURVIVOR IS DISTINGUISHABLE FROM A KNOWN ONE BY THAT LIST AND BY
/// NOTHING ELSE. The mutation job is deliberately not a gate and does not fail
/// on a score, so nothing refuses a survivor and nothing here pretends to.
/// </summary>
public sealed class OracleMutationSurvivorTests
{
    private const float Dt = 0.02f;
    private const float Friction = 0.71f;
    private const float ForceScale = 10f;

    /// <summary>The one species pair the two-particle scenes use, so an
    /// interaction that happens produces a force rather than cancelling to
    /// nothing for an unrelated reason.</summary>
    private static float[] Matrix(float entry)
    {
        var m = new float[64];
        m[0] = entry;
        return m;
    }

    /// <summary>
    /// Two particles on one row, <paramref name="separation"/> apart in x,
    /// stepped once. Positions are seeded directly because the interesting
    /// separations are exact bit patterns rather than anything a seed reaches.
    /// </summary>
    private static TestOracle.World SteppedPair(
        float separation, float beta, float rmax, float matrixEntry = 1.0f)
    {
        var w = new TestOracle.World(
            n: 2, speciesN: 1, seed: 0x5EED, rmax: rmax, beta: beta,
            dt: Dt, friction: Friction, forceScale: ForceScale, matrix8x8: Matrix(matrixEntry));
        w.X[0] = 0f;
        w.Y[0] = 0f;
        w.X[1] = separation;
        w.Y[1] = 0f;
        w.Vx[0] = 0f;
        w.Vx[1] = 0f;
        w.Vy[0] = 0f;
        w.Vy[1] = 0f;
        w.Step();
        return w;
    }

    /// <summary>
    /// The interaction radius is half-open. A pair exactly <c>rmax</c> apart is
    /// outside it and contributes nothing, and the engine says the same thing
    /// on both of its paths: the scalar path skips on <c>jae</c> after
    /// <c>comiss r2, rmax2</c> (<c>src/kernel/step.inc:586-587</c>) and the
    /// AVX2 path keeps only <c>r2 &lt; rmax2</c> (<c>vcmpps ... 0x11</c>,
    /// <c>src/kernel/step.inc:678</c>).
    ///
    /// WHY THIS IS NOT SELF-EVIDENT, and why the sweep is over beta. At exactly
    /// <c>rmax</c> the normalised distance is 1.0, which is the far end of the
    /// tent, and for most beta the tent evaluates to exactly zero there - so
    /// including the pair would produce a zero force anyway and the boundary
    /// would hold by arithmetic accident rather than by the comparison.
    /// Measured over 9,998 values of beta in (0, 1), 1,509 of them leave
    /// <c>1 - |t| * inv1mb</c> one ulp above zero, and beta = 0.15 is one of
    /// them: there the comparison is the only thing holding the boundary.
    /// beta = 0.3 and 0.5 are carried because they are the values the shipped
    /// scenes use, and they are honestly the ones that do NOT discriminate.
    /// </summary>
    [Theory]
    [InlineData(0.15f)]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    public void APairAtExactlyRmaxContributesNothing(float beta)
    {
        const float Rmax = 0.25f;

        // 0.25 is a power of two, so rmax * rmax, the separation and its square
        // are all exact and r2 lands on rmax2 bit for bit rather than near it.
        var w = SteppedPair(Rmax, beta, Rmax);

        Assert.Equal(0f, w.Vx[0]);
        Assert.Equal(0f, w.Vy[0]);
        Assert.Equal(0f, w.Vx[1]);
        Assert.Equal(0f, w.Vy[1]);
    }

    /// <summary>
    /// The non-vacuity control for <see cref="APairAtExactlyRmaxContributesNothing"/>:
    /// one ulp closer than <c>rmax</c> is inside the radius and does produce a
    /// force. Without this, a pair that never interacts for some other reason
    /// would satisfy the boundary assertion just as well.
    /// </summary>
    [Theory]
    [InlineData(0.15f)]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    public void APairOneUlpInsideRmaxDoesContribute(float beta)
    {
        const float Rmax = 0.25f;

        var w = SteppedPair(MathF.BitDecrement(Rmax), beta, Rmax);

        Assert.NotEqual(0f, w.Vx[0]);
        Assert.NotEqual(0f, w.Vx[1]);
    }

    /// <summary>
    /// The repulsion knee is half-open from the other side: <c>xn &lt; beta</c>
    /// is the universal-repulsion branch, and <c>xn</c> exactly AT beta belongs
    /// to the matrix-scaled tent. The engine says the same thing - the scalar
    /// path branches on <c>jb</c> after <c>comiss xn, beta</c>
    /// (<c>src/kernel/step.inc:592-593</c>), so equality falls through to the
    /// tent there too.
    ///
    /// The observable is the species matrix rather than a magnitude, which is
    /// what makes this a property rather than a golden: the repulsion branch
    /// computes <c>xn * invBeta - 1</c> and never reads the matrix, while the
    /// tent scales by it. So flipping the sign of the one matrix entry has to
    /// change the result if, and only if, the tent branch was taken.
    ///
    /// beta = 0.15 with rmax = 0.25 puts the pair exactly on the knee:
    /// <c>0.0375 * (1/0.25)</c> is a power-of-two scaling, so <c>xn</c> lands
    /// on beta bit for bit rather than near it. It is also a beta where the two
    /// branch expressions do NOT agree at the knee - the curve is continuous
    /// there in exact arithmetic, and in f32 they differ for 2,898 of 9,998
    /// beta values swept in (0, 1). Where they agree the boundary holds by
    /// arithmetic accident; here it holds because of the comparison.
    /// </summary>
    [Fact]
    public void AtExactlyTheKneeTheMatrixApplies()
    {
        const float Rmax = 0.25f;
        const float Beta = 0.15f;
        const float OnTheKnee = 0.0375f; // beta * rmax, exact

        var attract = SteppedPair(OnTheKnee, Beta, Rmax, matrixEntry: 1.0f);
        var repel = SteppedPair(OnTheKnee, Beta, Rmax, matrixEntry: -1.0f);

        Assert.NotEqual(attract.Vx[0], repel.Vx[0]);
    }

    /// <summary>
    /// The control for <see cref="AtExactlyTheKneeTheMatrixApplies"/>, and a
    /// property in its own right: one ulp INSIDE the knee the universal
    /// repulsion applies, and it is universal - the same force whatever the
    /// species matrix says. Without this the assertion above would be satisfied
    /// by a matrix that happened to matter everywhere.
    /// </summary>
    [Fact]
    public void OneUlpInsideTheKneeTheRepulsionIsUniversal()
    {
        const float Rmax = 0.25f;
        const float Beta = 0.15f;

        var attract = SteppedPair(MathF.BitDecrement(0.0375f), Beta, Rmax, matrixEntry: 1.0f);
        var repel = SteppedPair(MathF.BitDecrement(0.0375f), Beta, Rmax, matrixEntry: -1.0f);

        Assert.Equal(attract.Vx[0], repel.Vx[0]);
        Assert.NotEqual(0f, attract.Vx[0]);
    }

    /// <summary>
    /// FTZ flushes a subnormal to a zero of the RESULT'S SIGN, which is what
    /// the hardware does and what <see cref="TestOracle.World.Ftz"/> documents.
    /// A negative subnormal becomes negative zero, not zero.
    ///
    /// WHY IT IS ASSERTED ON THE ROUTINE RATHER THAN THROUGH A STEP. Nothing
    /// that leaves <c>Step</c> carries the sign: every flushed value reaches a
    /// stored array element through <c>fx + ...</c>, through the velocity sum
    /// or through <c>Wrap</c>, and <c>(-0) + (+0)</c> is <c>+0</c> in every one
    /// of them. That is why the mutation baseline reports this site as reached
    /// by NO test rather than as a survivor, and it is the reason the
    /// assertion is where it is. The sign is not decoration: DAZ and FTZ exist
    /// here to model the seam's pinned MXCSR, and the first divide or
    /// <c>copysign</c> to meet one of these zeros reads it.
    /// </summary>
    [Fact]
    public void FlushingASubnormalKeepsTheSignOfTheResult()
    {
        // Half of the smallest normal float: subnormal on the way in, so FTZ
        // is what decides what comes out.
        const float Subnormal = 6e-39f;

        Assert.Equal(
            BitConverter.SingleToInt32Bits(-0f),
            BitConverter.SingleToInt32Bits(TestOracle.World.Ftz(-Subnormal)));

        Assert.Equal(
            BitConverter.SingleToInt32Bits(0f),
            BitConverter.SingleToInt32Bits(TestOracle.World.Ftz(Subnormal)));
    }

    /// <summary>
    /// The two survivors accepted as equivalent, each anchored to the exact
    /// source text the acceptance was argued about.
    /// </summary>
    private static readonly (string Source, string Mutant, string Why)[] Accepted =
    [
        (
            "float x = (v1 >> 40) * (1.0f / 16777216.0f);",
            "v1 >>> 40",
            "v1 is a ulong, so >> is already a logical shift and >>> is the same operation"
        ),
        (
            "return v < 0f ? -0f : 0f;",
            "v <= 0f",
            "the enclosing branch is guarded by v != 0f, so v == 0f never reaches this comparison"
        ),
    ];

    /// <summary>
    /// The acceptance above is an argument about specific expressions, so it
    /// goes stale the moment one of them is rewritten. This refuses that
    /// silently: an accepted survivor whose source no longer exists has to be
    /// re-triaged against the next run rather than inherited.
    /// </summary>
    [Fact]
    public void AcceptedSurvivorsStillDescribeTheirSource()
    {
        var path = Path.Combine(Build.RepoRoot, "tests", "Swarm.Oracle", "TestOracle.cs");
        Assert.True(File.Exists(path), $"expected {path} to exist");
        var source = File.ReadAllText(path);

        foreach (var (line, mutant, why) in Accepted)
        {
            Assert.True(
                source.Contains(line, StringComparison.Ordinal),
                $"the mutation baseline's survivor `{mutant}` was accepted as equivalent because "
                    + $"{why}, and that argument is about the line `{line}`, which is no longer in "
                    + "TestOracle.cs. Re-triage it against the next mutation run instead of "
                    + "carrying the acceptance over.");
        }
    }
}
