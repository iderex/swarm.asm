using Xunit;

namespace Swarm.Tests;

/// <summary>
/// What <c>scan_arg_token</c> in <c>src/swarm.asm</c> treats as a preset path,
/// asserted against the assembled exe rather than against the routine's own
/// comment.
///
/// The rule has two halves and only the first was ever written down: a BARE
/// token starting with <c>-</c> is a flag and is skipped, and a QUOTED one is
/// taken, because the scan dispatches on the token's first byte and tests
/// <c>'-'</c> before <c>'"'</c>, so a quoted token never meets the <c>'-'</c>
/// test at all. README.md and the routine's contract note both claimed such a
/// path was unreachable; it is reachable by quoting it, and nothing here
/// noticed because nothing here looked.
///
/// Every leg runs under <c>-smoke</c>, where a refused preset reports through
/// the exit code and puts up no box, and every leg runs on a desktop of this
/// test's own making so a 60-frame smoke window never reaches a screen.
/// </summary>
public sealed class PresetArgumentScanTests
{
    /// <summary>A name no file in the working directory carries.</summary>
    private const string Absent = "-no-such-preset-93f1.txt";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Both legs below read an exit code against the file being absent, and a
    /// file of that name in the working directory would invert one of them
    /// quietly. The child is launched with the repository root as its working
    /// directory, so that is where the premise is checked.
    /// </summary>
    private static void AssertTheNameIsUnused()
    {
        var path = Path.Combine(Build.RepoRoot, Absent);
        Assert.False(File.Exists(path), $"{path} exists, so these legs no longer test what they say");
    }

    /// <summary>
    /// Bare, so the token is a flag: no preset is applied, the built-in one
    /// runs, and the absent file is never opened.
    /// </summary>
    [Fact]
    public void ABareDashTokenIsSkippedAndTheAbsentFileIsNeverOpened()
    {
        AssertTheNameIsUnused();

        using var desktop = HiddenDesktop.Create();
        using var child = desktop.Launch($"\"{Build.ExePath}\" {Absent} -smoke");

        Assert.Equal(0, child.WaitForExit(Patience));
    }

    /// <summary>
    /// The same absent name, quoted, and the exit code moves. Under
    /// <c>-smoke</c> the only route to 1 is <c>preset_apply</c> having been
    /// entered on this token, so the pair with the leg above is what proves the
    /// path was reached rather than merely that something failed.
    /// </summary>
    [Fact]
    public void AQuotedDashTokenIsTakenAsAPresetPath()
    {
        AssertTheNameIsUnused();

        using var desktop = HiddenDesktop.Create();
        using var child = desktop.Launch($"\"{Build.ExePath}\" \"{Absent}\" -smoke");

        Assert.Equal(1, child.WaitForExit(Patience));
    }

    /// <summary>
    /// The non-vacuity control. Without it, a build in which every quoted token
    /// failed for some reason of its own would satisfy the leg above while
    /// observing nothing about the dash. A quoted token naming a preset that
    /// does parse runs clean, so the 1 there belongs to the file and not to the
    /// quote.
    /// </summary>
    [Fact]
    public void AQuotedTokenNamingAReadablePresetRunsClean()
    {
        using var desktop = HiddenDesktop.Create();
        using var child = desktop.Launch($"\"{Build.ExePath}\" \"presets\\headline.txt\" -smoke");

        Assert.Equal(0, child.WaitForExit(Patience));
    }
}
