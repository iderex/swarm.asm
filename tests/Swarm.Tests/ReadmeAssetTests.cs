using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The two pictures in the README are the kernel's own raster, and this is
/// what makes that checkable rather than asserted.
///
/// A screenshot would satisfy "the README shows the swarm" and lose the thing
/// the repository cares about: a caption naming a preset, a seed and a count
/// is a label unless the pixels beside it can be regenerated from those
/// inputs. The property that separates the two is the colour range.
/// <c>plot_core</c> clears to <c>PLOT_BG</c> and writes
/// <c>swarm_palette[species and 7]</c> and nothing else, so a frame it drew
/// holds at most nine colours and every one of them is in
/// <c>src/kernel/plot.inc</c>. Both files are decoded here, byte for byte, and
/// every pixel is checked against the palette parsed out of that source. A
/// hand-captured frame carries anti-aliasing, a window border or a compression
/// artefact, and fails on the first pixel that is none of the nine.
///
/// WHAT THIS CANNOT SAY. It does not re-run the simulation, so it cannot say
/// the committed pixels are the state that scene reaches after 600 steps -
/// only that they came out of this kernel's raster over this palette. It reads
/// no framebuffer and loads nothing native, so it runs on a machine that
/// cannot execute the DLL.
///
/// <see cref="ReadmeAssetRasterTests"/> is where that half lives. It produces
/// the raster and compares every pixel of the still against it, and it loads
/// the DLL to do so, which is why it is a class of its own rather than a test
/// here.
///
/// The GIF is decoded rather than sniffed for a second reason. An animation
/// that a viewer plays once, or at a speed of its own choosing, is a defect in
/// a README asset and not a rough edge, so the loop extension and the per-frame
/// delay are read out of the file, and every frame's LZW stream is expanded to
/// prove it yields exactly one index per pixel.
/// </summary>
public sealed class ReadmeAssetTests
{
    private const string StillPath = "docs/media/swarm-still.png";
    private const string LoopPath = "docs/media/swarm-loop.gif";

    private const uint StillW = 1024,
        StillH = 1024;
    private const uint LoopW = 384,
        LoopH = 384;
    private const int LoopFrames = 72;
    private const int LoopDelayCs = 4;

    /// <summary>The scene's particle count, and 2x2 per particle under FLAG_SPLAT.</summary>
    private const int SceneN = 8192;

    /// <summary>
    /// The whole README media budget, from the issue that asked for the
    /// assets: both files together stay well under 2 MB. Held here rather than
    /// in prose because a picture regenerated at a larger size is exactly the
    /// change that would walk past a sentence.
    /// </summary>
    private const long MediaBudgetBytes = 2 * 1024 * 1024;

