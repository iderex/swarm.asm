namespace Swarm.Tests;

/// <summary>
/// The reference RNG and init distribution the kernel claims to implement
/// (rng.inc + init.inc, masterplan decisions 1/8). Shared by every test that
/// pins seeded state so there is a single source of truth for "what the seed
/// means".
/// </summary>
public static class TestOracle
{
    public sealed class SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }

        public ulong State => _state;
    }

    /// <summary>One particle's initial position and species, consuming three
    /// draws in the pinned order (x, y, species).</summary>
    public static (float X, float Y, uint Species) DrawParticle(SplitMix64 rng, uint speciesN)
    {
        ulong v1 = rng.Next(), v2 = rng.Next(), v3 = rng.Next();
        float x = (v1 >> 40) * (1.0f / 16777216.0f);
        float y = (v2 >> 40) * (1.0f / 16777216.0f);
        uint s = (uint)(((v3 >> 32) * speciesN) >> 32);
        return (x, y, s);
    }

    /// <summary>
    /// The reference world: seeded init plus the pinned force+integrate step
    /// (masterplan force-model section), implemented verbatim in scalar f32 so
    /// it defines what the kernel's `swarm_pass`/`swarm_step` must reproduce
    /// (bit-exact for the scalar path, within epsilon for the SIMD path).
    ///
    /// IT ALSO MODELS THE ENGINE'S FP MODE (#160). The seam pins MXCSR to
    /// 0x9FC0, which sets FTZ and DAZ: a subnormal result is flushed to a zero
    /// of the result's sign, and a subnormal source operand is read as zero.
    /// Neither is IEEE behaviour and .NET does neither, so without this the
    /// two implementations legitimately disagree in exactly the range friction
    /// decay drives every velocity into, and the disagreement hides inside the
    /// parity epsilon instead of being asserted either way.
    ///
    /// <see cref="Ftz"/> is applied to every arithmetic RESULT below and to
    /// every value READ out of the state arrays. DAZ then needs no separate
    /// modelling: every operand reaching an operation is either a freshly
    /// flushed result, a flushed array read, or a parameter, and a subnormal
    /// operand cannot survive to reach one.
    ///
    /// WHERE IT IS DELIBERATELY NOT APPLIED, because the hardware does not:
    /// the rounding instructions behind <see cref="RoundHalfEven"/> and
    /// MathF.Floor return integer-valued floats, which are never subnormal,
    /// and MathF.Abs is a bit operation rather than an arithmetic one. Their
    /// operands are already flushed, so nothing subnormal reaches them either.
    ///
    /// The model costs nothing outside the subnormal range: a value that is
    /// zero, normal, infinite or NaN comes back unchanged, bit for bit. That
    /// is the property that lets the existing normal-range parity stay exact,
    /// and it is checked rather than assumed - see
    /// <see cref="OracleDivergenceSweep"/>, whose scalar arm measures zero
    /// drift at every count and horizon.
    /// </summary>
    public sealed class World
    {
        public readonly int N;
        public readonly int SpeciesN;
        public readonly float[] X, Y, Vx, Vy;
        public readonly uint[] S;
        private readonly float _rmax, _beta, _dt, _friction, _forceScale;
        private readonly float[] _matrix; // 8x8 row-major

        public World(int n, int speciesN, ulong seed, float rmax, float beta,
            float dt, float friction, float forceScale, float[] matrix8x8)
        {
            N = n; SpeciesN = speciesN;
            _rmax = rmax; _beta = beta; _dt = dt; _friction = friction;
            _forceScale = forceScale; _matrix = matrix8x8;
            X = new float[n]; Y = new float[n]; Vx = new float[n]; Vy = new float[n];
            S = new uint[n];
            var rng = new SplitMix64(seed);
            for (int i = 0; i < n; i++)
            {
                var (x, y, s) = DrawParticle(rng, (uint)speciesN);
                X[i] = x; Y[i] = y; S[i] = s; Vx[i] = 0; Vy[i] = 0;
            }
        }

        // round to nearest even integer (SSE roundss mode 0 semantics)
        private static float RoundHalfEven(float v) => MathF.Round(v, MidpointRounding.ToEven);

        /// <summary>Smallest positive normal f32. Strictly below it, and above
        /// zero, is the subnormal range FTZ and DAZ are about.</summary>
        private const float MinNormal = 1.17549435e-38f;

        /// <summary>
        /// One arithmetic result under the engine's pinned FTZ. A subnormal
        /// becomes a zero carrying the result's sign; everything else - zero,
        /// normal, infinite, NaN - is returned unchanged.
        /// </summary>
        internal static float Ftz(float v)
        {
            if (v != 0f && MathF.Abs(v) < MinNormal)
            {
                return v < 0f ? -0f : 0f;
            }
            return v;
        }

        private static float Wrap(float p)
        {
            p = Ftz(p - MathF.Floor(p));
            return p >= 1.0f ? 0.0f : p; // 1.0 reachable in f32; pinned to 0
        }

        /// <summary>One fused force+integrate step over the frozen state,
        /// mutating the arrays in place (forces read the pre-step snapshot).</summary>
        public void Step()
        {
            float rmax2 = Ftz(_rmax * _rmax);
            float vmax = Ftz(_rmax / _dt);
            float invRmax = Ftz(1.0f / _rmax);
            float invBeta = Ftz(1.0f / _beta);
            float inv1mb = Ftz(1.0f / Ftz(1.0f - _beta));

            var nx = new float[N];
            var ny = new float[N];
            var nvx = new float[N];
            var nvy = new float[N];
            for (int i = 0; i < N; i++)
            {
                float xi = Ftz(X[i]), yi = Ftz(Y[i]);
                float fx = 0, fy = 0;
                for (int j = 0; j < N; j++)
                {
                    float dx = Ftz(Ftz(X[j]) - xi); dx = Ftz(dx - RoundHalfEven(dx));
                    float dy = Ftz(Ftz(Y[j]) - yi); dy = Ftz(dy - RoundHalfEven(dy));
                    float r2 = Ftz(Ftz(dx * dx) + Ftz(dy * dy));
                    if (r2 <= 0f || r2 >= rmax2) continue;
                    float r = Ftz(MathF.Sqrt(r2));
                    float xn = Ftz(r * invRmax);
                    float f;
                    if (xn < _beta)
                    {
                        f = Ftz(Ftz(xn * invBeta) - 1.0f);
                    }
                    else
                    {
                        float a = _matrix[S[i] * 8 + S[j]];
                        float t = Ftz(Ftz(Ftz(2.0f * xn) - 1.0f) - _beta);
                        f = Ftz(a * Ftz(1.0f - Ftz(MathF.Abs(t) * inv1mb)));
                    }
                    float q = Ftz(Ftz(_forceScale * f) / r);
                    fx = Ftz(fx + Ftz(q * dx)); fy = Ftz(fy + Ftz(q * dy));
                }
                float vx = Clamp(Ftz(Ftz(Ftz(Vx[i]) * _friction) + Ftz(fx * _dt)), -vmax, vmax);
                float vy = Clamp(Ftz(Ftz(Ftz(Vy[i]) * _friction) + Ftz(fy * _dt)), -vmax, vmax);
                nvx[i] = vx; nvy[i] = vy;
                nx[i] = Wrap(Ftz(xi + Ftz(vx * _dt)));
                ny[i] = Wrap(Ftz(yi + Ftz(vy * _dt)));
            }
            Array.Copy(nx, X, N); Array.Copy(ny, Y, N);
            Array.Copy(nvx, Vx, N); Array.Copy(nvy, Vy, N);
        }

        private static float Clamp(float v, float lo, float hi) =>
            v < lo ? lo : (v > hi ? hi : v);
    }
}
