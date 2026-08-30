// The two foreign engines of the competitor comparison (#153), ported.
//
// #153 asks for three engines beside this one. The managed baseline
// (ManagedBaseline.cs) is the third; these are the other two, and what is
// ported is each engine's CORE - its force law, its damping and its
// integration - rather than its program.
//
// WHY A PORTED CORE AND NOT A MEASUREMENT OF THEIR PROGRAM. A figure taken
// from an executable this repository did not build is not reproducible from
// this repository alone, which is the rule every published row here is held
// to. A ported core is: it is source in this tree, it runs in this harness, in
// the same process as the kernel it is quoted beside, over the same seeded
// population, timed by the same instrument. The two are different claims and
// they are kept in different sentences - nothing here is a measurement of
// anyone's program, and the section in docs/BENCHMARKS.md says so on the row.
//
// WHAT IS PORTED AND WHAT IS NOT. The core is ported. The acceleration
// structure is NOT: both engines are run brute force over every ordered pair,
// which is what the managed baseline and the `## Baseline` kernel rows already
// do. A table in which one side enumerates fewer pairs measures the
// acceleration structure and not the core, and this engine's own acceleration
// structure has its own rows in that document. So all four columns walk N^2
// pairs and the only thing that differs is the arithmetic each engine
// specifies for one pair and for one particle.
//
// EVERY DEVIATION FROM THE PINNED RULE SET IS NAMED WHERE IT IS MADE. That is
// the part #153 says is most likely to go wrong, so a deviation is a comment
// at the line that makes it, and the published row carries the same list.
// Neither core is held to TestOracle: they do not compute what this engine
// computes, and a port bent until it did would be this engine wearing another
// engine's name. What IS asserted about them is in
// tests/Swarm.Tests/CompetitorCoreTests.cs.
//
// Both cores are read out of a checkout of the upstream repository at a named
// commit, quoted by file and line in the comments below, so a later reader can
// re-resolve every claim about what the source says.

using System.Runtime.CompilerServices;

/// <summary>
/// One update of a fixed population under a foreign engine's rules, in plain
/// C#, for the competitor comparison (#153).
/// </summary>
internal static class CompetitorCores
{
    /// <summary>
    /// The core of <c>tom-mohr/particle-life-app</c> at commit
    /// <c>3ba0c4e0055971301e6a9c36073ebbe6b8c3eda4</c>.
    ///
    /// This engine expresses the same force law this repository pins in
    /// docs/MASTERPLAN.md. Its accelerator, at
    /// <c>src/main/java/com/particle_life/app/Main.java:275-279</c>, reads
    /// <c>dist &lt; beta ? (dist / beta - 1) : a * (1 - abs(1 + beta - 2 * dist) / (1 - beta))</c>
    /// with <c>dist</c> the neighbour offset already divided by rmax, which is
    /// this engine's <c>xn</c>; and <c>|1 + beta - 2*xn|</c> is
    /// <c>|2*xn - 1 - beta|</c> term for term. Same knee, same tent, same
    /// matrix entry.
    ///
    /// The deviations are elsewhere and each is marked at its line: the extra
    /// factor of rmax on the acceleration, double precision throughout,
    /// friction renormalised to 60 fps, and no velocity clamp.
    /// </summary>
    internal sealed class ParticleLifeApp
    {
        private readonly ManagedBaseline.Scene _s;
        private readonly double[] _x, _y, _vx, _vy;
        private readonly double[] _x0, _y0;
        private readonly int[] _species;

        internal readonly double[] OutX, OutY, OutVx, OutVy;

        internal ParticleLifeApp(ManagedBaseline.Scene s)
        {
            _s = s;
            _x = new double[s.N];
            _y = new double[s.N];
            _vx = new double[s.N];
            _vy = new double[s.N];
            _x0 = new double[s.N];
            _y0 = new double[s.N];
            _species = new int[s.N];
            OutX = new double[s.N];
            OutY = new double[s.N];
            OutVx = new double[s.N];
            OutVy = new double[s.N];

            // The SAME particles the managed baseline and the kernel start
            // from, drawn by the one routine rather than by a copy of it.
            var x = new float[s.N];
            var y = new float[s.N];
            ManagedBaseline.Draw(s, x, y, _species);
            for (int i = 0; i < s.N; i++)
            {
                // DEVIATION: double throughout. Physics.java carries Vector3d,
                // so every position, velocity and force here is f64 where this
                // engine's is f32. The drawn f32 coordinate widens exactly, so
                // both engines start from the same particles.
                _x0[i] = x[i];
                _y0[i] = y[i];
            }
        }

