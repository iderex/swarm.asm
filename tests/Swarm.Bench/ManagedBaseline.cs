// The managed baseline for the competitor comparison (#153).
//
// The comparison set named in #5 is two foreign engines plus "a naive
// idiomatic C# port as the managed baseline". This is that port. It answers
// one question and no other: what does the same scene cost when it is written
// the way a competent C# developer would write it, on the same host, in the
// same run, timed by the same instrument as the kernel it is compared against.
//
// WHAT MAKES IT A FAIR BASELINE IS THAT IT IS NOT DELIBERATELY SLOW. The
// fairness argument is the deliverable here, so the shape is chosen with a
// measurement rather than an opinion: two idiomatic layouts are implemented
// and both are timed, and the FASTER of the two is what the comparison quotes.
// A baseline picked that way is a floor on plain managed performance, not a
// strawman.
//
// The line it does not cross is explicit vectorisation. System.Numerics.Vector
// and System.Runtime.Intrinsics are both available in this runtime, and a port
// using them is a different artefact answering a different question - it would
// compare hand-written AVX2 against JIT-emitted AVX2 rather than against
// managed code. Whatever the JIT chooses on its own is kept, because a
// developer writing plain C# gets that for free.
//
// It computes the same force+integrate pass as the kernel over the same seeded
// population, which is asserted rather than claimed: ManagedBaselineParityTests
// compares it against TestOracle, the reference the kernel itself is checked
// against. A baseline that got faster by computing less would red that test.

using System.Runtime.CompilerServices;

/// <summary>
/// One force+integrate pass over a fixed population, in plain C#. Two storage
/// layouts, one force law, shared by the benchmark and by the parity test.
/// </summary>
internal static class ManagedBaseline
{
    /// <summary>The scene: the same parameters and the same seed the kernel is
    /// initialised with, so both sides step the identical population.</summary>
    internal sealed record Scene(
        int N,
        int SpeciesN,
        ulong Seed,
        float RMax,
        float Beta,
        float Dt,
        float Friction,
        float ForceScale,
        float[] Matrix8x8);

    // SplitMix64 and the init distribution are what the seed MEANS (rng.inc,
    // init.inc) rather than an implementation choice, so they are reproduced
    // exactly. Getting them wrong would give the baseline a different
    // population and make every comparison quietly meaningless.
    private static void Draw(Scene s, float[] x, float[] y, int[] species)
    {
        ulong state = s.Seed;

        ulong Next()
        {
            state += 0x9E3779B97F4A7C15;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }

        for (int i = 0; i < s.N; i++)
        {
            ulong v1 = Next(), v2 = Next(), v3 = Next();
            x[i] = (v1 >> 40) * (1.0f / 16777216.0f);
            y[i] = (v2 >> 40) * (1.0f / 16777216.0f);
            species[i] = (int)(((v3 >> 32) * (ulong)s.SpeciesN) >> 32);
        }
    }

