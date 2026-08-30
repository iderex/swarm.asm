using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The ported competitor cores (#153) are published as competitor numbers, so
/// what a reader is entitled to challenge is not their speed but whether each
/// one computes what its upstream engine specifies - and, where it does not,
/// that the difference is one of the deviations named beside the row rather
/// than an unnamed fifth one.
///
/// Neither core is held to <see cref="TestOracle"/> the way the managed
/// baseline is, because neither is meant to agree with it. What is asserted
/// instead is one property per engine, chosen to be the one that would go
/// wrong:
///
/// <list type="bullet">
/// <item>The Java core DOES express this repository's force law. The
/// deviations are the four listed on the row, and no others - which is checked
/// by neutralising all four in the scene and requiring the port to land on the
/// oracle.</item>
/// <item>The C++ core does NOT express it, and its per-partner-group
/// integration is the property being ported. Both are pinned, because the
/// pressure on a port like this is always toward quietly making it agree.</item>
/// </list>
///
/// Neither core needs the native library, so nothing here skips when Smart App
/// Control blocks a load.
/// </summary>
public class CompetitorCoreTests
{
    private static float[] Matrix()
    {
        var m = new float[64];
        for (uint a = 0; a < 8; a++)
            for (uint b = 0; b < 8; b++)
                m[a * 8 + b] = MathF.Sin(a * 3.1f + b * 1.7f);
        return m;
    }

    // ---- the Java core: the same law, and exactly the named deviations -----

    /// <summary>
    /// The four deviations the row names for tom-mohr/particle-life-app are
    /// all scene-removable, which is what makes this test possible:
    ///
    /// <list type="number">
    /// <item>the extra factor of rmax on the acceleration - cancelled by
    /// handing the port <c>forceScale / rmax</c>;</item>
    /// <item>friction renormalised to 60 fps, <c>pow(friction, 60 * dt)</c> -
    /// the identity at <c>dt = 1/60</c>;</item>
    /// <item>no velocity clamp - inert at a scene whose one step never
    /// reaches <c>rmax / dt</c>, which the oracle side proves by agreeing;</item>
    /// <item>double precision - not removable, and it is the reason this
    /// compares within a tolerance instead of at zero.</item>
    /// </list>
    ///
    /// So a port carrying a FIFTH difference - a dropped branch, a missing
    /// wrap, a mis-transcribed knee - cannot pass, and that is the property.
    /// </summary>
    [Theory]
    [InlineData("bench-like", 512, 6, 0x5EEDul, 0.05f, 0.3f, 0.71f, 10f)]
    [InlineData("wide-knee", 384, 3, 0xC0FFEEul, 0.25f, 0.7f, 0.9f, 4f)]
    [InlineData("narrow-tail", 384, 8, 0x1234ul, 0.03f, 0.1f, 0.5f, 25f)]
    public void JavaCoreIsThisEnginesLawOnceTheFourNamedDeviationsAreRemoved(
        string name, int n, int speciesN, ulong seed,
        float rmax, float beta, float friction, float forceScale)
    {
        // Deviation 2 is removed by the timestep and nothing else: at
        // dt = 1/60, pow(friction, 60 * dt) is friction.
        const float Dt = 1.0f / 60.0f;
        float[] matrix = Matrix();

        var oracle = new TestOracle.World(
            n, speciesN, seed, rmax, beta, Dt, friction, forceScale, matrix);

        // Deviation 1 is removed here: the port multiplies by rmax, so it is
        // handed a force scale divided by it.
        var port = new CompetitorCores.ParticleLifeApp(new ManagedBaseline.Scene(
            n, speciesN, seed, rmax, beta, Dt, friction, forceScale / rmax, matrix));

        oracle.Step();
        port.Update();

        // Deviation 4 is what is left: f64 against f32 over the same sum in
        // the same order. The bound is on the velocity, because that is where
        // the accumulation happens; the position follows it through one
        // multiply-add.
        double worstV = 0, worstP = 0;
        for (int i = 0; i < n; i++)
        {
            worstV = Math.Max(worstV, Math.Abs(port.OutVx[i] - oracle.Vx[i]));
            worstV = Math.Max(worstV, Math.Abs(port.OutVy[i] - oracle.Vy[i]));
            worstP = Math.Max(worstP, Math.Abs(port.OutX[i] - oracle.X[i]));
            worstP = Math.Max(worstP, Math.Abs(port.OutY[i] - oracle.Y[i]));
        }

        // A f32 velocity of order rmax/dt carries an ulp of about 1e-7 of its
        // own size, and the sum runs over the in-range neighbours of one
        // particle. 1e-4 of the clamp bound is far above that and far below
        // any dropped term: the smallest single in-range contribution at these
        // scenes is orders of magnitude larger.
        double tolV = 1e-4 * (rmax / Dt);
        Assert.True(worstV < tolV,
            $"{name}: worst velocity divergence {worstV:G6} exceeds {tolV:G6}");
        Assert.True(worstP < 1e-5,
            $"{name}: worst position divergence {worstP:G6} exceeds 1e-5");
    }

    /// <summary>
    /// The anti-vacuity leg of the test above, and the reason its tolerance is
    /// a bound rather than a shrug: with deviation 1 left IN - the port handed
    /// the plain force scale, so its acceleration is rmax times the oracle's -
    /// the same comparison fails. A tolerance loose enough to swallow a real
    /// difference would pass here too.
    /// </summary>
    [Fact]
    public void TheJavaComparisonFailsWhenOneNamedDeviationIsLeftIn()
    {
        const int N = 512, SpeciesN = 6;
        const ulong Seed = 0x5EED;
        const float RMax = 0.05f, Beta = 0.3f, Dt = 1.0f / 60.0f;
        const float Friction = 0.71f, ForceScale = 10f;
        float[] matrix = Matrix();

        var oracle = new TestOracle.World(
            N, SpeciesN, Seed, RMax, Beta, Dt, Friction, ForceScale, matrix);
        var port = new CompetitorCores.ParticleLifeApp(new ManagedBaseline.Scene(
            N, SpeciesN, Seed, RMax, Beta, Dt, Friction, ForceScale, matrix));

        oracle.Step();
        port.Update();

        double worstV = 0;
        for (int i = 0; i < N; i++)
        {
            worstV = Math.Max(worstV, Math.Abs(port.OutVx[i] - oracle.Vx[i]));
            worstV = Math.Max(worstV, Math.Abs(port.OutVy[i] - oracle.Vy[i]));
        }

        Assert.True(worstV > 1e-4 * (RMax / Dt),
            $"the rmax deviation left in produced a divergence of only {worstV:G6}");
    }

    /// <summary>
    /// The accelerator of Main.java:275-279 written with the tent term as
    /// <c>|1 + beta - 2*xn|</c>, against the same law written as this
    /// repository writes it, <c>|2*xn - 1 - beta|</c>. The two are the same
    /// function and the test says so at points on both sides of the knee, so
    /// the sentence "same knee, same tent" in the published section is
    /// checked rather than asserted.
    /// </summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(0.1)]
    [InlineData(0.7)]
    public void TheJavaAcceleratorIsThePinnedForceCurve(double beta)
    {
        double[] entries = [-1.0, -0.37, 0.0, 0.42, 1.0];

        for (int step = 0; step <= 200; step++)
        {
            double xn = step / 200.0;
            foreach (double a in entries)
            {
                double ported = CompetitorCores.ParticleLifeApp.Accelerate(a, xn, beta);
                double pinned = xn < beta
                    ? xn / beta - 1.0
                    : a * (1.0 - Math.Abs(2.0 * xn - 1.0 - beta) / (1.0 - beta));

                Assert.True(Math.Abs(ported - pinned) < 1e-12,
                    $"beta={beta} xn={xn} a={a}: ported {ported:G17} against pinned {pinned:G17}");
            }
        }
    }

    // ---- the C++ core: the deviations, pinned so they cannot be smoothed ---

    /// <summary>
    /// hunar4321/particle-life computes <c>1/r</c> inside the radius and zero
    /// at and outside it (ofApp.cpp:59). No knee, no matrix weighting of the
    /// shape, no sign change with distance. This is the deviation the
    /// published row leads with, and pinning it is what stops a later edit
    /// from quietly bending the port toward this repository's own law.
    /// </summary>
    [Fact]
    public void TheCppPairForceIsInverseDistanceAndNothingElse()
    {
        const float R = 0.05f, R2 = R * R;

        for (int step = 1; step <= 200; step++)
        {
            float r = R * step / 100f; // sweeps through and well past the radius
            float d2 = r * r;
            float got = CompetitorCores.ParticleLifeCpp.PairForce(d2, R2);

            if (d2 < R2)
            {
                Assert.Equal(1.0f / MathF.Sqrt(d2), got);
                // Monotonically decreasing and never negative: the two things
                // a knee or a matrix term would break.
                Assert.True(got > 0f);
            }
            else
            {
                Assert.Equal(0f, got);
            }
        }

        // The boundary is exclusive: at exactly the radius the source's
        // `distance_squared < radius*radius` is false.
        Assert.Equal(0f, CompetitorCores.ParticleLifeCpp.PairForce(R2, R2));
    }

    /// <summary>
    /// The property #153's tree text names as the sharpest deviation: this
    /// engine integrates each particle once per PARTNER GROUP, not once per
    /// frame against a frozen state.
    ///
    /// It is told apart from a frozen-state update by arithmetic rather than
    /// by reading the code, so it survives a rewrite. Under the source's
    /// order a particle's displacement over one update is the SUM of its
    /// intermediate velocities, one per partner group; under a frozen-state
    /// update it is the final velocity and nothing else. The test asserts the
    /// first and would fail on the second.
    ///
    /// THE CLAMP IS THE ONE THING THAT COULD MAKE THIS PASS FOR THE WRONG
    /// REASON, so the scene is scaled until nothing reaches the world edge and
    /// the run asserts that before it asserts anything else. A first attempt
    /// at this property - comparing the source's group order against the
    /// reverse - is NOT here, because it passes on a frozen-state port too:
    /// the damping multiplies the standing velocity once per group, so the
    /// order matters through the velocity even when it does not matter through
    /// the position, and the test could not tell the two apart.
    /// </summary>
    [Fact]
    public void TheCppCoreIntegratesPerPartnerGroup()
    {
        // A matrix scaled down so no particle travels far enough in one update
        // to meet the clamp at 0 or 1. The scale is a property of this fixture
        // and nothing else reads it.
        var m = new float[64];
        for (uint a = 0; a < 8; a++)
            for (uint b = 0; b < 8; b++)
                m[a * 8 + b] = MathF.Sin(a * 3.1f + b * 1.7f) * 1e-4f;

        var scene = new ManagedBaseline.Scene(
            2048, 6, 0x5EED, 0.05f, 0.3f, 0.02f, 0.71f, 10f, m);

        var cpp = new CompetitorCores.ParticleLifeCpp(scene);
        cpp.Update();

        for (int g = 0; g < scene.SpeciesN; g++)
            for (int i = 0; i < cpp.X[g].Length; i++)
            {
                Assert.InRange(cpp.X[g][i], 1e-6f, 1f - 1e-6f);
                Assert.InRange(cpp.Y[g][i], 1e-6f, 1f - 1e-6f);
            }

        // Displacement against the standing velocity. Equal everywhere means
        // the update integrated once, which is not this engine.
        double worst = 0;
        for (int g = 0; g < scene.SpeciesN; g++)
            for (int i = 0; i < cpp.X[g].Length; i++)
            {
                double dispX = cpp.X[g][i] - cpp.X0[g][i];
                double dispY = cpp.Y[g][i] - cpp.Y0[g][i];
                worst = Math.Max(worst, Math.Abs(dispX - cpp.Vx[g][i]));
                worst = Math.Max(worst, Math.Abs(dispY - cpp.Vy[g][i]));
            }

        // The threshold sits between two measured numbers rather than beside
        // one. Ported as the source has it, the worst gap is 1.03E-05 at this
        // fixture; with the integration moved out of the group loop into a
        // single pass - the "corrected" port this exists to refuse - it falls
        // to 2.98E-08, which is the rounding of one f32 add at a coordinate of
        // order 0.5 and is the floor a comparison like this can have.
        Assert.True(worst > 1e-6,
            "one update's displacement is its final velocity, so this port integrates " +
            $"once against a frozen state rather than once per partner group (worst {worst:G6})");
    }

    /// <summary>
    /// The C++ core is not this repository's engine, and the published row
    /// says so. The claim is checked at the one point where the two laws
    /// disagree in SIGN: inside the knee this repository repels regardless of
    /// the matrix entry, while ofApp.cpp:59 always attracts along the offset
    /// with a coefficient that carries the entry's sign.
    /// </summary>
    [Fact]
    public void TheCppCoreDoesNotExpressThePinnedRuleSet()
    {
        const float RMax = 0.05f, Beta = 0.3f, R2Max = RMax * RMax;

        // A separation well inside the knee, where the pinned law is
        // repulsive: xn = 0.1 against beta = 0.3.
        float r = 0.1f * RMax;
        float pinned = 0.1f / Beta - 1.0f;
        Assert.True(pinned < 0f, "the fixture is not inside the repulsion knee");

        // The C++ force at the same separation carries no knee at all.
        float cpp = CompetitorCores.ParticleLifeCpp.PairForce(r * r, R2Max);
        Assert.True(cpp > 0f);
        Assert.True(Math.Abs(cpp - pinned) > 1f,
            "the two laws came out close, which the published deviation says they are not");
    }

    // ---- both cores: one population, and a repeatable update ---------------

    /// <summary>
    /// Both ported cores start from the particles <see cref="ManagedBaseline"/>
    /// draws, which is what makes "the same seeded population" a fact rather
    /// than a caption. It is asserted through the cores' own finished state
    /// under a null scene - force scale zero - so it holds for whatever the
    /// constructors do rather than for a routine they are trusted to call.
    /// </summary>
    [Fact]
    public void BothCoresStartFromTheBaselinesPopulation()
    {
        const int N = 1024, SpeciesN = 6;
        var scene = new ManagedBaseline.Scene(
            N, SpeciesN, 0x5EED, 0.05f, 0.3f, 0.02f, 1.0f, 0.0f, new float[64]);

        var x = new float[N];
        var y = new float[N];
        var species = new int[N];
        ManagedBaseline.Draw(scene, x, y, species);

        // Force scale zero and friction one: one update moves nothing, so the
        // finished state is the drawn state.
        var app = new CompetitorCores.ParticleLifeApp(scene);
        app.Update();
        for (int i = 0; i < N; i++)
        {
            Assert.Equal(x[i], app.OutX[i], 6);
            Assert.Equal(y[i], app.OutY[i], 6);
        }

        // The C++ core has no force scale in its arithmetic, so the null scene
        // for it is the all-zero matrix above; its coefficient g is then zero
        // and the population is only partitioned, not moved.
        var cpp = new CompetitorCores.ParticleLifeCpp(scene);
        cpp.Update();
        var seen = new List<(float, float)>();
        for (int g = 0; g < SpeciesN; g++)
            for (int i = 0; i < cpp.X[g].Length; i++)
                seen.Add((cpp.X[g][i], cpp.Y[g][i]));

        var drawn = new List<(float, float)>();
        for (int i = 0; i < N; i++)
            drawn.Add((x[i], y[i]));

        Assert.Equal(N, seen.Count);
        Assert.Equal(drawn.OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToArray(),
            seen.OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToArray());
    }

    /// <summary>
    /// The published figure is a minimum over repeated calls, so a call has to
    /// do the same work every time. Both cores advance the state in place and
    /// restore it at the top of the update for exactly that reason; a restore
    /// that missed a buffer would make round two a different measurement from
    /// round one, and the number would be a minimum over a drifting subject.
    /// </summary>
    [Fact]
    public void RepeatingAnUpdateReproducesIt()
    {
        var scene = new ManagedBaseline.Scene(
            1024, 6, 0x5EED, 0.05f, 0.3f, 0.02f, 0.71f, 10f, Matrix());

        var app = new CompetitorCores.ParticleLifeApp(scene);
        app.Update();
        double[] appX = (double[])app.OutX.Clone();
        double[] appV = (double[])app.OutVx.Clone();
        for (int k = 0; k < 3; k++)
        {
            app.Update();
            Assert.Equal(appX, app.OutX);
            Assert.Equal(appV, app.OutVx);
        }

        var cpp = new CompetitorCores.ParticleLifeCpp(scene);
        cpp.Update();
        float[][] first = cpp.X.Select(a => (float[])a.Clone()).ToArray();
        for (int k = 0; k < 3; k++)
        {
            cpp.Update();
            for (int g = 0; g < first.Length; g++)
                Assert.Equal(first[g], cpp.X[g]);
        }
    }
}
