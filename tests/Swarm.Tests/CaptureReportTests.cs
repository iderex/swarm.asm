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
            Assert.Equal("SWRMFRM1", System.Text.Encoding.ASCII.GetString(dump, 0, 8));
            ulong freq = BitConverter.ToUInt64(dump, 8);
            ulong count = BitConverter.ToUInt64(dump, 16);
            uint n = BitConverter.ToUInt32(dump, 24);
            uint flags = BitConverter.ToUInt32(dump, 28);
            ulong seed = BitConverter.ToUInt64(dump, 32);
            Assert.Equal((ulong)CaptureFrames, count);
            Assert.Equal(HeaderBytes + 8 * (long)count, dump.LongLength);

            var samples = new ulong[count];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToUInt64(dump, HeaderBytes + 8 * i);
            }

            // The dump is the recorded order, and the reduction sorts in place,
            // so this is the regression test for the two ever being reordered:
            // 3,600 consecutive real frame times are never non-decreasing, and
            // an ascending dump means the sort ran before the write.
            Assert.False(
                IsAscending(samples),
                "swarm-frames.bin came back in ascending order, so the reduction sorted the buffer before the dump was written");

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
            // them, applied to the samples this run actually produced.
            var sorted = (ulong[])samples.Clone();
            Array.Sort(sorted);
            ulong sum = 0;
            foreach (var t in sorted)
            {
                sum += t;
            }
            int last = sorted.Length - 1;
            AssertFigure(report, "mean", sum / (ulong)sorted.Length, freq);
            AssertFigure(report, "p50", sorted[(int)Math.Floor(0.50 * last)], freq);
            AssertFigure(report, "p99", sorted[(int)Math.Floor(0.99 * last)], freq);
            AssertFigure(report, "max", sorted[last], freq);
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
    private static void AssertFigure(string report, string name, ulong ticks, ulong freq)
    {
        ulong us = (ticks * 1_000_000 + freq / 2) / freq;
        string expected = $"{name}={us / 1000}.{us % 1000:D3} ms";
        Assert.Contains(expected, report, StringComparison.Ordinal);
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