        /// <summary>
        /// One <c>Physics.update()</c>: velocities for the whole population
        /// against frozen positions, then positions. That is the order at
        /// <c>Physics.java:116-134</c>, two passes over the array rather than
        /// one, and it is why this reads IN and writes OUT the way the kernel
        /// pass does.
        /// </summary>
        internal void Update()
        {
            ManagedBaseline.Scene s = _s;
            int n = s.N;
            double rmax = s.RMax;
            double rmax2 = rmax * rmax;
            double beta = s.Beta;
            double dt = s.Dt;

            // DEVIATION: friction is renormalised to 60 fps.
            // Physics.java:401 - `Math.pow(settings.friction, 60 * settings.dt)`.
            // At the published scene (friction 0.71, dt 0.02) that is
            // 0.71^1.2, not 0.71.
            double frictionFactor = Math.Pow(s.Friction, 60.0 * dt);

            double[] x = _x, y = _y, vx = _vx, vy = _vy;
            float[] matrix = s.Matrix8x8;
            int[] species = _species;

            // The restore that makes one update repeatable, so the instrument
            // is the same min-of-rounds the kernel rows use. It is O(N)
            // against an O(N^2) pass and is timed inside the window rather
            // than hidden outside it; the section measures its size.
            Array.Copy(_x0, x, n);
            Array.Copy(_y0, y, n);
            Array.Clear(vx, 0, n);
            Array.Clear(vy, 0, n);

            // Physics.java:437 - `deltaV.mul(rmax * force * dt)`.
            // DEVIATION: the extra factor of rmax. Accelerator.java documents
            // it - the returned acceleration "is also interpreted as relative
            // to rmax" - so the same law produces an acceleration rmax times
            // this engine's.
            double k = rmax * s.ForceScale * dt;

            for (int i = 0; i < n; i++)
            {
                double xi = x[i], yi = y[i];
                int row = species[i] * 8;

                // Physics.java:402 - friction multiplies the standing velocity
                // BEFORE the neighbours are summed. The sum below carries its
                // own dt, so this is the same shape as `v * friction + f * dt`
                // rather than a different order of operations.
                double vxi = vx[i] * frictionFactor;
                double vyi = vy[i] * frictionFactor;

                for (int j = 0; j < n; j++)
                {
                    if (i == j)
                        continue; // Physics.java:423

                    // Range.wrapConnection, Range.java:75-81: a single wind
                    // wrap onto [-0.5, 0.5). `dx -= round(dx)` is the same
                    // function except exactly at +/-0.5, where round-half-even
                    // sends 0.5 to 0 and this sends it to -0.5.
                    double dx = x[j] - xi;
                    if (dx < -0.5) dx += 1.0;
                    else if (dx >= 0.5) dx -= 1.0;

                    double dy = y[j] - yi;
                    if (dy < -0.5) dy += 1.0;
                    else if (dy >= 0.5) dy -= 1.0;

                    double d2 = dx * dx + dy * dy;

                    // Physics.java:432 - `<=` rmax^2, inclusive, where this
                    // engine's pass excludes the boundary. A pair at exactly
                    // rmax contributes a zero force under both laws, so the
                    // difference is a branch and not a trajectory.
                    if (d2 == 0.0 || d2 > rmax2)
                        continue;

                    // Physics.java:434 - the offset is divided by rmax before
                    // the accelerator sees it, so `dist` below is xn.
                    double ux = dx / rmax, uy = dy / rmax;
                    double dist = Math.Sqrt(ux * ux + uy * uy);

                    // Main.java:275-279, the accelerator itself. It is the
                    // one below rather than a copy of it inlined here: two
                    // statements of a force law are two force laws, and the
                    // one the suite asserts about has to be the one the
                    // published number was taken from.
                    double force = Accelerate(matrix[row + species[j]], dist, beta);

                    double q = force / dist;
                    vxi += ux * q * k;
                    vyi += uy * q * k;
                }

                // DEVIATION: no velocity clamp. This engine clamps to rmax/dt
                // so a particle cannot cross a cell in one step; Physics.java
                // has no such bound anywhere.
                OutVx[i] = vxi;
                OutVy[i] = vyi;
            }

            for (int i = 0; i < n; i++)
            {
                // Physics.java:447 - `velocity.mulAdd(dt, position, position)`,
                // then Range.wrap onto [0, 1) at :449 and Range.java:47-57.
                OutX[i] = Wrap(x[i] + OutVx[i] * dt);
                OutY[i] = Wrap(y[i] + OutVy[i] * dt);
            }
        }

        private static double Wrap(double v)
        {
            if (v < 0)
            {
                do { v += 1.0; } while (v < 0);
                return v;
            }

            while (v >= 1.0) v -= 1.0;
            return v;
        }

