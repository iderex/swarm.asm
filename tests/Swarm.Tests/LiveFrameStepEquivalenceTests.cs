using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The live frame no longer calls <c>pool_step</c>. It performs that routine's
/// loop body itself - <c>pool_build</c>, <c>pool_fanout</c>, and the
/// <c>AH_FRAME</c> increment - because a single call cannot be timed in halves
/// and the capture instrument owes a build figure and a pass figure separately
/// (#125).
///
/// THE COST OF THAT IS A SECOND STATEMENT OF WHAT A STEP IS, and this is what
/// stands under it. An operation added to <c>pool_step</c>'s loop is an
/// operation the shipped frame silently stops performing: the harness would go
/// on stepping through the seam and agreeing with the oracle, every existing
/// test would stay green, and the executable would run a different simulation
/// from the one the suite checks. Nothing else in the tree reads the two
/// against each other.
///
/// It is a source comparison and it says so. It cannot tell that the two
/// perform the same work, only that they name the same operations in the same
/// order; a change that reordered them identically in both places passes here.
/// What it refuses is the asymmetric change, which is the one that happens.
/// </summary>
public sealed class LiveFrameStepEquivalenceTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(Build.RepoRoot, Path.Combine(parts)));

    /// <summary>
    /// The operations a routine body performs, as the source names them: every
    /// <c>call</c> target, plus the <c>AH_FRAME</c> increment, which is the one
    /// operation of a step that is a store rather than a call.
    /// </summary>
    private static List<string> Operations(string body)
    {
        var ops = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            // Strip the comment before matching, or a routine named in a
            // comment would count as an operation.
            var line = raw.Split(';')[0];

            var call = Regex.Match(line, @"^\s*call\s+([A-Za-z_][A-Za-z0-9_]*)");
            if (call.Success)
            {
                ops.Add(call.Groups[1].Value);
                continue;
            }

            if (Regex.IsMatch(line, @"^\s*inc\s+qword\s*\[[^\]]*\+AH_FRAME\]"))
                ops.Add("AH_FRAME++");
        }

        return ops;
    }

    private static string Between(string source, string start, string end, string what)
    {
        int a = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"could not find the start of {what} ({start})");
        int b = source.IndexOf(end, a, StringComparison.Ordinal);
        Assert.True(b > a, $"could not find the end of {what} ({end})");
        return source[a..b];
    }

    [Fact]
    public void TheLiveFramePerformsExactlyPoolStepsLoopBody()
    {
        // pool_step's loop body: from the loop label to the decrement that
        // closes it, which is the whole of what one step is.
        var stepBody = Between(
            Read("src", "platform", "pool.inc"),
            "pool_step:", "        dec     esi", "pool_step's loop");

        // The frame's own step: from the pause branch to the plot, which is
        // the span the work window's build and pass phases cover.
        var frameBody = Between(
            Read("src", "swarm.asm"), "  .work:", "  .no_step:", "the live frame's step");

        var expected = Operations(stepBody);
        var actual = Operations(frameBody);

        // The anti-vacuity leg. A renamed label or a reflowed routine would
        // leave both lists empty, and two empty lists are equal.
        Assert.True(
            expected.Count >= 3,
            $"read only {expected.Count} operation(s) out of pool_step's loop, so this " +
            "check is comparing nothing against nothing");

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The reason the frame does it itself, stated as a property rather than
    /// as a comment: the capture instrument needs a read between the two
    /// halves, so the frame must not go back to one call.
    /// </summary>
    [Fact]
    public void TheFrameStillTakesAReadBetweenTheBuildAndThePass()
    {
        var frame = Between(
            Read("src", "swarm.asm"), "  .work:", "  .plot:", "the live frame's step");

        Assert.DoesNotContain("call    pool_step", frame, StringComparison.Ordinal);
        Assert.Contains("QueryPerformanceCounter, cap_t1", frame, StringComparison.Ordinal);
    }
}
