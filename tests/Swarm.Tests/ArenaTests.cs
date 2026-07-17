using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// swarm_init builds the arena: it validates params, size-checks the caller's
/// buffer (fail-closed), resolves the SIMD path against the CPU, writes the
/// 512-byte header, and seeds bank OUT from the deterministic RNG (masterplan
/// decisions 1, 7, 8). These tests pin the fail-closed branches, the header
/// fields, and — the load-bearing one — that the seeded positions/species
/// match an independent splitmix64 oracle draw-for-draw, so the first frame is
/// a pure function of the seed.
/// </summary>
public sealed unsafe class ArenaTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern uint swarm_cpu_paths();

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    private const uint CpuAvx2 = 1, CpuAvx512 = 2;
    private const int IerrParams = -1, IerrArenaSmall = -2, IerrPath = -3;

    // Arena header offsets (abi.inc AH_*).
    private const int AhMagic = 0, AhAbi = 8, AhPath = 12, AhFrame = 16,
        AhRng = 24, AhPadded = 32, AhG = 36, AhParams = 208, AhSize = 512;
    private const ulong ArenaMagic = 0x004D525753;

    private static SwarmParams Valid(uint n = 100, uint species = 3, ulong seed = 0x1234,
        float rmax = 0.05f, uint forcePath = 0)
    {
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = species, Seed = seed,
            RMax = rmax, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f,
            ForcePath = forcePath, Flags = 0,
        };
        for (int i = 0; i < species * species; i++) p.Matrix[i] = 0.1f;
        return p;
    }

    private static uint PaddedN(uint n) => ((n + 15u) & ~15u) + 16u;

    private static T Read<T>(void* arena, long off) where T : unmanaged =>
        Unsafe.ReadUnaligned<T>((byte*)arena + off);

    /// <summary>Runs an action with a freshly 64-aligned arena sized for p.</summary>
    private static void WithArena(in SwarmParams p, Action<nint, ulong> body)
    {
        ulong size = swarm_layout_bytes(in p);
        Assert.True(size > 0, "params must be valid to size the arena");
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            body((nint)arena, size);
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    // --- CPU detection -------------------------------------------------------

    [Fact]
    public void CpuPathsReportsAvx2()
    {
        _ = NativeKernel.Handle;
        uint paths = swarm_cpu_paths();
        // Every x64 machine this project targets (dev + CI) has AVX2+FMA.
        Assert.True((paths & CpuAvx2) != 0, $"expected AVX2 bit; got 0x{paths:X}");
        // AVX-512 may or may not be present; only bits 0/1 are defined.
        Assert.Equal(0u, paths & ~(CpuAvx2 | CpuAvx512));
    }

    // --- fail-closed branches ------------------------------------------------

    [Fact]
    public void InitFailsClosedOnInvalidParams()
    {
        _ = NativeKernel.Handle;
        var good = Valid();
        WithArena(good, (arena, size) =>
        {
            var bad = good;
            bad.RMax = 0.5f; // out of (0, 0.25]
            new Span<byte>((void*)arena, (int)size).Fill(0xCD);
            int rc = swarm_init((void*)arena, size, in bad);
            Assert.Equal(IerrParams, rc);
            Assert.Equal(0xCDCDCDCDu, Read<uint>((void*)arena, 0)); // arena untouched
        });
    }

    [Fact]
    public void InitFailsClosedOnSmallArena()
    {
        _ = NativeKernel.Handle;
        var p = Valid();
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            new Span<byte>(arena, (int)size).Fill(0xCD);
            int rc = swarm_init(arena, size - 1, in p); // one byte short
            Assert.Equal(IerrArenaSmall, rc);
            Assert.Equal(0xCDCDCDCDu, Read<uint>(arena, 0)); // untouched
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    [Fact]
    public void InitFailsClosedOnUnsupportedPath()
    {
        _ = NativeKernel.Handle;
        uint paths = swarm_cpu_paths();
        // Request a path the machine lacks. Prefer AVX-512 when absent; if this
        // machine has it, request nothing testable and skip.
        uint want = (paths & CpuAvx512) == 0 ? 2u : 0u;
        if (want == 0)
        {
            Assert.Skip("this machine supports AVX-512; no unsupported path to request");
            return;
        }
        var p = Valid(forcePath: want);
        WithArena(p, (arena, size) =>
        {
            new Span<byte>((void*)arena, (int)size).Fill(0xCD);
            int rc = swarm_init((void*)arena, size, in p);
            Assert.Equal(IerrPath, rc);
            Assert.Equal(0xCDCDCDCDu, Read<uint>((void*)arena, 0));
        });
    }

    // --- header --------------------------------------------------------------

    [Fact]
    public void InitWritesHeader()
    {
        _ = NativeKernel.Handle;
        var p = Valid(n: 100, species: 3, seed: 0xABCD);
        uint paths = swarm_cpu_paths();
        uint expectedPath = (paths & CpuAvx512) != 0 ? 2u : 1u; // auto: 512 > 2 > avx2
        WithArena(p, (a, size) =>
        {
            void* arena = (void*)a;
            Assert.Equal(0, swarm_init(arena, size, in p));
            Assert.Equal(ArenaMagic, Read<ulong>(arena, AhMagic));
            Assert.Equal(1u, Read<uint>(arena, AhAbi));
            Assert.Equal(expectedPath, Read<uint>(arena, AhPath));
            Assert.Equal(0ul, Read<ulong>(arena, AhFrame));
            // AhRng holds the post-init state (advanced by 3n draws); its exact
            // value is pinned in InitSeedsBankOutMatchingOracle. Here only the
            // seed round-trip through the params copy is checked (below).
            Assert.Equal(PaddedN(100), Read<uint>(arena, AhPadded));
            // g = largest power of two with 1/g >= rmax; 1/16 = 0.0625 >= 0.05
            // but 1/32 = 0.03125 < 0.05, so rmax 0.05 -> g = 16.
            Assert.Equal(16u, Read<uint>(arena, AhG));
            // params copy
            Assert.Equal(100u, Read<uint>(arena, AhParams + 4));  // SP_N
            Assert.Equal(3u, Read<uint>(arena, AhParams + 8));    // SP_SPECIES_N
            Assert.Equal(0xABCDul, Read<ulong>(arena, AhParams + 12)); // SP_SEED
        });
    }

    // --- the seeded state matches the oracle ---------------------------------

    private sealed class SplitMix64(ulong seed)
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

    [Theory]
    [InlineData(1u, 1u, 0x1UL)]
    [InlineData(100u, 3u, 0xABCDUL)]
    [InlineData(4096u, 8u, 0xDEADBEEFUL)]
    [InlineData(16u, 2u, 0UL)] // n = 0 mod 16: exercises the +16 pad tail
    public void InitSeedsBankOutMatchingOracle(uint n, uint species, ulong seed)
    {
        _ = NativeKernel.Handle;
        var p = Valid(n: n, species: species, seed: seed);
        WithArena(p, (a, size) =>
        {
            void* arena = (void*)a;
            Assert.Equal(0, swarm_init(arena, size, in p));

            uint padded = PaddedN(n);
            long baseOff = AhSize;
            long stride = padded * 4L;
            float* xOut = (float*)((byte*)arena + baseOff);
            float* yOut = (float*)((byte*)arena + baseOff + stride);
            float* vxOut = (float*)((byte*)arena + baseOff + 2 * stride);
            float* vyOut = (float*)((byte*)arena + baseOff + 3 * stride);
            uint* spOut = (uint*)((byte*)arena + baseOff + 4 * stride);
            uint* idOut = (uint*)((byte*)arena + baseOff + 5 * stride);

            var rng = new SplitMix64(seed);
            for (uint i = 0; i < n; i++)
            {
                ulong v1 = rng.Next(), v2 = rng.Next(), v3 = rng.Next();
                float ex = (v1 >> 40) * (1.0f / 16777216.0f);
                float ey = (v2 >> 40) * (1.0f / 16777216.0f);
                uint es = (uint)(((v3 >> 32) * species) >> 32);
                Assert.Equal(ex, xOut[i]);
                Assert.Equal(ey, yOut[i]);
                Assert.Equal(es, spOut[i]);
                Assert.Equal(0f, vxOut[i]);
                Assert.Equal(0f, vyOut[i]);
                Assert.Equal(i, idOut[i]);
            }
            // pads: finite zero, id continues i
            for (uint i = n; i < padded; i++)
            {
                Assert.Equal(0f, xOut[i]);
                Assert.Equal(0f, yOut[i]);
                Assert.Equal(0f, vxOut[i]);
                Assert.Equal(0f, vyOut[i]);
                Assert.Equal(0u, spOut[i]);
                Assert.Equal(i, idOut[i]);
            }
            // the header RNG state advanced to exactly 3n draws
            Assert.Equal(rng.State, Read<ulong>(arena, AhRng));
        });
    }
}
