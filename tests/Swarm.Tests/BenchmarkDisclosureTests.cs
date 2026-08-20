using System.Text;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// docs/BENCHMARKS.md opens by stating what every published row owes: "every
/// baseline row carries the CPU, the feature path, the particle count, the
/// seed, the commit, and the date". Until this file existed, nothing read that
/// sentence back, and three sections had already dropped a field.
///
/// The field this refuses the loss of is the commit, and it is the one whose
/// absence is hardest to see. A row missing its machine or its seed reads as
/// incomplete. A row carrying a date and no commit reads as complete, and a
/// reader who wants the build resolves it from the nearest commit line above -
/// which, in a document where a section measures a change against its own
/// parent, is a different build than the one that produced the numbers. The
/// wrong answer is the one a careful reader arrives at, which is why it is
/// worth a check rather than a convention.
///
/// The unit is the disclosure bullet rather than the section, because that is
/// where the drift happens: a sub-section that carries its own machine and its
/// own date is making its own disclosure, and it owes its own commit with it.
///
/// WHAT THIS DOES NOT CHECK, stated because the test reads stronger than it is.
/// It cannot tell whether the commit named is the one the numbers were taken
/// at, only that one is named; a wrong sha passes here and is caught by a
/// reader or not at all. It says nothing about the machine, the feature path,
/// the count or the seed, which the same sentence also requires - those are
/// prose in many shapes and are not decidable this way. And an undated row
/// escapes it entirely, because the rule keyed on here is that a row which
/// dates itself names its build.
/// </summary>
public sealed class BenchmarkDisclosureTests
{
    private static string BenchmarksPath => Path.Combine(Build.RepoRoot, "docs", "BENCHMARKS.md");

    /// <summary>
    /// A markdown bullet is one logical line the file wraps over several. Every
    /// disclosure in this document wraps, so a per-line read would report a
    /// commit and its date as two unrelated bullets and pass on both.
    /// </summary>
    private static List<(int Line, string Text)> DisclosureBullets(string[] lines)
    {
        var bullets = new List<(int, string)>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("- ", StringComparison.Ordinal))
                continue;

            var start = i;
            var text = new StringBuilder(lines[i]);

            // Continuations are indented two spaces. A blank line, a heading, a
            // table row and a nested bullet all end the bullet.
            while (i + 1 < lines.Length
                   && lines[i + 1].StartsWith("  ", StringComparison.Ordinal)
                   && lines[i + 1].Trim().Length > 0
                   && !lines[i + 1].TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                text.Append(' ').Append(lines[++i].Trim());
            }

            bullets.Add((start + 1, text.ToString()));
        }

        return bullets;
    }

    [Fact]
    public void EveryDatedBenchmarkRowNamesTheBuildItWasTakenAt()
    {
        var lines = File.ReadAllLines(BenchmarksPath);
        var dated = DisclosureBullets(lines)
            .Where(b => b.Text.Contains("**Date**", StringComparison.Ordinal))
            .ToArray();

        // The anti-vacuity leg. A renamed file, a reflowed document or a change
        // of disclosure shape would leave the set empty, and an empty set
        // satisfies every assertion below while reading as a clean sweep.
        Assert.True(
            dated.Length >= 12,
            $"expected docs/BENCHMARKS.md to carry at least 12 dated disclosure bullets, found " +
            $"{dated.Length}. Either the document lost most of its disclosures, or it stopped " +
            "writing them as `- **...**: ...` bullets and this check now reads nothing.");

        var undisclosed = dated
            .Where(b => !b.Text.Contains("**Commit**", StringComparison.Ordinal)
                        && !b.Text.Contains("**Kernel commit**", StringComparison.Ordinal))
            .Select(b => $"docs/BENCHMARKS.md:{b.Line}")
            .ToArray();

        Assert.True(
            undisclosed.Length == 0,
            $"{undisclosed.Length} disclosure(s) in docs/BENCHMARKS.md date a measurement without " +
            $"naming the build it was taken at: {string.Join(", ", undisclosed)}. The document's own " +
            "opening sentence requires both. A dated row with no commit does not read as incomplete, " +
            "so the next reader resolves the build from the nearest commit line above it, which " +
            "belongs to a different measurement.");
    }

    [Fact]
    public void TheRuleThisEnforcesIsStillWrittenInTheDocument()
    {
        var text = string.Join(' ', File.ReadAllLines(BenchmarksPath).Take(20).Select(l => l.Trim()));

        // If the requirement is ever dropped from the document, this check is
        // enforcing a rule nobody states any more, and that is a worse state
        // than the one it was built to fix.
        Assert.Contains("every baseline row carries", text, StringComparison.Ordinal);
        Assert.Contains("the commit, and the date", text, StringComparison.Ordinal);
    }
}
