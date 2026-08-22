using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The still in the README is this kernel's raster of the scene its caption
/// names, proved by producing that raster here and comparing every pixel.
///
/// <see cref="ReadmeAssetTests"/> reads both committed files without loading
/// anything native, and states in its own words what that costs: it cannot say
/// the committed pixels are the state the scene reaches after its warm-up,
/// only that they came out of this kernel's palette. This class is that
/// missing half, and it is a separate class because it loads the DLL. The
/// palette tests stay native-free and keep running on a machine that cannot
/// execute one; this one skips there and is authoritative in CI, where
/// SWARM_REQUIRE_NATIVE turns the skip into a failure.
///
/// WHAT IT DEFENDS. A change that moves the simulation - the RNG, the force
/// pass, the integrator, the raster - moves these pixels, and the committed
/// picture then stops being what the command in the README produces. Nothing
/// noticed: the new picture is still nine colours out of the same table, so
/// every palette assertion passes, the file sizes stay inside the budget, and
/// the README goes on offering a command whose output no longer matches what
/// is displayed beside it. This reds until the assets are regenerated.
///
/// The scene is read out of the assembled image rather than restated here, so
/// "the default preset assembled into swarm.exe" in the caption is literally
/// what runs, and a preset edit moves this expectation with it. The two fields
/// the caption declares pinned are pinned here and nothing else is touched.
///
/// WHAT IT DOES NOT COVER. The loop's 72 frames. Both pictures are taken from
/// one state by one generator run, so a simulation change moves the still as
/// well, and the loop's structure - its palette, its delay, its frame count
/// and that every frame decodes - is held by the sibling. A loop regenerated
/// on its own from some other state would pass both.
/// </summary>
public sealed unsafe class ReadmeAssetRasterTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_plot(void* arena, uint* bgra, uint w, uint h);

    private const string StillPath = "docs/media/swarm-still.png";

    /// <summary>Steps before the picture is taken; the generator's own warm-up.</summary>
    private const uint Warm = 600;

    /// <summary>FRAME_W / FRAME_H, src/swarm.asm - the shipped framebuffer.</summary>
    private const uint StillW = 1024,
        StillH = 1024;

    [Fact]
    public void TheStillIsTheShippedSceneAfterItsWarmUp()
    {
        (uint w, uint h, byte[] rgb) = ReadmeAssetTests.DecodePng(
            File.ReadAllBytes(ReadmeAssetTests.Abs(StillPath)));

        Assert.Equal(StillW, w);
        Assert.Equal(StillH, h);

        uint[] frame = ShippedSceneRaster(StillW, StillH);

        for (int i = 0; i < frame.Length; i++)
        {
            // swarm_plot writes BGRA with the top byte unused; the PNG carries
            // the same three channels in R, G, B order.
            uint kernel = frame[i] & 0x00FFFFFFu;
            uint committed =
                ((uint)rgb[i * 3] << 16) | ((uint)rgb[i * 3 + 1] << 8) | rgb[i * 3 + 2];

            if (kernel != committed)
            {
                Assert.Fail(
                    $"{StillPath} disagrees with the kernel at pixel {i} "
                        + $"({i % (int)StillW}, {i / (int)StillW}): the file holds "
                        + $"0x{committed:X6}, the raster of the shipped scene after "
                        + $"{Warm} steps holds 0x{kernel:X6}. Regenerate the assets.");
            }
        }
    }

    /// <summary>
    /// The shipped preset, taken out of the assembled image, run to the
    /// warm-up and rasterized. The two pins are the ones the README caption
    /// declares: force_path is AVX2 where the image leaves it on auto, because
    /// auto resolves per host and a committed picture has to name the path
    /// that drew it, and FLAG_SPLAT is set where the image leaves it to the
    /// -splat toggle.
    /// </summary>
    private static uint[] ShippedSceneRaster(uint w, uint h)
    {
        _ = NativeKernel.Handle;

        SwarmParams p = ExePreset.Extract(File.ReadAllBytes(Build.ExePath));
        p.ForcePath = 1;
        p.Flags |= ExePreset.FlagSplat;

        ulong bytes = swarm_layout_bytes(in p);
        Assert.NotEqual(0ul, bytes);

        void* arena = NativeMemory.AlignedAlloc((nuint)bytes, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, bytes, in p));
            swarm_step(arena, Warm);

            var frame = new uint[checked((int)(w * h))];
            fixed (uint* fb = frame)
            {
                swarm_plot(arena, fb, w, h);
            }
            return frame;
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }
}
