using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The shipped instrument, end to end. <c>FrameStatsTests</c> proves the
/// reduction across the P/Invoke seam; nothing there says swarm.exe calls it,
/// and a capture mode that quietly stopped reporting would leave every one of
/// those tests green. This is the leg that watches the real executable run a
/// real capture and then reads what it wrote.
///
/// It costs about a minute, because that is what the measurement costs:
/// <c>CAPTURE_FRAMES</c> is 3,600 and the loop is paced to 60 fps, so the run
/// is the instrument at its shipped length rather than a shortened stand-in.
/// A shortened one would not exercise the sample count every recorded figure
/// in docs/BENCHMARKS.md was taken at.
///
/// The run happens on a desktop of this test's own making, for the reason
/// <c>PresetRefusalDialogTests</c> gives: the capture puts up a real window,
/// and a test that takes the screen from whoever is at the machine is a test
/// that gets switched off.
/// </summary>
public sealed class CaptureReportTests
{
    /// <summary>CAPTURE_FRAMES in src/swarm.asm.</summary>
    private const int CaptureFrames = 3600;

    /// <summary>CAP_HEADER_BYTES in src/swarm.asm.</summary>
    private const int HeaderBytes = 40;

    /// <summary>
    /// CAP_SERIES in src/swarm.asm, and the plane order the dump is written
    /// in. The first three are the phases the work window is split into; the
    /// fourth is the whole window they add up to.
    /// </summary>
    private static readonly string[] Planes = ["step", "plot", "blit", "frame"];

    [Fact]
    public void ACaptureRunWritesTheDumpAndAReportThatAgreesWithIt()
    {
        var work = Directory.CreateTempSubdirectory("swarm-capture-");
        try
        {
            using var desktop = HiddenDesktop.Create();
            using var child = desktop.Launch($"\"{Build.ExePath}\" -capture", work.FullName);

            // 3,600 paced frames is about 60 s; the margin is for a loaded host,
            // and an overrun throws rather than being read as a pass.
            Assert.Equal(0, child.WaitForExit(TimeSpan.FromSeconds(300)));

            var dumpPath = Path.Combine(work.FullName, "swarm-frames.bin");
            var reportPath = Path.Combine(work.FullName, "swarm-frames.txt");
            Assert.True(File.Exists(dumpPath), $"no swarm-frames.bin in {work.FullName}");
            Assert.True(File.Exists(reportPath), $"no swarm-frames.txt in {work.FullName}");

            var dump = File.ReadAllBytes(dumpPath);
            Assert.Equal("SWRMFRM2", System.Text.Encoding.ASCII.GetString(dump, 0, 8));
            ulong freq = BitConverter.ToUInt64(dump, 8);
            ulong count = BitConverter.ToUInt64(dump, 16);
            uint n = BitConverter.ToUInt32(dump, 24);
            uint flags = BitConverter.ToUInt32(dump, 28);
            ulong seed = BitConverter.ToUInt64(dump, 32);
            Assert.Equal((ulong)CaptureFrames, count);
            Assert.Equal(HeaderBytes + 8 * (long)count * Planes.Length, dump.LongLength);

            var planes = new ulong[Planes.Length][];
            for (int p = 0; p < Planes.Length; p++)
            {
                planes[p] = new ulong[count];
                long at = HeaderBytes + 8 * (long)count * p;
                for (int i = 0; i < planes[p].Length; i++)
                {
                    planes[p][i] = BitConverter.ToUInt64(dump, checked((int)(at + 8 * i)));
                }
            }

            // The three phases are consecutive deltas of four reads and the
            // frame is the outermost pair, so the identity is exact rather than
            // approximate. It is what says the planes describe one window and
            // not three unrelated ones: a read taken on the wrong side of a
            // call, or a plane written at the wrong offset, breaks it.
            for (int i = 0; i < (int)count; i++)
            {
                Assert.Equal(planes[3][i], planes[0][i] + planes[1][i] + planes[2][i]);
            }

            // The dump is the recorded order, and the reduction sorts in place,
            // so this is the regression test for the two ever being reordered:
            // 3,600 consecutive real frame times are never non-decreasing, and
            // an ascending dump means the sort ran before the write. Every
            // plane is checked, because the reduction sorts each one.
            for (int p = 0; p < Planes.Length; p++)
            {
                Assert.False(
                    IsAscending(planes[p]),
                    $"the {Planes[p]} plane of swarm-frames.bin came back in ascending order, so the reduction sorted the buffer before the dump was written");
            }

            var report = File.ReadAllText(reportPath);

            // Every scene field the report states is read back out of the dump
            // rather than out of the source, so a report describing a different
            // run than the file beside it fails here.
            Assert.Contains($"samples={count}", report, StringComparison.Ordinal);
            Assert.Contains($"n={n}", report, StringComparison.Ordinal);
            Assert.Contains($"flags=0x{flags:X8}", report, StringComparison.Ordinal);
            Assert.Contains($"seed=0x{seed:X16}", report, StringComparison.Ordinal);
            Assert.Contains($"qpc_freq={freq}", report, StringComparison.Ordinal);

            // And the figures against docs/BENCHMARKS.md's own definition of
            // them, applied to the samples this run actually produced, one
            // line per plane. A report that reduced the wrong plane, or reduced
            // one plane four times, fails here rather than reading plausibly.
            for (int p = 0; p < Planes.Length; p++)
            {
                var sorted = (ulong[])planes[p].Clone();
                Array.Sort(sorted);
                ulong sum = 0;
                foreach (var t in sorted)
                {
                    sum += t;
                }
                int last = sorted.Length - 1;
                AssertFigure(report, Planes[p], "mean", sum / (ulong)sorted.Length, freq);
                AssertFigure(report, Planes[p], "p50", sorted[(int)Math.Floor(0.50 * last)], freq);
                AssertFigure(report, Planes[p], "p99", sorted[(int)Math.Floor(0.99 * last)], freq);
                AssertFigure(report, Planes[p], "max", sorted[last], freq);
            }
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The report prints milliseconds to three decimals, which is whole
    /// microseconds, and the exe rounds half up on the way there.
    /// </summary>
    private static void AssertFigure(string report, string plane, string name, ulong ticks, ulong freq)
    {
        ulong us = (ticks * 1_000_000 + freq / 2) / freq;
        string figure = $"{name}={us / 1000}.{us % 1000:D3} ms";

        // The figure has to be on its own plane's line: the four lines share
        // every name, so a search over the whole report would pass on another
        // plane's copy of the number.
        string prefix = plane.PadRight(5) + " ";
        string? line = null;
        foreach (var candidate in report.Split('\n'))
        {
            if (candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                line = candidate;
            }
        }
        Assert.True(line is not null, $"no '{plane}' line in the report");
        Assert.Contains(figure, line!, StringComparison.Ordinal);
    }

    private static bool IsAscending(ulong[] values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
            {
                return false;
            }
        }
        return true;
    }
}
