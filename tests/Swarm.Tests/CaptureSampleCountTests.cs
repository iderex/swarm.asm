using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The capture run's sample count is the run's own, taken from the command
/// line as decision 12's protocol writes it (#330). <c>CaptureReportTests</c>
/// watches the instrument at its shipped length and says nothing about where
/// that length came from; a build that ignored the argument and recorded 3,600
/// samples every time would leave it green.
///
/// Two things are asserted here and they are different. The positive leg runs
/// a real capture at a count no constant in the source carries and reads the
/// file back: that is what says the count reached the LOOP BOUND and the PLANE
/// STRIDE, since a buffer written at one stride and dumped at another produces
/// planes whose four phases do not add up to the window they were taken from.
/// The negative legs are one per refusal branch, and each is refused before
/// the buffer is committed and before a window exists.
///
/// Every leg runs on a desktop of this test's own making, for the reason
/// <c>PresetRefusalDialogTests</c> gives: a capture puts up a real window, and
/// a test that takes the screen from whoever is at the machine is a test that
/// gets switched off. The negative legs watch that desktop for a window too -
/// a refused count writes no box, deliberately, because a capture run is an
/// unattended instrument and a modal box would hang the very run it refuses.
/// </summary>
public sealed class CaptureSampleCountTests
{
    /// <summary>
    /// A count no constant in src/swarm.asm carries, so a build that ignored
    /// the argument cannot land on it by accident. 90 paced frames is about
    /// 1.5 s.
    /// </summary>
    private const int Samples = 90;

    /// <summary>CAP_HEADER_BYTES in src/swarm.asm.</summary>
    private const int HeaderBytes = 40;

    /// <summary>CAP_SERIES in src/swarm.asm.</summary>
    private const int Planes = 5;

    /// <summary>
    /// CAPTURE_FRAMES_MAX in src/swarm.asm: the largest count whose whole
    /// sample block still fits the DWORD length WriteFile takes. The bound is
    /// re-derived here rather than copied as a literal, so the two cannot
    /// drift apart silently.
    /// </summary>
    private const long MaxSamples = 0xFFFFFFFFL / (8 * Planes);

    /// <summary>
    /// EXIT_BAD_ARG in src/swarm.asm: what a refused sample count exits with,
    /// as against the 1 every other failure in the exe uses.
    /// </summary>
    private const int ExitBadArg = 2;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(120);

    private static string AcceptedFixture =>
        Path.Combine(Build.RepoRoot, "tests", "fixtures", "preset", "accepted.txt");

