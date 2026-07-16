using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The RNG is the engine's single source of randomness, and determinism is a
/// product invariant: the asm stream must equal the reference splitmix64
/// stream u64-for-u64 for the same seed, or every downstream "same seed, same
/// universe" guarantee is void. This test is the oracle for the kernel's RNG
/// (docs/MASTERPLAN.md, decision 8).
/// </summary>
public sealed class RngTests
{
    [DllImport("swarm.kernel.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void swarm_rng_fill(ulong seed, [Out] ulong[] outBuffer, uint count);

    /// <summary>The reference splitmix64 — the exact algorithm the asm claims
    /// to implement (constants and shifts per rng.inc).</summary>
    private static ulong[] ReferenceStream(ulong seed, int count)
    {
        var result = new ulong[count];
        ulong state = seed;
        for (int i = 0; i < count; i++)
        {
            state += 0x9E3779B97F4A7C15;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            result[i] = z ^ (z >> 31);
        }
        return result;
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(0x1234_5678_9ABC_DEF0UL)]
    [InlineData(ulong.MaxValue)]
    public void RngStreamExactMatch(ulong seed)
    {
        const int count = 256;
        _ = NativeKernel.Handle; // ensure the DLL is loaded before the P/Invoke binds

        var actual = new ulong[count];
        swarm_rng_fill(seed, actual, count);

        Assert.Equal(ReferenceStream(seed, count), actual);
    }

    [Fact]
    public void RngZeroCountWritesNothing()
    {
        _ = NativeKernel.Handle;

        // A sentinel-filled buffer must come back untouched when count is 0 —
        // the fill loop's zero-count guard, exercised from the boundary.
        var buffer = new ulong[4];
        Array.Fill(buffer, 0xDEAD_BEEF_DEAD_BEEFUL);
        swarm_rng_fill(12345, buffer, 0);

        Assert.All(buffer, v => Assert.Equal(0xDEAD_BEEF_DEAD_BEEFUL, v));
    }
}
