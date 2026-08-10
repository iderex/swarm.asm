using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// swarm_plot rasterizes the current state into a BGRA framebuffer (decision
/// 9): clear to the background, then one per-species coloured pixel per
/// particle, with (x,y) in [0,1) truncated to a pixel and belted against
/// w-1 / h-1. A golden test: known positions written straight into bank OUT,
/// then the exact pixels are checked.
/// </summary>
public sealed unsafe class PlotTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_plot(void* arena, uint[] bgra, uint w, uint h);

    private const uint Bg = 0x001A1A22;

    /// <summary>src/kernel/abi.inc FLAG_SPLAT - the 2x2 raster.</summary>
    private const uint FlagSplat = 2;

    /// <summary>
    /// Untouched words past the framebuffer. The clear covers w*h words, so a
    /// guard word that is still zero is a word nothing wrote: neither the
    /// clear nor a splat that ran off the end of the buffer.
    /// </summary>
    private const int Guard = 16;

    private static readonly uint[] Palette =
        [0x00FF4040, 0x0040FF40, 0x004080FF, 0x00FFD040,
         0x00FF40FF, 0x0040FFFF, 0x00FF8020, 0x00A060FF];

    private static SwarmParams Params(uint n, uint species, uint flags = 0)
    {
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = species, Seed = 1,
            RMax = 0.05f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f,
            ForcePath = 0, Flags = flags,
        };
        for (int i = 0; i < species * species; i++) p.Matrix[i] = 0.1f;
        return p;
    }

    /// <summary>
    /// Plots one particle at (x, y) of species 0 into a w*h framebuffer with a
    /// guard region behind it, and returns the whole buffer including the
    /// guard.
    /// </summary>
    private static uint[] PlotOne(float x, float y, uint w, uint h, uint flags)
    {
        _ = NativeKernel.Handle;
        var p = Params(1, 1, flags);
        ulong size = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, size);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));
            uint padded = *(uint*)((byte*)arena + 32);
            long stride = padded * 4L;
            *(float*)((byte*)arena + 512) = x;
            *(float*)((byte*)arena + 512 + stride) = y;
            *(uint*)((byte*)arena + 512 + 4 * stride) = 0;

            var bgra = new uint[w * h + Guard];
            swarm_plot(arena, bgra, w, h);
            return bgra;
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    /// <summary>The set of pixel indices carrying a palette colour.</summary>
    private static HashSet<int> Lit(uint[] bgra, uint w, uint h)
    {
        var lit = new HashSet<int>();
        for (int i = 0; i < w * h; i++)
        {
            if (bgra[i] != Bg) lit.Add(i);
        }
        for (int i = (int)(w * h); i < bgra.Length; i++)
        {
            Assert.Equal(0u, bgra[i]); // nothing wrote past the framebuffer
        }
        return lit;
    }

    [Fact]
    public void PlotsEachParticleAsOneSpeciesColouredPixel()
    {
        _ = NativeKernel.Handle;
        const uint n = 4, species = 4, w = 8, h = 8;
        var p = Params(n, species);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));

            uint padded = *(uint*)((byte*)arena + 32);
            long stride = padded * 4L;
            var xOut = (float*)((byte*)arena + 512);
            var yOut = (float*)((byte*)arena + 512 + stride);
            var spOut = (uint*)((byte*)arena + 512 + 4 * stride);

            // (x, y, species) -> expected (px, py) = (trunc(x*8), trunc(y*8))
            (float x, float y, uint s, int px, int py)[] pts =
            [
                (0.1f, 0.1f, 0, 0, 0),
                (0.9f, 0.1f, 1, 7, 0),
                (0.1f, 0.9f, 2, 0, 7),
                (0.5f, 0.5f, 3, 4, 4),
            ];
            for (int i = 0; i < n; i++)
            {
                xOut[i] = pts[i].x; yOut[i] = pts[i].y; spOut[i] = pts[i].s;
            }

            var bgra = new uint[w * h];
            swarm_plot(arena, bgra, w, h);

            foreach (var (_, _, s, px, py) in pts)
            {
                Assert.Equal(Palette[s], bgra[py * w + px]);
            }
            // an untouched pixel stays background
            Assert.Equal(Bg, bgra[2 * w + 2]);
            // every pixel is either background or a used palette colour
            var used = new HashSet<uint>(pts.Select(t => Palette[t.s])) { Bg };
            Assert.All(bgra, px => Assert.Contains(px, used));
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    [Fact]
    public void BeltClampsAnOutOfRangePositionToTheLastPixel()
    {
        // wrap keeps positions < 1, but the min-against-w-1/h-1 belt must still
        // hold if a boundary 1.0 ever reaches the raster: 1.0*8 = 8 -> clamp 7.
        _ = NativeKernel.Handle;
        const uint n = 1, species = 1, w = 8, h = 8;
        var p = Params(n, species);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));
            uint padded = *(uint*)((byte*)arena + 32);
            long stride = padded * 4L;
            *(float*)((byte*)arena + 512) = 1.0f;              // x = 1.0
            *(float*)((byte*)arena + 512 + stride) = 1.0f;     // y = 1.0
            *(uint*)((byte*)arena + 512 + 4 * stride) = 0;     // species 0

            var bgra = new uint[w * h];
            swarm_plot(arena, bgra, w, h);

            Assert.Equal(Palette[0], bgra[7 * w + 7]); // clamped to (7,7)
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    [Fact]
    public void BeltClampsANegativePositionToPixelZero()
    {
        // The lower belt: a negative position (wrap never produces one, but the
        // clamp is a complete backstop) must land at pixel 0, not underflow the
        // buffer.
        _ = NativeKernel.Handle;
        const uint n = 1, species = 1, w = 8, h = 8;
        var p = Params(n, species);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));
            uint padded = *(uint*)((byte*)arena + 32);
            long stride = padded * 4L;
            *(float*)((byte*)arena + 512) = -0.5f;             // x < 0
            *(float*)((byte*)arena + 512 + stride) = -0.5f;    // y < 0
            *(uint*)((byte*)arena + 512 + 4 * stride) = 0;

            var bgra = new uint[w * h];
            swarm_plot(arena, bgra, w, h);

            Assert.Equal(Palette[0], bgra[0]); // clamped to (0,0)
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    // ---------------------------------------------------------------------
    // FLAG_SPLAT: the 2x2 raster (decision 9's amendment). The mode is carried
    // platform-side in SwarmParams.flags, so the goldens below are the same
    // shape as the 1px ones with the bit set - and the three above are left
    // exactly as they were, which is what proves the added mode did not alter
    // the existing one.
    // ---------------------------------------------------------------------

    [Fact]
    public void SplatModeLightsTheAnchorAndItsThreeNeighbours()
    {
        // (0.1, 0.1) at 8x8 anchors at (0,0), well away from either edge, so
        // the block is the full 2x2 and nothing is clamped.
        var bgra = PlotOne(0.1f, 0.1f, 8, 8, FlagSplat);

        Assert.Equal(new HashSet<int> { 0, 1, 8, 9 }, Lit(bgra, 8, 8));
        Assert.All(new[] { 0, 1, 8, 9 }, i => Assert.Equal(Palette[0], bgra[i]));
    }

    [Fact]
    public void SplatModeIsTheOnlyDifferenceFromTheOnePixelRaster()
    {
        // The same position under both modes: the 1px raster lights exactly
        // the anchor the splat starts from. Without this the test above would
        // pass for a splat anchored somewhere else entirely.
        Assert.Equal(new HashSet<int> { 0 }, Lit(PlotOne(0.1f, 0.1f, 8, 8, 0), 8, 8));
    }

    [Fact]
    public void SplatCollapsesOntoTheAnchorOneUlpUnderOne()
    {
        // The input that separates a correct clamp from one that happens to
        // work: x = y = the largest float below 1.0, which truncates to the
        // LAST pixel rather than past it. 0.99999994f * 8 = 7.9999995 -> 7, so
        // the anchor is (7,7) and both neighbours are outside an 8x8 buffer.
        // A splat that added +1 and +w unconditionally would write words 64,
        // 71 and 72 - all in the guard region.
        float justUnderOne = MathF.BitDecrement(1.0f);
        Assert.True(justUnderOne < 1.0f && justUnderOne * 8.0f < 8.0f);

        var bgra = PlotOne(justUnderOne, justUnderOne, 8, 8, FlagSplat);

        Assert.Equal(new HashSet<int> { 7 * 8 + 7 }, Lit(bgra, 8, 8));
    }

    [Fact]
    public void SplatOnTheLastColumnDoesNotWrapOntoTheNextRow()
    {
        // The edge the collapse test above cannot see, because at (7,7) an
        // unclamped px+1 leaves the buffer and lands in the guard. Here it
        // would stay inside it: anchor (7,0), and px+1 = 8 is word 8, which is
        // pixel (0,1). A row wrap is a wrong picture, not a crash, so it needs
        // its own golden.
        var bgra = PlotOne(MathF.BitDecrement(1.0f), 0.1f, 8, 8, FlagSplat);

        Assert.Equal(new HashSet<int> { 7, 7 + 8 }, Lit(bgra, 8, 8));
        Assert.Equal(Bg, bgra[8]); // (0,1): where a wrapped write would land
    }

    [Fact]
    public void SplatOnTheLastRowStaysInsideTheBuffer()
    {
        // Anchor (0,7): py+1 = 8 would be word 64, one past the framebuffer.
        var bgra = PlotOne(0.1f, MathF.BitDecrement(1.0f), 8, 8, FlagSplat);

        Assert.Equal(new HashSet<int> { 7 * 8, 7 * 8 + 1 }, Lit(bgra, 8, 8));
    }

    [Fact]
    public void SplatDoesNotRelaxTheLowerBelt()
    {
        // A negative position is still clamped to pixel 0 first; the splat
        // then grows from there. It must not underflow the buffer, and the
        // block must be the same one an in-range anchor at (0,0) produces.
        var bgra = PlotOne(-0.5f, -0.5f, 8, 8, FlagSplat);

        Assert.Equal(new HashSet<int> { 0, 1, 8, 9 }, Lit(bgra, 8, 8));
    }

    [Fact]
    public void SplatIsCorrectOnANonSquareFramebuffer()
    {
        // w != h catches an index built from the wrong dimension: the row
        // stride is w, and a splat that stepped by h instead would land two
        // rows down here and nowhere visible on an 8x8.
        const uint w = 5, h = 3;
        var bgra = PlotOne(0.5f, 0.1f, w, h, FlagSplat); // trunc(2.5)=2, trunc(0.3)=0

        Assert.Equal(new HashSet<int> { 2, 3, 2 + (int)w, 3 + (int)w }, Lit(bgra, w, h));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(FlagSplat)]
    public void TheFramebufferIsIdenticalAcrossRunsInEitherMode(uint flags)
    {
        // Determinism per mode: same state, same framebuffer, byte for byte.
        var a = PlotOne(0.375f, 0.625f, 8, 8, flags);
        var b = PlotOne(0.375f, 0.625f, 8, 8, flags);

        Assert.Equal(a, b);
    }
}
