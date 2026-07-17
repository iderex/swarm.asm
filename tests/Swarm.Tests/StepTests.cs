using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The force+integrate kernel: swarm_build / swarm_pass / swarm_step. These
/// tests pin the scalar path against the C# oracle (the masterplan force
/// model) within the epsilon tier, the build bank copy, the frame counter,
/// determinism, and — the threading-determinism seam — pass-split invariance
/// (pass(0,n) == pass(0,k);pass(k,n)).
/// </summary>
public sealed unsafe class StepTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_build(void* arena);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_pass(void* arena, uint first, uint last);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    private const float RMax = 0.2f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f;
    private const int AhPadded = 32, AhFrame = 16, AhSize = 512;

    private static (SwarmParams, float[]) Make(uint n, uint species, ulong seed)
    {
        var matrix = new float[64];
        // varied, deterministic entries in [-1, 1] so the matrix path matters
        for (uint a = 0; a < species; a++)
            for (uint b = 0; b < species; b++)
                matrix[a * 8 + b] = MathF.Sin(a * 3.1f + b * 1.7f); // in [-1,1]
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = species, Seed = seed,
            RMax = RMax, Beta = Beta, Dt = Dt, Friction = Friction, ForceScale = ForceScale,
            ForcePath = 0, Flags = 0,
        };
        for (int i = 0; i < 64; i++) p.Matrix[i] = matrix[i];
        return (p, matrix);
    }

    private static void WithArena(in SwarmParams p, Action<nint> body)
    {
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try { body((nint)arena); }
        finally { NativeMemory.AlignedFree(arena); }
    }

    // torus min-image distance
    private static float TorusDist(float a, float b)
    {
        float d = MathF.Abs(a - b);
        return MathF.Min(d, 1f - d);
    }

    [Theory]
    [InlineData(64u, 3u, 0x1234UL, 1)]
    [InlineData(64u, 3u, 0x1234UL, 4)]
    [InlineData(200u, 5u, 0xBEEFUL, 8)]
    [InlineData(37u, 1u, 0x9UL, 8)] // single species: only universal repulsion
    public void StepMatchesOracleWithinEpsilon(uint n, uint species, ulong seed, int steps)
    {
        _ = NativeKernel.Handle;
        var (p, matrix) = Make(n, species, seed);

        var oracle = new TestOracle.World((int)n, (int)species, seed,
            RMax, Beta, Dt, Friction, ForceScale, matrix);
        for (int k = 0; k < steps; k++) oracle.Step();

        WithArena(p, arena =>
        {
            void* a = (void*)arena;
            Assert.Equal(0, swarm_init(a, swarm_layout_bytes(in p), in p));
            swarm_step(a, (uint)steps);

            var x = new float[n]; var y = new float[n];
            var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];
            swarm_read_state(a, x, y, vx, vy, sp);

            for (uint i = 0; i < n; i++)
            {
                Assert.True(TorusDist(x[i], oracle.X[i]) < 1e-5f,
                    $"x[{i}] asm {x[i]} vs oracle {oracle.X[i]} (step {steps})");
                Assert.True(TorusDist(y[i], oracle.Y[i]) < 1e-5f,
                    $"y[{i}] asm {y[i]} vs oracle {oracle.Y[i]}");
                Assert.True(MathF.Abs(vx[i] - oracle.Vx[i]) < 1e-4f,
                    $"vx[{i}] asm {vx[i]} vs oracle {oracle.Vx[i]}");
                Assert.True(MathF.Abs(vy[i] - oracle.Vy[i]) < 1e-4f, $"vy[{i}]");
                Assert.Equal(oracle.S[i], sp[i]);
            }
        });
    }

    [Fact]
    public void BuildCopiesOutBankToInBank()
    {
        _ = NativeKernel.Handle;
        var (p, _) = Make(100, 3, 0x77);
        WithArena(p, arena =>
        {
            void* a = (void*)arena;
            Assert.Equal(0, swarm_init(a, swarm_layout_bytes(in p), in p));
            swarm_build(a);

            uint padded = *(uint*)((byte*)a + AhPadded);
            long stride = padded * 4L;
            // every OUT dword must now equal its IN counterpart (6 arrays)
            for (int k = 0; k < 6; k++)
            {
                var outArr = (uint*)((byte*)a + AhSize + k * stride);
                var inArr = (uint*)((byte*)a + AhSize + (k + 6) * stride);
                for (uint i = 0; i < padded; i++)
                    Assert.Equal(outArr[i], inArr[i]);
            }
        });
    }

    [Fact]
    public void StepAdvancesFrameCounter()
    {
        _ = NativeKernel.Handle;
        var (p, _) = Make(32, 2, 0x5);
        WithArena(p, arena =>
        {
            void* a = (void*)arena;
            Assert.Equal(0, swarm_init(a, swarm_layout_bytes(in p), in p));
            Assert.Equal(0ul, *(ulong*)((byte*)a + AhFrame));
            swarm_step(a, 7);
            Assert.Equal(7ul, *(ulong*)((byte*)a + AhFrame));
        });
    }

    [Fact]
    public void Deterministic()
    {
        _ = NativeKernel.Handle;
        var (p, _) = Make(128, 4, 0xC0FFEE);
        var (a1x, a1y) = RunAndRead(p, 8);
        var (a2x, a2y) = RunAndRead(p, 8);
        Assert.Equal(a1x, a2x);
        Assert.Equal(a1y, a2y);
    }

    private static (float[], float[]) RunAndRead(SwarmParams p, uint steps)
    {
        float[] rx = [], ry = [];
        WithArena(p, arena =>
        {
            void* a = (void*)arena;
            swarm_init(a, swarm_layout_bytes(in p), in p);
            swarm_step(a, steps);
            uint n = p.N;
            var x = new float[n]; var y = new float[n];
            var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];
            swarm_read_state(a, x, y, vx, vy, sp);
            rx = x; ry = y;
        });
        return (rx, ry);
    }

    [Theory]
    [InlineData(200u, 5u, 0xABCUL, 64u)]
    [InlineData(200u, 5u, 0xABCUL, 1u)]
    [InlineData(200u, 5u, 0xABCUL, 199u)]
    public void PassSplitInvariance(uint n, uint species, ulong seed, uint k)
    {
        // The fused pass is a pure map (reads frozen IN, writes disjoint OUT[i]),
        // so pass(0,n) must equal pass(0,k) then pass(k,n) bit-for-bit — the
        // property that makes the M3 threading deterministic, testable from M1.
        _ = NativeKernel.Handle;
        var (p, _) = Make(n, species, seed);

        var whole = ReadAfter(p, a =>
        {
            swarm_build((void*)a);
            swarm_pass((void*)a, 0, n);
        });
        var split = ReadAfter(p, a =>
        {
            swarm_build((void*)a);
            swarm_pass((void*)a, 0, k);
            swarm_pass((void*)a, k, n);
        });
        Assert.Equal(whole, split);
    }

    private static float[] ReadAfter(SwarmParams p, Action<nint> act)
    {
        float[] result = [];
        WithArena(p, arena =>
        {
            void* a = (void*)arena;
            swarm_init(a, swarm_layout_bytes(in p), in p);
            act((nint)a);
            uint n = p.N;
            var x = new float[n]; var y = new float[n];
            var vx = new float[n]; var vy = new float[n]; var sp = new uint[n];
            swarm_read_state(a, x, y, vx, vy, sp);
            // interleave all components so a mismatch anywhere fails the compare
            result = new float[n * 4];
            for (uint i = 0; i < n; i++)
            {
                result[i * 4 + 0] = x[i];
                result[i * 4 + 1] = y[i];
                result[i * 4 + 2] = vx[i];
                result[i * 4 + 3] = vy[i];
            }
        });
        return result;
    }
}
