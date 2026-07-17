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
}