        /// <summary>
        /// The accelerator alone, exposed so the suite can hold this port's
        /// force law against the one this repository pins instead of taking
        /// the comment above on trust.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static double Accelerate(double a, double xn, double beta) =>
            xn < beta
                ? xn / beta - 1.0
                : a * (1.0 - Math.Abs(1.0 + beta - 2.0 * xn) / (1.0 - beta));
    }

    /// <summary>
    /// The core of <c>hunar4321/particle-life</c> at commit
    /// <c>256278714c4f6a1ce900d24faafcc101769c54c2</c>,
    /// <c>particle_life/src/ofApp.cpp</c>.
    ///
    /// This engine does NOT express the pinned rule set, and that is the
    /// finding rather than an obstacle to be normalised away. Its force is
    /// <c>1/r</c> inside a radius and zero outside it - no repulsion knee, no
    /// matrix-weighted tent (<c>ofApp.cpp:59</c>). Its world is bounded by a
    /// clamp rather than a wrap (<c>ofApp.cpp:84-90</c>). It carries no dt:
    /// velocity is added straight to position (<c>ofApp.cpp:79-80</c>). And it
    /// integrates each particle once per PARTNER GROUP rather than once per
    /// frame against a frozen state - the update at <c>ofApp.cpp:474-489</c>
    /// calls <c>interaction</c> once per ordered species pair and each call
    /// ends by moving its first group, so a particle has already moved before
    /// the next group is summed against it.
    ///
    /// That last property is why this is stored per species group: the
    /// partition is not a storage preference, it is the thing being ported.
    /// </summary>
    internal sealed class ParticleLifeCpp
    {
        private readonly ManagedBaseline.Scene _s;
        private readonly int _groups;
        private readonly float[][] _px, _py, _pvx, _pvy;
        private readonly float[][] _px0, _py0;

        /// <summary>Group sizes, so a reader can derive the pair count from
        /// the partition rather than assume it is balanced.</summary>
        internal int[] GroupSizes { get; }

        internal ParticleLifeCpp(ManagedBaseline.Scene s)
        {
            _s = s;
            _groups = s.SpeciesN;

            var x = new float[s.N];
            var y = new float[s.N];
            var species = new int[s.N];
            ManagedBaseline.Draw(s, x, y, species);

            GroupSizes = new int[_groups];
            for (int i = 0; i < s.N; i++)
                GroupSizes[species[i]]++;

            _px = new float[_groups][];
            _py = new float[_groups][];
            _pvx = new float[_groups][];
            _pvy = new float[_groups][];
            _px0 = new float[_groups][];
            _py0 = new float[_groups][];
            for (int g = 0; g < _groups; g++)
            {
                _px[g] = new float[GroupSizes[g]];
                _py[g] = new float[GroupSizes[g]];
                _pvx[g] = new float[GroupSizes[g]];
                _pvy[g] = new float[GroupSizes[g]];
                _px0[g] = new float[GroupSizes[g]];
                _py0[g] = new float[GroupSizes[g]];
            }

            var fill = new int[_groups];
            for (int i = 0; i < s.N; i++)
            {
                int g = species[i];
                int k = fill[g]++;
                _px0[g][k] = x[i];
                _py0[g][k] = y[i];
            }
        }

        /// <summary>Positions after the last <see cref="Update"/>, group by
        /// group, for the suite to read.</summary>
        internal float[][] X => _px;

        internal float[][] Y => _py;

        /// <summary>The drawn positions this update restores from, and the
        /// velocity it leaves behind. The suite reads both: under a
        /// per-partner-group integration the displacement is the SUM of the
        /// intermediate velocities, and under a frozen-state one it is the
        /// final velocity exactly - which is how the two are told apart.</summary>
        internal float[][] X0 => _px0;

        internal float[][] Y0 => _py0;

        internal float[][] Vx => _pvx;

        internal float[][] Vy => _pvy;

        /// <summary>
        /// One <c>ofApp::update()</c> worth of interaction calls: every
        /// ordered species pair, in the source's order, each one moving its
        /// first group before the next call runs.
        /// </summary>
        internal void Update()
        {
            // The same in-window restore the other core pays, for the same
            // reason and measured the same way.
            for (int g = 0; g < _groups; g++)
            {
                Array.Copy(_px0[g], _px[g], _px0[g].Length);
                Array.Copy(_py0[g], _py[g], _py0[g].Length);
                Array.Clear(_pvx[g], 0, _pvx[g].Length);
                Array.Clear(_pvy[g], 0, _pvy[g].Length);
            }

            for (int a = 0; a < _groups; a++)
                for (int b = 0; b < _groups; b++)
                    Interaction(a, b, _s.Matrix8x8[a * 8 + b], _s.RMax);
        }

