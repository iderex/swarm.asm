using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the coupling issue #106 creates: the OpenSSF Scorecard badge in
/// README.md renders whatever api.scorecard.dev last received, and the only
/// thing that ever sends it anything is the Scorecard workflow running with
/// <c>publish_results: true</c> on the default branch.
///
/// The failure this refuses is silent in both directions and in neither does
/// anything go red on its own. Turn publishing off, or delete the workflow, and
/// the badge does not disappear - it keeps rendering the last score it was
/// given, or renders "unknown", and a reader takes either for a current
/// statement about the tree. Point the badge at a different project - a rename,
/// a copy-paste from another repository - and it renders a real score belonging
/// to somebody else, which is the worse half: it looks right.
///
/// So the rule is that the badge and the publishing move together. If the badge
/// is in the README, a workflow must publish for it; if the badge names a
/// project, its own link must name the same one.
///
/// WHAT THIS DOES NOT CHECK, stated because the test reads stronger than it is.
/// Nothing here contacts api.scorecard.dev, so a badge whose publishing is
/// configured correctly and whose results the API never received passes this
/// file. The tree is all a test in this harness can read; whether the score
/// arrived is visible only in the workflow run and on the badge itself.
/// </summary>
public sealed class ScorecardBadgeTests
{
    /// <summary>The badge image the README embeds: the API serves it per
    /// project, so the project path is the part that can drift.</summary>
    private static readonly Regex BadgeImage = new(
        @"https://api\.scorecard\.dev/projects/github\.com/(?<project>[^/\s)]+/[^/\s)]+)/badge",
        RegexOptions.Compiled);

    /// <summary>The human-readable viewer the badge links to. Same project,
    /// written a second time in a different shape, which is exactly why the two
    /// can disagree.</summary>
    private static readonly Regex BadgeLink = new(
        @"https://scorecard\.dev/viewer/\?uri=github\.com/(?<project>[^/\s)]+/[^/\s)]+)",
        RegexOptions.Compiled);

    /// <summary>A `publish_results: true` that is a setting rather than a
    /// sentence about one - the workflow's header comment discusses the flag at
    /// length and must not be mistaken for it.</summary>
    private static readonly Regex PublishResultsTrue = new(
        @"^\s*publish_results:\s*true\s*$",
        RegexOptions.Compiled);

    private static string[] WorkflowFiles()
    {
        var dir = Path.Combine(Build.RepoRoot, ".github", "workflows");
        Assert.True(Directory.Exists(dir), "expected .github/workflows to exist");

        var files = Directory.GetFiles(dir, "*.yml")
            .Concat(Directory.GetFiles(dir, "*.yaml"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files); // a moved/renamed workflows dir must fail loudly, not pass vacuously
        return files;
    }

    [Fact]
    public void ScorecardBadgeIsBackedByAPublishingWorkflow()
    {
        var readme = File.ReadAllText(Path.Combine(Build.RepoRoot, "README.md"));
        var badges = BadgeImage.Matches(readme);

        // The anti-vacuity leg. Without it, deleting the badge would leave this
        // test green while the workflow it guards went on running unread, and
        // deleting both would leave it green twice over.
        Assert.True(
            badges.Count == 1,
            $"expected exactly one OpenSSF Scorecard badge in README.md, found {badges.Count}. " +
            "The badge is the reason the workflow publishes; if it was removed on purpose, " +
            "remove the publishing with it and delete this test, because a badge nobody " +
            "reads and a score nobody publishes are two different decisions.");

        var publishers = WorkflowFiles()
            .Where(path =>
            {
                var lines = File.ReadAllLines(path);
                var usesScorecard = lines.Any(l =>
                    !l.TrimStart().StartsWith('#') &&
                    l.Contains("ossf/scorecard-action@", StringComparison.Ordinal));
                var publishes = lines.Any(l => PublishResultsTrue.IsMatch(l));
                return usesScorecard && publishes;
            })
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(
            publishers.Length == 1,
            $"README.md carries the Scorecard badge for '{badges[0].Groups["project"].Value}', but " +
            $"{publishers.Length} workflow(s) run ossf/scorecard-action with publish_results: true. " +
            "api.scorecard.dev serves the badge from what that workflow publishes and from nothing " +
            "else, so with no publisher the badge freezes at its last value or renders 'unknown' " +
            "and goes on looking like a current statement about this repository.");
    }

    [Fact]
    public void ScorecardBadgeAndItsViewerLinkNameOneProject()
    {
        var readme = File.ReadAllText(Path.Combine(Build.RepoRoot, "README.md"));

        var image = BadgeImage.Match(readme);
        var link = BadgeLink.Match(readme);

        Assert.True(image.Success, "expected the Scorecard badge image URL in README.md");
        Assert.True(link.Success, "expected the Scorecard badge to link to its scorecard.dev viewer");

        var imageProject = image.Groups["project"].Value;
        var linkProject = link.Groups["project"].Value;

        Assert.True(
            string.Equals(imageProject, linkProject, StringComparison.Ordinal),
            $"the Scorecard badge renders '{imageProject}' and links to '{linkProject}'. " +
            "Both are written by hand and a badge showing one project's score under another " +
            "project's link is a wrong claim that looks entirely healthy.");
    }
}
