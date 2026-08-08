using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Every file committed under <c>presets/</c> parses clean, found by walking
/// the directory rather than by naming the files.
///
/// A list would leave a newly added preset outside the gate while the suite
/// stayed green, which is the shape #93 and #117 already cost this repository
/// once each. The published presets are what a first run loads and what
/// <c>docs/BENCHMARKS.md</c> quotes its rows against, so a preset that stops
/// parsing after a grammar change is a shipped breakage rather than a stale
/// file.
///
/// A walk has its own vacuity failure, and it is worse than the one it
/// replaces: over an empty or deleted directory a walk passes forever while
/// checking nothing. So the judgement lives in <see cref="Verdict"/>, which
/// complains about a missing directory and about an empty one exactly as
/// loudly as about a malformed file, and every one of those three complaints
/// is executed by a test below rather than argued for here.
/// </summary>
public sealed class PresetWalkTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_parse_preset(byte[] text, uint len, ref SwarmParams p);

    /// <summary>
    /// The one name in <c>presets/</c> that is not a preset. The grammar has
    /// no comment syntax (masterplan decision 10), so the descriptions have to
    /// live in a file beside the scenes. Everything else in the directory is
    /// judged whatever it is called: a preset committed under an unexpected
    /// extension is inside the gate, not outside it.
    /// </summary>
    private const string NotAPreset = "README.md";

    private static string PresetDir => Path.Combine(Build.RepoRoot, "presets");

    /// <summary>
    /// What is wrong with <paramref name="dir"/> as a preset directory, one
    /// sentence per complaint, empty when nothing is. This is the whole guard;
    /// the tests differ only in which directory they hand it.
    /// </summary>
    private static List<string> Verdict(string dir)
    {
        var complaints = new List<string>();

        if (!Directory.Exists(dir))
        {
            complaints.Add($"no preset directory at {dir}");
            return complaints;
        }

        var files = Directory
            .GetFiles(dir)
            .Where(f => !string.Equals(Path.GetFileName(f), NotAPreset, StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            complaints.Add($"no preset files in {dir}: a walk over nothing checks nothing");
            return complaints;
        }

        _ = NativeKernel.Handle;
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var p = default(SwarmParams);
            int rc = swarm_parse_preset(bytes, (uint)bytes.Length, ref p);
            if (rc != 0)
            {
                uint u = unchecked((uint)rc);
                complaints.Add(
                    $"{Path.GetFileName(file)} refused: raw=0x{u:X8} "
                        + $"code={(u >> 20) & 0x7FF} line={u & 0xFFFFF}"
                );
            }
        }

        return complaints;
    }

    /// <summary>
    /// The guard itself, over the committed directory.
    /// </summary>
    [Fact]
    public void EveryCommittedPresetParsesClean()
    {
        Assert.Empty(Verdict(PresetDir));
    }

    /// <summary>
    /// And it is judging more than nothing. Separate from the test above so
    /// that a directory emptied by a bad merge fails on the count rather than
    /// passing on an empty walk.
    /// </summary>
    [Fact]
    public void ThereIsSomethingToWalk()
    {
        Assert.True(Directory.Exists(PresetDir), $"no preset directory at {PresetDir}");

        int presets = Directory
            .GetFiles(PresetDir)
            .Count(f => !string.Equals(Path.GetFileName(f), NotAPreset, StringComparison.Ordinal));

        Assert.True(presets >= 1, $"{PresetDir} holds no preset files");
    }

    /// <summary>
    /// The must-catch leg, and it is the walk that is on trial here rather
    /// than the parser: the malformed file is written into a directory the
    /// test then hands over wholesale, under a name and an extension nothing
    /// in this file mentions, beside a good preset that has to stay silent.
    /// </summary>
    [Fact]
    public void AMalformedFileNobodyNamedIsCaught()
    {
        var dir = NewTempDir();
        try
        {
            // A committed preset, so the good half of the directory is the
            // real thing rather than a fixture that drifted from it.
            var good = Directory
                .GetFiles(PresetDir)
                .First(f =>
                    !string.Equals(Path.GetFileName(f), NotAPreset, StringComparison.Ordinal)
                );
            File.Copy(good, Path.Combine(dir, Path.GetFileName(good)));

            // `n 0` is below the pinned lower bound, on line 2 of the grammar.
            var malformed = File.ReadAllText(good).Replace("n 1048576", "n 0");
            Assert.NotEqual(malformed, File.ReadAllText(good));
            File.WriteAllText(Path.Combine(dir, "throwaway.scene"), malformed, new ASCIIEncoding());

            var complaints = Verdict(dir);

            var complaint = Assert.Single(complaints);
            Assert.StartsWith("throwaway.scene refused:", complaint);
            Assert.Contains("line=2", complaint);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A directory that lost its contents is a failure, not a pass. This is
    /// the vacuity the walk introduces and the reason the count is judged
    /// inside the guard rather than trusted.
    /// </summary>
    [Fact]
    public void AnEmptyDirectoryIsRefused()
    {
        var dir = NewTempDir();
        try
        {
            // Even with the one file that is legitimately not a preset in it.
            File.WriteAllText(Path.Combine(dir, NotAPreset), "# nothing here\n");

            var complaint = Assert.Single(Verdict(dir));
            Assert.Contains("no preset files", complaint);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// And a directory that is gone entirely, which is the failure a walk is
    /// least likely to notice on its own.
    /// </summary>
    [Fact]
    public void AMissingDirectoryIsRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"swarm-presets-absent-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(dir));

        var complaint = Assert.Single(Verdict(dir));
        Assert.StartsWith("no preset directory at", complaint);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"swarm-presets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
