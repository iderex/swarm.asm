using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The structural half of live per-cell matrix editing (#180): the window
/// procedure counts wheel notches and drag pixels, and does nothing else. It
/// writes no matrix byte, touches no arena and executes no floating-point
/// instruction; the counts become a coefficient in
/// <c>ui_apply_matrix_edits</c>, which the render loop calls between two steps.
///
/// This is asserted over the source rather than over behaviour because that is
/// where it can be asserted at all. A window procedure runs on Windows message
/// delivery, which no test in this harness drives, so an edit landing mid-step
/// is not a state any executable check here could reach - and it is exactly
/// the failure the design exists to prevent. What is checkable is that no code
/// path from a message to a matrix byte exists in the first place.
/// <see cref="MatrixEditReplayTests"/> holds the executable half: that edits
/// committed between steps replay bit-identically.
///
/// The scan is proven by mutation. Each case builds the source text that the
/// defect would produce, pushes it through the same predicate, and requires it
/// to fail - so a scan that had stopped matching anything cannot pass by
/// finding nothing.
/// </summary>
public sealed class MatrixEditBoundaryTests
{
    private static string SourcePath => Path.Combine(Build.RepoRoot, "src", "swarm.asm");

    private static string Source => File.ReadAllText(SourcePath);

    /// <summary>The body of <c>proc WindowProc</c> through its <c>endp</c>,
    /// comments stripped, so a mnemonic named in a comment is not a finding
    /// and a mnemonic hidden behind one is not a miss.</summary>
    private static string WindowProcBody(string source)
    {
        var lines = source.Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("proc WindowProc", StringComparison.Ordinal));
        Assert.True(start >= 0, "proc WindowProc not found - the scan is looking at the wrong file");
        int end = Array.FindIndex(lines, start, l => l.TrimStart().StartsWith("endp", StringComparison.Ordinal));
        Assert.True(end > start, "proc WindowProc has no endp");

        return string.Join('\n', lines[start..(end + 1)].Select(StripComment));
    }

    private static string StripComment(string line)
    {
        int i = line.IndexOf(';');
        return i < 0 ? line : line[..i];
    }

    // Every write the handler must not make. The matrix lives in two places -
    // the params block and the arena's validated copy - so both names are
    // banned, along with the routine that writes them and the whole scalar FP
    // group, since a coefficient cannot be computed without one of these.
    private static readonly string[] Forbidden =
    [
        "ui_apply_matrix_edits",
        "SP_MATRIX",
        "AH_PARAMS",
        "arena",
        "movss", "mulss", "addss", "subss", "minss", "maxss",
        "cvtsi2ss", "cvttss2si", "comiss", "ucomiss",
    ];

    private static IEnumerable<string> Offenders(string body) =>
        Forbidden.Where(t => Regex.IsMatch(body, $@"(?<![A-Za-z0-9_.$@?~#]){Regex.Escape(t)}(?![A-Za-z0-9_.$@?~#])"));

    /// <summary>
    /// The claim: a message cannot move a matrix byte. WindowProc mentions
    /// none of the names through which one could be moved.
    /// </summary>
    [Fact]
    public void WindowProcTouchesNoMatrixByteAndNoFloatingPoint()
    {
        Assert.Empty(Offenders(WindowProcBody(Source)));
    }

    /// <summary>
    /// The must-catch half of the case above. Each mutation is the shortest
    /// version of the defect - one line inserted into the handler - and each
    /// must be refused. A scan silently matching nothing would pass the case
    /// above and fail every one of these.
    /// </summary>
    [Theory]
    [InlineData("        call    ui_apply_matrix_edits")]
    [InlineData("        movss   [sim_params+SP_MATRIX], xmm0")]
    [InlineData("        mov     r11, [arena]")]
    [InlineData("        addss   xmm0, xmm1")]
    public void TheScanRefusesAMatrixWriteInWindowProc(string injected)
    {
        var mutated = Source.Replace(
            "        cmp     edx, WM_MOUSEWHEEL",
            injected + "\n        cmp     edx, WM_MOUSEWHEEL",
            StringComparison.Ordinal);
        Assert.NotEqual(Source, mutated); // the anchor must still exist

        Assert.NotEmpty(Offenders(WindowProcBody(mutated)));
    }

    /// <summary>
    /// The other side of the same property: the edit is applied from exactly
    /// one place, and that place is the render loop's step-boundary chain,
    /// between <c>.render:</c> and the <c>.step:</c> label that follows it. A
    /// second call site anywhere - inside the plot, inside the pace, inside a
    /// helper the handler reaches - would put an edit somewhere other than a
    /// boundary and is refused here.
    /// </summary>
    [Fact]
    public void TheApplyRoutineIsCalledOnceAndOnlyAtTheStepBoundary()
    {
        var lines = Source.Split('\n').Select(StripComment).ToArray();

        var callSites = lines
            .Select((l, i) => (l, i))
            .Where(t => Regex.IsMatch(t.l, @"(?<![A-Za-z0-9_.$@?~#])call\s+ui_apply_matrix_edits(?![A-Za-z0-9_.$@?~#])"))
            .Select(t => t.i)
            .ToArray();

        Assert.Single(callSites);

        int render = Array.FindIndex(lines, l => l.TrimStart().StartsWith(".render:", StringComparison.Ordinal));
        Assert.True(render >= 0, ".render: not found - the render loop was renamed");
        int step = Array.FindIndex(lines, render, l => l.TrimStart().StartsWith(".step:", StringComparison.Ordinal));
        Assert.True(step > render, ".step: not found after .render:");

        Assert.InRange(callSites[0], render, step);
    }

    /// <summary>
    /// The must-catch half of the case above: a second call site is refused
    /// wherever it is put, including at a place that looks harmless.
    /// </summary>
    [Fact]
    public void TheScanRefusesASecondApplyCallSite()
    {
        var mutated = Source.Replace(
            "        call    hud_draw",
            "        call    ui_apply_matrix_edits\n        call    hud_draw",
            StringComparison.Ordinal);
        Assert.NotEqual(Source, mutated);

        var lines = mutated.Split('\n').Select(StripComment).ToArray();
        var callSites = lines.Count(l =>
            Regex.IsMatch(l, @"(?<![A-Za-z0-9_.$@?~#])call\s+ui_apply_matrix_edits(?![A-Za-z0-9_.$@?~#])"));

        Assert.Equal(2, callSites); // what the real case asserts is Single
    }
}
