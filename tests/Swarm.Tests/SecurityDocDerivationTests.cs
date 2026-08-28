using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for issue #312: SECURITY.md describes sets that move - the workflow
/// files, the manifests, the ingestion paths - and a hand-written count of any
/// of them is a claim nothing compares against the tree.
///
/// The failure this refuses already happened. The intake-ladder section said
/// four workflow files existed while the tree held eight, and three manifests
/// while it held six, and the four it did not name include both scheduled jobs
/// that reach the network and the job that writes a security event. Nothing
/// went red, because a sentence in a document is read by nobody but a reader.
///
/// So the rule is that these sets are handed to the reader as the command that
/// derives them, never as a number. This test refuses the number coming back.
///
/// WHAT THIS DOES NOT CHECK, stated because the test reads stronger than it is.
/// It is a scan for one shape - a cardinal immediately before a counted noun -
/// so a count written any other way ("both manifests", "the pair of workflow
/// files", a total spelled out a sentence away from its noun) walks straight
/// through. It does not read the tree either: a derivation command that is
/// wrong, or that returns something the surrounding prose contradicts, passes
/// here. What it buys is that the specific regression #312 was filed for
/// cannot land silently a second time.
/// </summary>
public sealed class SecurityDocDerivationTests
{
    /// <summary>The nouns whose sets move with the tree. Each is the subject of
    /// a derivation block in the document; a number in front of one of them is
    /// the restated-not-referenced shape #312 is about.</summary>
    private const string CountedNouns =
        "workflow file|manifest|ingestion path|call site|package reference|"
        + "direct package reference|resolved entry|resolved entries";

    /// <summary>Cardinals as English writes them, plus digits. Ordinals are in
    /// here too ("a third nuget block" counts the set just as "three" does).
    /// The boundary on the left keeps "afterthought" out of "eight".</summary>
    private const string Cardinals =
        @"one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|"
        + @"first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|"
        + @"\d+";

    private static readonly Regex Count = new(
        @"\b(?<n>" + Cardinals + @")\s+"
        + @"(?:more\s+|other\s+|further\s+|additional\s+|direct\s+)?"
        + @"(?<noun>" + CountedNouns + @")s?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Fenced blocks are the exemption and they are the whole point: the
    /// repair for a count is to paste the command that derives it, and command
    /// output legitimately contains numbers. Prose is what is judged.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> ProseLines(string doc)
    {
        var lines = doc.Replace("\r\n", "\n").Split('\n');
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                yield return (i + 1, lines[i]);
            }
        }

        Assert.False(inFence, "SECURITY.md has an unclosed code fence");
    }

    private static string SecurityDoc() =>
        File.ReadAllText(Path.Combine(Build.RepoRoot, "SECURITY.md"));

    [Fact]
    public void SecurityDocCountsNoSetThatMoves()
    {
        var offenders = ProseLines(SecurityDoc())
            .SelectMany(l => Count.Matches(l.Text).Select(m => $"SECURITY.md:{l.Line}: \"{m.Value}\""))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "SECURITY.md states a count of a set that moves with the tree. Replace it "
                + "with the command that derives it, in the shape the intake-ladder "
                + "section already uses (issue #312). Offending text:\n  "
                + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The counts were replaced by derivations, so the derivations have to be
    /// there. Without this, deleting every count AND every command would pass
    /// the test above, which is the repair's own failure mode.
    /// </summary>
    [Fact]
    public void SecurityDocHandsTheReaderTheDerivations()
    {
        var doc = SecurityDoc();

        Assert.Contains("git ls-tree --name-only origin/main .github/workflows/", doc, StringComparison.Ordinal);
        Assert.Contains("git ls-tree -r --name-only origin/main | grep -E 'csproj$", doc, StringComparison.Ordinal);
        Assert.Contains("grep -rn 'uses:' .github/workflows/", doc, StringComparison.Ordinal);
        Assert.Contains(@"grep -rn 'secrets\.' .github/workflows/", doc, StringComparison.Ordinal);
    }

    /// <summary>
    /// Done-when 3 of #312: the blast-radius analysis covers every workflow the
    /// derivation returns. This is the half of that bullet a test can hold -
    /// each workflow's file name is named in that subsection - and it is not
    /// the whole bullet, because whether the entry beside the name is a correct
    /// reading of the file is a judgement no scan makes.
    /// </summary>
    [Fact]
    public void EveryWorkflowIsNamedInTheBlastRadiusSection()
    {
        var doc = SecurityDoc().Replace("\r\n", "\n");
        var start = doc.IndexOf("### What each job can reach", StringComparison.Ordinal);
        Assert.True(start >= 0, "SECURITY.md lost the 'What each job can reach' section");
        var end = doc.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, "the blast-radius section has no following section to bound it");
        var section = doc[start..end];

        var dir = Path.Combine(Build.RepoRoot, ".github", "workflows");
        Assert.True(Directory.Exists(dir), "expected .github/workflows to exist");
        var workflows = Directory
            .GetFiles(dir, "*.yml")
            .Concat(Directory.GetFiles(dir, "*.yaml"))
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(workflows); // a moved workflows dir must fail loudly, not pass vacuously

        var missing = workflows
            .Where(w => !section.Contains(w!, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "SECURITY.md's blast-radius analysis does not name every workflow in the tree "
                + "(issue #312). Add an entry, or state in that subsection which workflows it "
                + "does not cover and why. Missing:\n  "
                + string.Join("\n  ", missing));
    }
}