        /// <summary>
        /// <c>ofApp::interaction</c>, <c>ofApp.cpp:32-93</c>, over one ordered
        /// pair of groups.
        /// </summary>
        private void Interaction(int a, int b, float bigG, float radius)
        {
            // ofApp.cpp:39 - the slider value divided by -100. Kept literally,
            // even though this scene supplies the coefficient in [-1, 1] where
            // the application supplies a slider in [-100, 100]: what a
            // comparison of cost is entitled to is the same arithmetic, and
            // rescaling the coefficient to make the trajectory look familiar
            // would be an invention. DEVIATION, named on the row.
            float g = bigG / -100f;

            // ofApp.cpp:75-76. DEVIATION: viscosity in place of friction. The
            // source multiplies by (1 - viscosity) AFTER adding the force;
            // this scene declares a friction coefficient, so viscosity is
            // taken as 1 - friction and the source's shape is kept.
            // worldGravity is 0.0F by default (ofApp.h:219) and is left there.
            float viscosity = 1f - _s.Friction;
            float oneMinusViscosity = 1f - viscosity;

            float[] x1 = _px[a], y1 = _py[a], vx1 = _pvx[a], vy1 = _pvy[a];
            float[] x2 = _px[b], y2 = _py[b];
            int n1 = x1.Length, n2 = x2.Length;
            float r2max = radius * radius;

            for (int i = 0; i < n1; i++)
            {
                float fx = 0f, fy = 0f;

                for (int j = 0; j < n2; j++)
                {
                    // ofApp.cpp:51 - the source compares POSITIONS, not
                    // indices, so a coincident pair in two different groups is
                    // skipped as well. Ported as written.
                    if (x1[i] == x2[j] && y1[i] == y2[j])
                        continue;

                    // DEVIATION: no wrap. The offset is the plain difference,
                    // because this engine's world is bounded by a clamp.
                    float dx = x1[i] - x2[j];
                    float dy = y1[i] - y2[j];
                    float d2 = dx * dx + dy * dy;

                    // ofApp.cpp:59. DEVIATION: the whole force law. 1/r inside
                    // the radius, zero outside, no beta knee and no matrix
                    // weighting of the shape - the matrix entry enters once,
                    // as the constant `g` above.
                    float force = d2 < r2max ? 1.0f / MathF.Sqrt(d2) : 0.0f;
                    fx += dx * force;
                    fy += dy * force;
                }

                // ofApp.cpp:64-71, the wall repel, is NOT ported: its default
                // is 10.0 in a 1600 x 900 pixel world (ofApp.h:215-222) and
                // this scene's world is the unit square, where a 10-unit
                // repulsion band covers all of it. Leaving it out is one
                // per-particle branch, not a per-pair one, so it moves the
                // trajectory and not the cost.
                vx1[i] = (vx1[i] + fx * g) * oneMinusViscosity;
                vy1[i] = (vy1[i] + fy * g) * oneMinusViscosity;

                // ofApp.cpp:79-80. DEVIATION: no dt. The velocity is added to
                // the position directly, and it is added HERE, inside the
                // group loop, which is the per-partner-group integration this
                // class exists to reproduce.
                x1[i] += vx1[i];
                y1[i] += vy1[i];
            }

            // ofApp.cpp:84-90, the bounds clamp, on the unit square rather
            // than on 1600 x 900. DEVIATION: a clamp where this engine wraps.
            for (int i = 0; i < n1; i++)
            {
                x1[i] = MathF.Min(MathF.Max(x1[i], 0f), 1f);
                y1[i] = MathF.Min(MathF.Max(y1[i], 0f), 1f);
            }
        }

        /// <summary>
        /// The force law alone, exposed so the suite can pin the deviation
        /// rather than leave it to prose: 1/r inside the radius, zero at and
        /// outside it, no knee and no matrix.
        /// </summary>
        internal static float PairForce(float d2, float r2max) =>
            d2 < r2max ? 1.0f / MathF.Sqrt(d2) : 0.0f;
    }

    /// <summary>
    /// The restore both ported cores pay inside their timed window, and
    /// nothing else. Timed beside them so the section can state the overhead's
    /// size instead of calling it negligible.
    /// </summary>
    internal sealed class RestoreOnly
    {
        private readonly int _n;
        private readonly float[] _x, _y, _vx, _vy, _x0, _y0;

        internal RestoreOnly(ManagedBaseline.Scene s)
        {
            _n = s.N;
            _x = new float[s.N];
            _y = new float[s.N];
            _vx = new float[s.N];
            _vy = new float[s.N];
            _x0 = new float[s.N];
            _y0 = new float[s.N];
            ManagedBaseline.Draw(s, _x0, _y0, new int[s.N]);
        }

        internal void Update()
        {
            Array.Copy(_x0, _x, _n);
            Array.Copy(_y0, _y, _n);
            Array.Clear(_vx, 0, _n);
            Array.Clear(_vy, 0, _n);
        }
    }
}
