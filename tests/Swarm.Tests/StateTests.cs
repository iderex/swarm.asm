using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// swarm_read_state is the id-ordered copy-out that keeps the arena opaque to
/// consumers (masterplan decision 5). These tests pin it against the seeded
/// init state: right after init id[i] = i, so the copy is the identity, and
/// the returned arrays must equal the same splitmix64 oracle the init test
/// uses. It is also the seam every kernel golden reads through, so getting the
/// scatter and the n-boundary right now is load-bearing.
/// </summary>
public sealed unsafe class StateTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    private static SwarmParams Params(uint n, uint species, ulong seed)
    {
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = species, Seed = seed,
            RMax = 0.05f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f,
            ForcePath = 0, Flags = 0,
        };
        for (int i = 0; i < species * species; i++) p.Matrix[i] = 0.1f;
        return p;
    }

    [Theory]
    [InlineData(1u, 1u, 7UL)]
    [InlineData(100u, 3u, 0xABCDUL)]
    [InlineData(4096u, 8u, 0xDEADBEEFUL)]
    public unsafe void ReadStateReturnsSeededStateInIdOrder(uint n, uint species, ulong seed)
    {
        _ = NativeKernel.Handle;
        var p = Params(n, species, seed);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));

            var x = new float[n];
            var y = new float[n];
            var vx = new float[n];
            var vy = new float[n];
            var sp = new uint[n];
            swarm_read_state(arena, x, y, vx, vy, sp);

            var rng = new TestOracle.SplitMix64(seed);
            for (uint i = 0; i < n; i++)
            {
                var (ex, ey, es) = TestOracle.DrawParticle(rng, species);
                // id[i] = i right after init, so the copy-out is the identity.
                Assert.Equal(ex, x[i]);
                Assert.Equal(ey, y[i]);
                Assert.Equal(es, sp[i]);
                Assert.Equal(0f, vx[i]);
                Assert.Equal(0f, vy[i]);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    [Fact]
    public unsafe void ReadStateHonorsIdPermutation()
    {
        // After init id[i] = i, so the copy is the identity and a scatter that
        // ignored id would still pass. Write a non-identity permutation into
        // id_out (white-box) so the indirection is actually exercised now,
        // before sorts (M2) produce real permutations.
        _ = NativeKernel.Handle;
        const uint n = 8, species = 2;
        var p = Params(n, species, 0x55);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));

            uint padded = ((n + 15u) & ~15u) + 16u;
            long stride = padded * 4L;
            var xOut = (float*)((byte*)arena + 512);
            var yOut = (float*)((byte*)arena + 512 + stride);
            var vxOut = (float*)((byte*)arena + 512 + 2 * stride);
            var vyOut = (float*)((byte*)arena + 512 + 3 * stride);
            var spOut = (uint*)((byte*)arena + 512 + 4 * stride);
            var idOut = (uint*)((byte*)arena + 512 + 5 * stride);

            // Reverse permutation, and a DISTINCT marker per component so a bug
            // that swapped destination pointers (e.g. vx vs vy) is caught too.
            for (uint i = 0; i < n; i++)
            {
                idOut[i] = n - 1 - i;
                xOut[i] = 1000f + i;
                yOut[i] = 2000f + i;
                vxOut[i] = 3000f + i;
                vyOut[i] = 4000f + i;
                spOut[i] = 5000u + i;
            }

            var x = new float[n];
            var y = new float[n];
            var vx = new float[n];
            var vy = new float[n];
            var sp = new uint[n];
            swarm_read_state(arena, x, y, vx, vy, sp);

            // slot i's markers land at caller index id[i] = n-1-i, each in its
            // own destination array.
            for (uint i = 0; i < n; i++)
            {
                uint dst = n - 1 - i;
                Assert.Equal(1000f + i, x[dst]);
                Assert.Equal(2000f + i, y[dst]);
                Assert.Equal(3000f + i, vx[dst]);
                Assert.Equal(4000f + i, vy[dst]);
                Assert.Equal(5000u + i, sp[dst]);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    [Fact]
    public unsafe void ReadStateWritesExactlyNElements()
    {
        _ = NativeKernel.Handle;
        var p = Params(100, 3, 0x1234);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));

            // One guard slot past n on each array: read_state must not touch it.
            const int n = 100;
            var x = new float[n + 1];
            var y = new float[n + 1];
            var vx = new float[n + 1];
            var vy = new float[n + 1];
            var sp = new uint[n + 1];
            x[n] = y[n] = vx[n] = vy[n] = float.NaN;
            sp[n] = 0xDEADBEEF;
            swarm_read_state(arena, x, y, vx, vy, sp);

            Assert.True(float.IsNaN(x[n]), "read_state wrote past n (x)");
            Assert.True(float.IsNaN(y[n]));
            Assert.True(float.IsNaN(vx[n]));
            Assert.True(float.IsNaN(vy[n]));
            Assert.Equal(0xDEADBEEFu, sp[n]);
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }
}