    /// <summary>
    /// The count reaches the loop bound, the buffer's plane stride, the dump's
    /// length, the header and the report - all four from the one argument.
    /// </summary>
    [Fact]
    public void ARunRecordsExactlyTheCountItWasGiven()
    {
        var work = Directory.CreateTempSubdirectory("swarm-samples-");
        try
        {
            using var desktop = HiddenDesktop.Create();
            using var child = desktop.Launch(
                $"\"{Build.ExePath}\" -capture \"{AcceptedFixture}\" {Samples}",
                work.FullName);

            Assert.Equal(0, child.WaitForExit(Patience));

            var dumpPath = Path.Combine(work.FullName, "swarm-frames.bin");
            var reportPath = Path.Combine(work.FullName, "swarm-frames.txt");
            Assert.True(File.Exists(dumpPath), $"no swarm-frames.bin in {work.FullName}");
            Assert.True(File.Exists(reportPath), $"no swarm-frames.txt in {work.FullName}");

            var dump = File.ReadAllBytes(dumpPath);
            Assert.Equal("SWRMFRM3", System.Text.Encoding.ASCII.GetString(dump, 0, 8));
            ulong count = BitConverter.ToUInt64(dump, 16);

            Assert.Equal((ulong)Samples, count);
            Assert.Equal(HeaderBytes + 8L * Samples * Planes, dump.LongLength);

            var planes = new ulong[Planes][];
            for (int p = 0; p < Planes; p++)
            {
                planes[p] = new ulong[Samples];
                long at = HeaderBytes + 8L * Samples * p;
                for (int i = 0; i < Samples; i++)
                {
                    planes[p][i] = BitConverter.ToUInt64(dump, checked((int)(at + 8 * i)));
                }
            }

            // The load-bearing assertion of this file. The four phases are
            // consecutive deltas of five reads and the frame plane is the
            // outermost pair, so the identity is exact - but only if the
            // samples were STORED at the same stride they were READ back at.
            // A capture_frame still striding by the compiled-in 3,600 while
            // the dump is written at 90 puts four of the five planes inside
            // memory this file never received, and the identity is what
            // notices.
            for (int i = 0; i < Samples; i++)
            {
                ulong parts = 0;
                for (int p = 0; p < Planes - 1; p++)
                {
                    parts += planes[p][i];
                }
                Assert.Equal(planes[Planes - 1][i], parts);
            }

            // And the identity above holds trivially over five planes of
            // zeros, which is what a dump of untouched pages would be. Every
            // frame of the shipped scene does real work in every phase.
            for (int p = 0; p < Planes; p++)
            {
                Assert.True(
                    planes[p].Count(v => v > 0) * 2 > Samples,
                    $"plane {p} is zero in most of the {Samples} frames, so the dump is not the run");
            }

            Assert.Contains($"samples={Samples}", File.ReadAllText(reportPath), StringComparison.Ordinal);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The four refusal branches, one case each. A token that is present but
    /// carries no bytes, one that is not a decimal number, one that names zero
    /// samples, and one past the bound the dump's single WriteFile imposes.
    /// Each ends the process with <c>EXIT_BAD_ARG</c>, writes neither artifact,
    /// and puts up no window on the way.
    ///
    /// The exit code is 2 rather than the 1 every other failure in the exe
    /// uses, and that is what makes three of these four legs a proof rather
    /// than a formality. With one code for both, deleting the zero check
    /// leaves <c>VirtualAlloc</c> refusing the zero-byte request that follows
    /// it, and the process exits 1 with no artifacts either way - the leg
    /// stays green over a build that has stopped checking. Measured, not
    /// supposed: that mutation was run under a single exit code and reddened
    /// nothing.
    ///
    /// The empty-token leg is the one that does NOT pin its own instruction,
    /// and it is left saying so. The <c>test edx, edx</c> ahead of the digit
    /// loop is a bounds guard: removing it leaves an empty token refused by
    /// the digit test reading the byte past its end, so this case is covered
    /// by the pair rather than by either alone.
    /// </summary>
    [Theory]
    [InlineData("\"\"", "an empty token names no count")]
    [InlineData("12x", "a token that is not all digits")]
    [InlineData("0", "zero samples is not a measurement")]
    [InlineData("107374183", "one past CAPTURE_FRAMES_MAX")]
    public void ARefusedCountExitsOneAndLeavesNothingBehind(string argument, string why)
    {
        // The bound case is only the bound case while the literal above is the
        // first count over it, so the arithmetic is checked rather than the
        // number trusted.
        if (argument == "107374183")
        {
            Assert.Equal(MaxSamples + 1, long.Parse(argument));
        }

        var work = Directory.CreateTempSubdirectory("swarm-samples-");
        try
        {
            using var desktop = HiddenDesktop.Create();
            using var child = desktop.Launch(
                $"\"{Build.ExePath}\" -capture \"{AcceptedFixture}\" {argument}",
                work.FullName);

            var seen = child.WatchForAnyWindowUntilExit(Patience);

            Assert.True(
                seen.ExitCode == ExitBadArg,
                $"{why}: the exe exited {seen.ExitCode} rather than {ExitBadArg} on '{argument}'");
            Assert.True(seen.Window is null, $"{why}: '{argument}' put up a window before refusing");
            Assert.True(
                Directory.GetFiles(work.FullName).Length == 0,
                $"{why}: '{argument}' left a file behind in {work.FullName}");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The non-vacuity control for the four legs above. The same command line
    /// with a count that IS accepted runs clean and writes both artifacts, so
    /// the 1s there belong to the counts and not to the fixture, the desktop or
    /// the working directory.
    /// </summary>
    [Fact]
    public void TheSameCommandLineWithAnAcceptedCountRunsClean()
    {
        var work = Directory.CreateTempSubdirectory("swarm-samples-");
        try
        {
            using var desktop = HiddenDesktop.Create();
            using var child = desktop.Launch(
                $"\"{Build.ExePath}\" -capture \"{AcceptedFixture}\" 1",
                work.FullName);

            Assert.Equal(0, child.WaitForExit(Patience));
            Assert.True(File.Exists(Path.Combine(work.FullName, "swarm-frames.bin")));
            Assert.True(File.Exists(Path.Combine(work.FullName, "swarm-frames.txt")));
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The default is a stated constant rather than an implied one, and it is
    /// the count <c>CaptureReportTests</c> and every figure in
    /// docs/BENCHMARKS.md were taken at. Read out of the source, so a source
    /// that moved it without moving the test says so.
    /// </summary>
    [Fact]
    public void TheShippedDefaultIsStatedInTheSource()
    {
        var src = File.ReadAllLines(Path.Combine(Build.RepoRoot, "src", "swarm.asm"));
        var line = src.FirstOrDefault(l => l.StartsWith("CAPTURE_FRAMES_DEFAULT", StringComparison.Ordinal));

        Assert.True(line is not null, "src/swarm.asm no longer states CAPTURE_FRAMES_DEFAULT");
        Assert.Equal("3600", line!.Split('=')[1].Split(';')[0].Trim());
    }
}
