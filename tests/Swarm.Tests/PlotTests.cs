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
    private static readonly uint[] Palette =
        [0x00FF4040, 0x0040FF40, 0x004080FF, 0x00FFD040,
         0x00FF40FF, 0x0040FFFF, 0x00FF8020, 0x00A060FF];

    private static SwarmParams Params(uint n, uint species)
    {
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = species, Seed = 1,
            RMax = 0.05f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f,
            ForcePath = 0, Flags = 0,
        };
        for (int i = 0; i < species * species; i++) p.Matrix[i] = 0.1f;
        return p;
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
}
