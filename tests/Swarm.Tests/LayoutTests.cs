using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// swarm_layout_bytes is part of the ABI contract: the same params must yield
/// the same size forever (a change is an ABI version bump). These tests pin
/// the formula against an independent C# mirror, the fail-closed zero on
/// invalid params, monotonicity in n, and the 64-byte-multiple guarantee.
/// </summary>
public sealed class LayoutTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    private static SwarmParams Valid(uint n = 4096, float rmax = 0.05f) => new()
    {
        Version = 1,
        N = n,
        SpeciesN = 3,
        Seed = 1,
        RMax = rmax,
        Beta = 0.3f,
        Dt = 0.02f,
        Friction = 0.71f,
        ForceScale = 10f,
        ForcePath = 0,
        Flags = 0,
    };

    /// <summary>The independent mirror of the pinned layout formula
    /// (masterplan decisions 1 and 3).</summary>
    private static ulong Mirror(uint n, float rmax)
    {
        uint padded = ((n + 15) & ~15u) + 16;
        uint g = 4;
        while (g < 512 && 1.0f / (2 * g) >= rmax)
        {
            g *= 2;
        }
        ulong total = 512 + (ulong)padded * 52 + ((ulong)g * g + 1) * 4;
        return (total + 63) & ~63UL;
    }

    [Theory]
    [InlineData(1u, 0.25f)]          // min n; min g (=4)
    [InlineData(4096u, 0.05f)]       // mid
    [InlineData(8192u, 0.001953f)]   // M1 headline-ish rmax -> g = 512
    [InlineData(1_048_576u, 0.001953f)] // max n, g = 512 (the 1M scene)
    [InlineData(1_048_576u, 0.25f)]  // max n, min g
    [InlineData(16u, 0.05f)]         // n = 0 mod 16: the +16 tail must still pad
    [InlineData(17u, 0.1f)]
    public void MatchesTheMirrorFormula(uint n, float rmax)
    {
        _ = NativeKernel.Handle;
        var p = Valid(n, rmax);
        Assert.Equal(Mirror(n, rmax), swarm_layout_bytes(in p));
    }

    [Fact]
    public void InvalidParamsYieldZero()
    {
        _ = NativeKernel.Handle;

        var bad = Valid();
        bad.Version = 2;
        Assert.Equal(0ul, swarm_layout_bytes(in bad));

        bad = Valid();
        bad.N = 0;
        Assert.Equal(0ul, swarm_layout_bytes(in bad));

        bad = Valid();
        bad.RMax = 0.26f;
        Assert.Equal(0ul, swarm_layout_bytes(in bad));

        bad = Valid();
        bad.Flags = 2; // bit 0 (FLAG_GRID) is valid; any reserved bit rejects
        Assert.Equal(0ul, swarm_layout_bytes(in bad));

        bad = Valid();
        bad.ForcePath = 4; // 0 auto / 1 AVX2 / 2 AVX-512 / 3 scalar; 4 invalid
        Assert.Equal(0ul, swarm_layout_bytes(in bad));

        bad = Valid();
        bad.Matrix[2 * 8 + 2] = 1.5f; // inside species range, out of [-1,1]
        Assert.Equal(0ul, swarm_layout_bytes(in bad));

        // Beyond species_n the matrix is dead storage and must NOT affect
        // validity (the parser leaves it zero; a caller-built struct may not).
        var ok = Valid();
        ok.Matrix[7 * 8 + 7] = 99f;
        Assert.NotEqual(0ul, swarm_layout_bytes(in ok));
    }

    [Fact]
    public void MonotonicInNAndAlwaysA64Multiple()
    {
        _ = NativeKernel.Handle;
        ulong prev = 0;
        for (uint n = 1; n <= 1_048_576; n = n * 2 + 3)
        {
            var p = Valid(n);
            ulong size = swarm_layout_bytes(in p);
            Assert.True(size >= prev, $"size must not shrink as n grows (n={n})");
            Assert.Equal(0ul, size % 64);
            prev = size;
        }
    }
}