    private static float Wrap(float p)
    {
        p -= MathF.Floor(p);
        return p >= 1.0f ? 0.0f : p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp(float v, float lo, float hi) =>
        v < lo ? lo : (v > hi ? hi : v);

    /// <summary>
    /// Structure of arrays, one float[] per field. This is the layout the
    /// comparison quotes, because it measures faster than the alternative
    /// below rather than because it was expected to.
    /// </summary>
    internal sealed class Soa
    {
        private readonly Scene _s;
        private readonly float[] _x, _y, _vx, _vy;
        private readonly int[] _species;

        internal readonly float[] OutX, OutY, OutVx, OutVy;

        /// <summary>The drawn population, before any force. Read by the parity
        /// test, which checks the two engines start from the same particles
        /// before it checks they agree about where the particles go.</summary>
        internal float[] InX => _x;

        internal float[] InY => _y;

        internal int[] InSpecies => _species;

        internal Soa(Scene s)
        {
            _s = s;
            _x = new float[s.N];
            _y = new float[s.N];
            _vx = new float[s.N];
            _vy = new float[s.N];
            _species = new int[s.N];
            OutX = new float[s.N];
            OutY = new float[s.N];
            OutVx = new float[s.N];
            OutVy = new float[s.N];
            Draw(s, _x, _y, _species);
        }

        /// <summary>Force and integrate the whole population, reading the IN
        /// arrays and writing the OUT arrays. Repeating it is idempotent,
        /// which is what lets the kernel rows and this one use one instrument:
        /// both time a pass over frozen input rather than a step that swaps
        /// banks.</summary>
        internal void Pass()
        {
            Scene s = _s;
            float rmax2 = s.RMax * s.RMax;
            float vmax = s.RMax / s.Dt;
            float invRmax = 1.0f / s.RMax;
            float invBeta = 1.0f / s.Beta;
            float inv1mb = 1.0f / (1.0f - s.Beta);
            float[] x = _x, y = _y, matrix = s.Matrix8x8;
            int[] species = _species;

            for (int i = 0; i < s.N; i++)
            {
                float xi = x[i], yi = y[i];
                int row = species[i] * 8;
                float fx = 0.0f, fy = 0.0f;

                for (int j = 0; j < s.N; j++)
                {
                    float dx = x[j] - xi;
                    dx -= MathF.Round(dx);
                    float dy = y[j] - yi;
                    dy -= MathF.Round(dy);

                    float r2 = dx * dx + dy * dy;
                    if (r2 <= 0.0f || r2 >= rmax2)
                    {
                        continue;
                    }

                    float r = MathF.Sqrt(r2);
                    float xn = r * invRmax;
                    float f;
                    if (xn < s.Beta)
                    {
                        f = xn * invBeta - 1.0f;
                    }
                    else
                    {
                        float t = 2.0f * xn - 1.0f - s.Beta;
                        f = matrix[row + species[j]] * (1.0f - MathF.Abs(t) * inv1mb);
                    }

                    float q = s.ForceScale * f / r;
                    fx += q * dx;
                    fy += q * dy;
                }

                float vx = Clamp(_vx[i] * s.Friction + fx * s.Dt, -vmax, vmax);
                float vy = Clamp(_vy[i] * s.Friction + fy * s.Dt, -vmax, vmax);
                OutVx[i] = vx;
                OutVy[i] = vy;
                OutX[i] = Wrap(xi + vx * s.Dt);
                OutY[i] = Wrap(yi + vy * s.Dt);
            }
        }
    }

    /// <summary>
    /// Array of structs, one value type per particle. The other shape a C#
    /// developer reaches for first, kept so the layout choice above rests on a
    /// measurement rather than on a preference.
    /// </summary>
    internal sealed class Aos
    {
        private struct Particle
        {
            internal float X, Y, Vx, Vy;
            internal int Species;
        }

        private readonly Scene _s;
        private readonly Particle[] _in;
        private readonly Particle[] _out;

        internal Aos(Scene s)
        {
            _s = s;
            _in = new Particle[s.N];
            _out = new Particle[s.N];

            var x = new float[s.N];
            var y = new float[s.N];
            var species = new int[s.N];
            Draw(s, x, y, species);
            for (int i = 0; i < s.N; i++)
            {
                _in[i] = new Particle { X = x[i], Y = y[i], Species = species[i] };
            }
        }

        internal void Pass()
        {
            Scene s = _s;
            float rmax2 = s.RMax * s.RMax;
            float vmax = s.RMax / s.Dt;
            float invRmax = 1.0f / s.RMax;
            float invBeta = 1.0f / s.Beta;
            float inv1mb = 1.0f / (1.0f - s.Beta);
            Particle[] p = _in;
            float[] matrix = s.Matrix8x8;

            for (int i = 0; i < s.N; i++)
            {
                float xi = p[i].X, yi = p[i].Y;
                int row = p[i].Species * 8;
                float fx = 0.0f, fy = 0.0f;

                for (int j = 0; j < s.N; j++)
                {
                    float dx = p[j].X - xi;
                    dx -= MathF.Round(dx);
                    float dy = p[j].Y - yi;
                    dy -= MathF.Round(dy);

                    float r2 = dx * dx + dy * dy;
                    if (r2 <= 0.0f || r2 >= rmax2)
                    {
                        continue;
                    }

                    float r = MathF.Sqrt(r2);
                    float xn = r * invRmax;
                    float f;
                    if (xn < s.Beta)
                    {
                        f = xn * invBeta - 1.0f;
                    }
                    else
                    {
                        float t = 2.0f * xn - 1.0f - s.Beta;
                        f = matrix[row + p[j].Species] * (1.0f - MathF.Abs(t) * inv1mb);
                    }

                    float q = s.ForceScale * f / r;
                    fx += q * dx;
                    fy += q * dy;
                }

                float vx = Clamp(p[i].Vx * s.Friction + fx * s.Dt, -vmax, vmax);
                float vy = Clamp(p[i].Vy * s.Friction + fy * s.Dt, -vmax, vmax);
                _out[i].Vx = vx;
                _out[i].Vy = vy;
                _out[i].X = Wrap(xi + vx * s.Dt);
                _out[i].Y = Wrap(yi + vy * s.Dt);
                _out[i].Species = p[i].Species;
            }
        }

        /// <summary>The pass result as four arrays, so the parity test can
        /// hold both layouts to the same comparison without either of them
        /// exposing its storage.</summary>
        internal void CopyOut(float[] x, float[] y, float[] vx, float[] vy)
        {
            for (int i = 0; i < _s.N; i++)
            {
                x[i] = _out[i].X;
                y[i] = _out[i].Y;
                vx[i] = _out[i].Vx;
                vy[i] = _out[i].Vy;
            }
        }
    }
}