    internal static string Abs(string rel) =>
        Path.Combine(Build.RepoRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// PLOT_BG followed by swarm_palette, read out of the kernel source rather
    /// than copied here, so a palette edit moves this test's expectation with
    /// it instead of leaving a stale constant behind.
    /// </summary>
    private static uint[] KernelColours()
    {
        string src = File.ReadAllText(Abs("src/kernel/plot.inc"));

        var bg = Regex.Match(src, @"^PLOT_BG\s*=\s*0x([0-9A-Fa-f]{8})", RegexOptions.Multiline);
        Assert.True(bg.Success, "PLOT_BG not found in src/kernel/plot.inc");

        int at = src.IndexOf("swarm_palette:", StringComparison.Ordinal);
        Assert.True(at >= 0, "swarm_palette not found in src/kernel/plot.inc");
        var entries = Regex
            .Matches(src[at..], @"^\s*dd\s+0x([0-9A-Fa-f]{8})", RegexOptions.Multiline)
            .Take(8)
            .Select(m => Convert.ToUInt32(m.Groups[1].Value, 16))
            .ToArray();
        Assert.Equal(8, entries.Length);

        return [Convert.ToUInt32(bg.Groups[1].Value, 16), .. entries];
    }

    /// <summary>
    /// Every pixel of the still is one of the nine colours the kernel raster
    /// can produce, and enough of them are lit to be the scene the caption
    /// names rather than an empty frame.
    /// </summary>
    [Fact]
    public void TheStillIsTheKernelRasterAndNothingElse()
    {
        uint[] allowed = KernelColours();
        (uint w, uint h, byte[] rgb) = DecodePng(File.ReadAllBytes(Abs(StillPath)));

        Assert.Equal(StillW, w);
        Assert.Equal(StillH, h);

        long lit = 0;
        for (long i = 0; i < (long)w * h; i++)
        {
            uint c = ((uint)rgb[i * 3] << 16) | ((uint)rgb[i * 3 + 1] << 8) | rgb[i * 3 + 2];
            int k = Array.IndexOf(allowed, c);
            Assert.True(k >= 0, $"pixel {i} is 0x{c:X6}, outside the plot palette");
            if (k != 0)
                lit++;
        }

        // A particle draws a 2x2 block and blocks overlap, so the lit count is
        // bounded above by 4n and is far from zero on a settled scene.
        Assert.InRange(lit, 1, 4L * SceneN);
    }

    /// <summary>
    /// The loop is a GIF89a that loops forever at a stated delay, its colour
    /// table is the kernel's palette, and every frame expands to exactly one
    /// palette index per pixel.
    /// </summary>
    [Fact]
    public void TheLoopIsALoopingGifOverTheKernelPalette()
    {
        uint[] allowed = KernelColours();
        byte[] d = File.ReadAllBytes(Abs(LoopPath));

        Assert.Equal("GIF89a", Encoding.ASCII.GetString(d, 0, 6));
        Assert.Equal(LoopW, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6)));
        Assert.Equal(LoopH, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)));

        // Packed byte: a global colour table is present and holds 2^(N+1)
        // entries. Nine colours need sixteen, so N is 3.
        Assert.True((d[10] & 0x80) != 0, "no global colour table");
        int gctEntries = 1 << ((d[10] & 0x07) + 1);
        Assert.Equal(16, gctEntries);

        for (int i = 0; i < allowed.Length; i++)
        {
            uint c =
                ((uint)d[13 + i * 3] << 16) | ((uint)d[14 + i * 3] << 8) | d[15 + i * 3];
            Assert.Equal(allowed[i], c);
        }

        int p = 13 + gctEntries * 3;
        bool loops = false;
        int frames = 0;
        while (p < d.Length)
        {
            switch (d[p])
            {
                case 0x21 when d[p + 1] == 0xFF:
                {
                    int n = d[p + 2];
                    string app = Encoding.ASCII.GetString(d, p + 3, n);
                    p += 3 + n;
                    byte[] body = ReadSubBlocks(d, ref p);
                    if (app == "NETSCAPE2.0")
                    {
                        // sub-block id 1, then a loop count of 0 = forever.
                        Assert.Equal(3, body.Length);
                        Assert.Equal(1, body[0]);
                        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)));
                        loops = true;
                    }
                    break;
                }
                case 0x21 when d[p + 1] == 0xF9:
                {
                    int n = d[p + 2];
                    Assert.Equal(4, n);
                    Assert.Equal(
                        LoopDelayCs,
                        BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 4)));
                    p += 3 + n;
                    Assert.Equal(0, d[p]); // block terminator
                    p++;
                    break;
                }
                case 0x2C:
                {
                    Assert.Equal(LoopW, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 5)));
                    Assert.Equal(LoopH, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 7)));
                    p += 10;
                    int minCodeSize = d[p++];
                    byte[] lzw = ReadSubBlocks(d, ref p);
                    byte[] px = DecodeLzw(lzw, minCodeSize);
                    Assert.Equal((int)(LoopW * LoopH), px.Length);
                    Assert.True(
                        px.All(i => i < allowed.Length),
                        $"frame {frames} indexes a colour-table entry the palette does not fill");
                    frames++;
                    break;
                }
                case 0x3B:
                    p = d.Length;
                    break;
                default:
                    Assert.Fail($"unexpected block 0x{d[p]:X2} at offset {p}");
                    break;
            }
        }

        Assert.True(loops, "no NETSCAPE2.0 loop extension: the animation would play once");
        Assert.Equal(LoopFrames, frames);
    }

    /// <summary>
    /// The README shows both assets above the fold and discloses the scene
    /// beside them, which is what makes the picture reproducible rather than
    /// decorative.
    /// </summary>
    [Fact]
    public void TheReadmeShowsTheSwarmWithItsDisclosure()
    {
        string[] lines = File.ReadAllLines(Abs("README.md"));

        int loopAt = Array.FindIndex(lines, l => l.Contains(LoopPath, StringComparison.Ordinal));
        int stillAt = Array.FindIndex(lines, l => l.Contains(StillPath, StringComparison.Ordinal));
        Assert.True(loopAt >= 0, $"README does not show {LoopPath}");
        Assert.True(stillAt >= 0, $"README does not link {StillPath}");

        // Above the fold: both sit inside the opening screen of the document,
        // ahead of the first section heading.
        int firstSection = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal));
        Assert.True(firstSection > 0, "README has no section heading");
        Assert.True(loopAt < firstSection, "the loop is below the first section heading");
        Assert.True(stillAt < firstSection, "the still is below the first section heading");

        // The caption names every field the picture cannot be regenerated
        // without. Each is checked on its own so a dropped one says which.
        string head = string.Join('\n', lines[..firstSection]);
        foreach (string field in
            (string[])["8,192", "0x9E3779B97F4A7C15", "rmax = 0.05", "FLAG_GRID", "FLAG_SPLAT",
                       "force_path = 1", "600", "--asset"])
        {
            Assert.True(
                head.Contains(field, StringComparison.Ordinal),
                $"the disclosure beside the picture does not state {field}");
        }
    }

    /// <summary>
    /// Both assets together stay inside the README media budget.
    /// </summary>
    [Fact]
    public void TheAssetsStayInsideTheMediaBudget()
    {
        long total = new FileInfo(Abs(StillPath)).Length + new FileInfo(Abs(LoopPath)).Length;
        Assert.InRange(total, 1, MediaBudgetBytes);
    }

    // --- decoders ----------------------------------------------------------

    /// <summary>
    /// Enough of RFC 2083 to read what the harness writes: colour type 2, bit
    /// depth 8, filter type 0 on every row. Anything else is refused rather
    /// than handled, because a picture in this repository that needed a wider
    /// decoder did not come from the encoder that is supposed to have made it.
    /// </summary>
    internal static (uint W, uint H, byte[] Rgb) DecodePng(byte[] d)
    {
        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], d[..8]);

        uint w = 0,
            h = 0;
        var idat = new MemoryStream();
        int p = 8;
        while (p < d.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            string type = Encoding.ASCII.GetString(d, p + 4, 4);
            ReadOnlySpan<byte> body = d.AsSpan(p + 8, len);
            switch (type)
            {
                case "IHDR":
                    w = BinaryPrimitives.ReadUInt32BigEndian(body);
                    h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    Assert.Equal(8, body[8]); // bit depth
                    Assert.Equal(2, body[9]); // truecolour
                    Assert.Equal(0, body[12]); // no interlace
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
            }
            p += 12 + len;
        }

        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.CopyTo(raw);
        byte[] bytes = raw.ToArray();
        Assert.Equal(h * (1 + w * 3), (uint)bytes.Length);

        var rgb = new byte[(long)w * h * 3];
        for (uint y = 0; y < h; y++)
        {
            long row = y * (1L + w * 3);
            Assert.Equal(0, bytes[row]); // filter type None
            Array.Copy(bytes, row + 1, rgb, y * w * 3L, w * 3L);
        }
        return (w, h, rgb);
    }

    private static byte[] ReadSubBlocks(byte[] d, ref int p)
    {
        var ms = new MemoryStream();
        while (d[p] != 0)
        {
            int n = d[p++];
            ms.Write(d, p, n);
            p += n;
        }
        p++;
        return ms.ToArray();
    }

    /// <summary>
    /// GIF's variable-width LZW, the reading half. The table grows one entry
    /// behind the encoder, which is the asymmetry a hand-written encoder gets
    /// wrong, so decoding here is what proves the file is readable at all.
    /// </summary>
    private static byte[] DecodeLzw(byte[] data, int minCodeSize)
    {
        int clear = 1 << minCodeSize;
        int eoi = clear + 1;
        int codeSize = minCodeSize + 1;
        long bit = 0;

        int Read()
        {
            int v = 0;
            for (int k = 0; k < codeSize; k++)
            {
                long byteAt = bit >> 3;
                if (byteAt >= data.Length)
                    return -1;
                v |= ((data[byteAt] >> (int)(bit & 7)) & 1) << k;
                bit++;
            }
            return v;
        }

        var table = new List<byte[]>();
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < clear; i++)
                table.Add([(byte)i]);
            table.Add([]); // clear
            table.Add([]); // end of information
            codeSize = minCodeSize + 1;
        }

        Reset();
        var outp = new MemoryStream();
        int prev = -1;
        while (true)
        {
            int c = Read();
            if (c < 0 || c == eoi)
                break;
            if (c == clear)
            {
                Reset();
                prev = -1;
                continue;
            }

            byte[] entry;
            if (c < table.Count)
                entry = table[c];
            else
            {
                Assert.True(prev >= 0 && c == table.Count, $"undefined LZW code {c}");
                entry = [.. table[prev], table[prev][0]];
            }
            outp.Write(entry);

            if (prev >= 0)
            {
                table.Add([.. table[prev], entry[0]]);
                if (table.Count > (1 << codeSize) - 1 && codeSize < 12)
                    codeSize++;
            }
            prev = c;
        }
        return outp.ToArray();
    }
}
