using System.Diagnostics;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The version-truth cross-check, proven against deliberately mismatched
/// fixtures (issue #181).
///
/// <c>docs/RELEASE-POLICY.md</c> makes a version tag the whole publish step, so
/// a pipeline that would happily attest a binary whose version disagrees with
/// its tag is the defect <c>tools/version-truth.ps1</c> exists to prevent. A
/// gate that is never shown to refuse anything is indistinguishable from no
/// gate, so every leg below runs the real script and asserts the verdict:
///
/// <list type="bullet">
///   <item>an agreeing tag greens - the non-vacuity control, without which a
///         script that always failed would satisfy every other leg,</item>
///   <item>a tag that is not three-part <c>vX.Y.Z</c> reds,</item>
///   <item>a tag with no changelog section, with two of them, or with one
///         carrying no date reds,</item>
///   <item>and a tag whose major disagrees with the ABI version the artifact
///         reports reds ON ITS OWN, with the changelog agreeing, which is the
///         only leg arrangement that proves that comparison rather than
///         letting the changelog leg cover for it.</item>
/// </list>
///
/// WHAT THIS DOES NOT COVER, so the file is not credited with more than it
/// does. The ABI version is an argument here, exactly as it is an argument in
/// <c>release.yml</c>; nothing below executes the built DLL, so the step that
/// reads <c>swarm_version</c> out of the artifact is proven by the release run
/// and by nothing here. And the script's own disclosure holds: <c>Y</c> and
/// <c>Z</c> are read against the changelog alone, because the image carries no
/// product version to read them against.
/// </summary>
public sealed class VersionTruthTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "swarm-version-truth-" + Guid.NewGuid().ToString("N")[..12]);

    public VersionTruthTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    private static string ScriptPath =>
        Path.Combine(Build.RepoRoot, "tools", "version-truth.ps1");

    private string WriteChangelog(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, body);
        return path;
    }

    private static string Released(string version, string date = "2026-08-30") =>
        "# Changelog\n\n## Unreleased\n\n## " + version + " - " + date +
        "\n\n### Added\n\n- the entry the policy's step 3 moved here.\n";

    [Fact]
    public void AnAgreeingTagPasses()
    {
        var changelog = WriteChangelog("agree.md", Released("1.0.0"));
        var (exit, output) = RunCheck("v1.0.0", changelog, 1);

        Assert.True(exit == 0, $"an agreeing tag was refused:\n{output}");
        Assert.Contains("version truth: v1.0.0 agrees", output);
    }

    [Fact]
    public void TheGreenLegIsNotPinnedToVersionOne()
    {
        // The control above uses the only version the tree can ship today, so
        // on its own it cannot tell a working comparison from three constants.
        var changelog = WriteChangelog("agree-two.md", Released("2.1.3"));
        var (exit, output) = RunCheck("v2.1.3", changelog, 2);

        Assert.True(exit == 0, $"an agreeing 2.1.3 tag was refused:\n{output}");
    }

    [Theory]
    [InlineData("1.0.0")] // no leading v
    [InlineData("v1.0")] // two parts
    [InlineData("v1.0.0.0")] // four parts
    [InlineData("v1.0.0-rc1")] // pre-release suffix
    [InlineData("v01.0.0")] // leading zero: two tags for one version
    [InlineData("release-1.0.0")]
    public void ATagThatIsNotThreePartIsRefused(string tag)
    {
        var changelog = WriteChangelog("shape.md", Released("1.0.0"));
        var (exit, output) = RunCheck(tag, changelog, 1);

        Assert.True(exit == 1, $"'{tag}' was accepted as a version tag:\n{output}");
        Assert.Contains("REFUSED (tag shape)", output);
    }

    [Fact]
    public void ATagWithNoChangelogSectionIsRefused()
    {
        // The policy's step 3 moves the entries under the new heading before the
        // tag is pushed. This is what a forgotten step 3 looks like.
        var changelog = WriteChangelog("unreleased-only.md",
            "# Changelog\n\n## Unreleased\n\n### Added\n\n- not moved yet.\n");
        var (exit, output) = RunCheck("v1.0.0", changelog, 1);

        Assert.True(exit == 1, $"a tag with no changelog section was accepted:\n{output}");
        Assert.Contains("REFUSED (changelog)", output);
    }

    [Fact]
    public void TwoSectionsForOneVersionAreRefused()
    {
        var changelog = WriteChangelog("twice.md",
            Released("1.0.0") + "\n## 1.0.0 - 2026-08-29\n\n- and again.\n");
        var (exit, output) = RunCheck("v1.0.0", changelog, 1);

        Assert.True(exit == 1, $"a changelog with two 1.0.0 sections was accepted:\n{output}");
        Assert.Contains("REFUSED (changelog)", output);
    }

    [Fact]
    public void AnUndatedSectionIsRefused()
    {
        var changelog = WriteChangelog("undated.md",
            "# Changelog\n\n## 1.0.0\n\n### Added\n\n- dated by nothing.\n");
        var (exit, output) = RunCheck("v1.0.0", changelog, 1);

        Assert.True(exit == 1, $"an undated section was accepted:\n{output}");
        Assert.Contains("REFUSED (changelog)", output);
    }

    [Fact]
    public void ASectionDatedBySomethingThatIsNotACalendarDateIsRefused()
    {
        var changelog = WriteChangelog("not-a-date.md", Released("1.0.0", "2026-13-40"));
        var (exit, output) = RunCheck("v1.0.0", changelog, 1);

        Assert.True(exit == 1, $"'2026-13-40' was accepted as a date:\n{output}");
        Assert.Contains("REFUSED (changelog)", output);
    }

    [Theory]
    [InlineData("1.0.10")] // the one-character mistake: a prefix match would pass
    [InlineData("1.0.01")]
    [InlineData("11.0.0")]
    public void ASectionThatMerelyStartsWithTheVersionIsNotThatSection(string heading)
    {
        var changelog = WriteChangelog("prefix.md", Released(heading));
        var (exit, output) = RunCheck("v1.0.0", changelog, 1);

        Assert.True(exit == 1, $"'## {heading}' was read as the 1.0.0 section:\n{output}");
        Assert.Contains("REFUSED (changelog)", output);
    }

    [Fact]
    public void AMissingChangelogIsRefusedRatherThanSkipped()
    {
        var absent = Path.Combine(_root, "no-such-changelog.md");
        var (exit, output) = RunCheck("v1.0.0", absent, 1);

        Assert.True(exit == 1, $"a missing changelog passed:\n{output}");
        Assert.Contains("REFUSED (changelog)", output);
    }

    [Fact]
    public void ATagMajorDisagreeingWithTheArtifactIsRefusedOnItsOwn()
    {
        // The changelog agrees, so this is the binary leg alone. With a
        // mismatched changelog too, a script that never compared the ABI
        // version at all would still red here and the leg would be unproven.
        var changelog = WriteChangelog("major.md", Released("2.0.0"));
        var (exit, output) = RunCheck("v2.0.0", changelog, 1);

        Assert.True(exit == 1, $"v2.0.0 was accepted against an ABI version of 1:\n{output}");
        Assert.Contains("REFUSED (binary version)", output);
        Assert.DoesNotContain("REFUSED (changelog)", output);
        Assert.Contains("SWARM_ABI_VERSION = 1", output);
    }

    [Fact]
    public void ATagBehindTheArtifactIsRefusedInTheSameDirection()
    {
        // The other direction of the same comparison: a breaking ABI bump that
        // shipped under a tag that did not move.
        var changelog = WriteChangelog("behind.md", Released("1.4.0"));
        var (exit, output) = RunCheck("v1.4.0", changelog, 2);

        Assert.True(exit == 1, $"v1.4.0 was accepted against an ABI version of 2:\n{output}");
        Assert.Contains("REFUSED (binary version)", output);
    }

    [Fact]
    public void EveryDisagreementIsReportedRatherThanTheFirst()
    {
        var changelog = WriteChangelog("both.md", Released("1.0.0"));
        var (exit, output) = RunCheck("v3.0.0", changelog, 1);

        Assert.True(exit == 1, $"v3.0.0 was accepted:\n{output}");
        Assert.Contains("REFUSED (changelog)", output);
        Assert.Contains("REFUSED (binary version)", output);
        Assert.Contains("2 disagreement(s)", output);
    }

    private static (int Exit, string Output) RunCheck(string tag, string changelogPath, int abiVersion)
    {
        var psi = new ProcessStartInfo(PowerShellHost.Value)
        {
            WorkingDirectory = Build.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add("-Tag");
        psi.ArgumentList.Add(tag);
        psi.ArgumentList.Add("-ChangelogPath");
        psi.ArgumentList.Add(changelogPath);
        psi.ArgumentList.Add("-AbiVersion");
        psi.ArgumentList.Add(abiVersion.ToString());

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start a PowerShell host");

        // Both pipes drained asynchronously, the convention Build.cs states and
        // for the reason it states: a synchronous read on one stream deadlocks
        // against a child blocked writing the other.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "version-truth.ps1 did not exit within 60s");
        return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    // Resolved once: the probe starts a process, and per-RunCheck that would be
    // most of this class's wall time.
    private static readonly Lazy<string> PowerShellHost = new(FindPowerShellExe);

    private static string FindPowerShellExe()
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate)
                {
                    WorkingDirectory = Build.RepoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("exit 0");
                using var p = Process.Start(psi);
                if (p is null)
                {
                    continue;
                }
                p.WaitForExit(30_000);
                if (p.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not on PATH; try the next.
            }
        }

        throw new InvalidOperationException("neither pwsh nor powershell is on PATH");
    }
}
