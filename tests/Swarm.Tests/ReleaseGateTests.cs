using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The release workflow's shape, locked where a silent edit would cost the most
/// (issue #181).
///
/// A tag run is the last gate a byte passes before it is attested, and it runs
/// once per release rather than on every pull request, so a step quietly
/// dropped from it is discovered by the release that needed it. Four
/// properties are held here:
///
/// <list type="bullet">
///   <item><b>The release gate is not weaker than the pull-request gate.</b>
///         Every <c>run:</c> block in ci.yml's <c>build</c> job appears
///         verbatim in release.yml. The two files carry the gate twice because
///         factoring it into a reusable workflow would rename the required
///         check the branch ruleset names, and this is the cost of that: a
///         test that reds when one copy moves without the other.</item>
///   <item><b>The cross-check is wired in.</b> release.yml invokes
///         <c>tools/version-truth.ps1</c>. Without this the script is proven by
///         VersionTruthTests and reached by nothing.</item>
///   <item><b>The job that attests restores no cache.</b> zizmor's
///         cache-poisoning audit says the same thing from outside the
///         repository; this says it from inside, so the property survives the
///         audit being narrowed, skipped or unavailable.</item>
///   <item><b>The job holding the write scopes runs none of the ingestion
///         steps.</b> <c>secrets.GITHUB_TOKEN</c> is minted with the
///         permissions of the job it is read in (#193), so a token carrying
///         <c>attestations: write</c> must not be handed to the steps that
///         fetch prettier off npm, restore NuGet packages or execute a
///         repository script. Merging the two jobs back is one edit and looks
///         like a simplification.</item>
/// </list>
///
/// WHAT THIS DOES NOT COVER. It reads workflow text and never a run. Whether
/// the tag trigger fires, whether the attestation is produced, and whether the
/// artifacts carry the bytes are facts about an execution, and the only place
/// they can be established is a tag run - which is #182, deliberately a
/// separate issue because it needs a maintainer-authorized tag.
/// </summary>
public sealed class ReleaseGateTests
{
    private static string WorkflowPath(string name) =>
        Path.Combine(Build.RepoRoot, ".github", "workflows", name);

    [Fact]
    public void TheReleaseWorkflowTriggersOnVersionTagsAndNothingElse()
    {
        var lines = File.ReadAllLines(WorkflowPath("release.yml"));
        var on = Block(lines, "on:");

        Assert.Contains(on, l => l.Trim() == "push:");
        Assert.Contains(on, l => l.Trim() == "tags: [\"v*\"]");

        // A release workflow that also ran on `push: branches` or on a pull
        // request would attest untagged bytes.
        Assert.DoesNotContain(on, l => l.Trim().StartsWith("branches", StringComparison.Ordinal));
        Assert.DoesNotContain(on, l => l.Trim().StartsWith("pull_request", StringComparison.Ordinal));
        Assert.DoesNotContain(on, l => l.Trim().StartsWith("workflow_dispatch", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReleaseGateCarriesEveryStepThePullRequestGateRuns()
    {
        var ciRuns = RunBlocks(File.ReadAllLines(WorkflowPath("ci.yml")));
        var releaseRuns = RunBlocks(File.ReadAllLines(WorkflowPath("release.yml")));

        Assert.NotEmpty(ciRuns);

        var missing = ciRuns.Where(r => !releaseRuns.Contains(r)).ToArray();

        Assert.True(
            missing.Length == 0,
            "release.yml is missing " + missing.Length + " of ci.yml's " + ciRuns.Count +
            " gate commands, so a tag would be published on a weaker gate than a " +
            "pull request passes. Missing:\n  " +
            string.Join("\n  ", missing.Select(m => m.Replace("\n", " / "))));
    }

    [Fact]
    public void TheReleaseWorkflowRunsTheVersionTruthCrossCheck()
    {
        var text = File.ReadAllText(WorkflowPath("release.yml"));

        Assert.Contains("tools/version-truth.ps1", text);
        Assert.True(
            File.Exists(Path.Combine(Build.RepoRoot, "tools", "version-truth.ps1")),
            "release.yml invokes tools/version-truth.ps1 and the script is not in the tree");
    }

    [Fact]
    public void TheReleaseJobRestoresNoDependencyCache()
    {
        var offenders = File.ReadAllLines(WorkflowPath("release.yml"))
            .Select((line, i) => (line, number: i + 1))
            .Where(x => x.line.TrimStart().StartsWith("cache:", StringComparison.Ordinal)
                        || x.line.Contains("actions/cache@", StringComparison.Ordinal))
            .Select(x => x.number + ": " + x.line.Trim())
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "the job that attests what it builds restores a cache a pull request " +
            "could have written into:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoJobHoldingAWriteScopeRunsAnIngestionStep()
    {
        // The three surfaces SECURITY.md names as this repository's widest, by
        // the command that reaches them. Substrings of the `run:` text, so the
        // direction this errs in is treating more steps as ingestion steps,
        // which is the safe one for a rule about who may not hold a grant.
        string[] ingestion = ["npx", "dotnet ", "./tools/", "./build.ps1"];
        string[] writeScopes = ["id-token: write", "attestations: write", "contents: write"];

        var lines = File.ReadAllLines(WorkflowPath("release.yml"));
        var offenders = new List<string>();

        foreach (var (job, start, end) in JobBlocks(lines))
        {
            var body = lines[start..end];
            var scope = writeScopes.FirstOrDefault(s => body.Any(l => l.Trim() == s));
            if (scope is null)
            {
                continue;
            }

            foreach (var run in RunBlocks(body))
            {
                var reached = ingestion.FirstOrDefault(
                    i => run.Contains(i, StringComparison.Ordinal));
                if (reached is not null)
                {
                    offenders.Add($"job '{job}' holds {scope} and runs '{reached}': {run.Replace("\n", " / ")}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a job in release.yml holds a write scope and executes an ingestion step, so its " +
            "GITHUB_TOKEN carries that scope into the widest surface in the tree (#193):\n  " +
            string.Join("\n  ", offenders));
    }

    // Each `  <name>:` block under `jobs:`, as (name, first line, one past the
    // last).
    private static List<(string Name, int Start, int End)> JobBlocks(string[] lines)
    {
        var jobsAt = Array.FindIndex(lines, l => l.StartsWith("jobs:", StringComparison.Ordinal));
        Assert.True(jobsAt >= 0, "the workflow has no 'jobs:' block");

        var starts = new List<(string, int)>();
        for (var i = jobsAt + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#')
            {
                break;
            }
            var trimmed = line.TrimStart();
            if (line.Length - trimmed.Length == 2 && trimmed.EndsWith(":", StringComparison.Ordinal))
            {
                starts.Add((trimmed[..^1], i));
            }
        }

        Assert.NotEmpty(starts);

        return starts
            .Select((s, k) => (s.Item1, s.Item2, k + 1 < starts.Count ? starts[k + 1].Item2 : lines.Length))
            .ToList();
    }

    // The top-level block introduced by `key` at column zero: every following
    // line up to the next column-zero line that is not blank and not a comment.
    private static List<string> Block(string[] lines, string key)
    {
        var start = Array.FindIndex(lines, l => l.StartsWith(key, StringComparison.Ordinal));
        Assert.True(start >= 0, $"no '{key}' block in the workflow");

        var block = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (!char.IsWhiteSpace(line[0]))
            {
                break;
            }
            block.Add(line);
        }

        return block;
    }

    // Every `run:` step body in a workflow, normalised to its own indentation
    // and with trailing whitespace dropped, so the comparison is over commands
    // rather than over how deeply a job happens to be nested.
    private static List<string> RunBlocks(string[] lines)
    {
        var blocks = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            {
                continue;
            }

            var indent = lines[i].Length - trimmed.Length;
            var inline = trimmed[4..].Trim();

            if (inline.Length > 0 && inline != "|" && inline != ">")
            {
                blocks.Add(inline);
                continue;
            }

            var body = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j].TrimEnd();
                if (line.Length == 0)
                {
                    body.Add(string.Empty);
                    continue;
                }
                if (line.Length - line.TrimStart().Length <= indent)
                {
                    break;
                }
                body.Add(line);
            }

            while (body.Count > 0 && body[^1].Length == 0)
            {
                body.RemoveAt(body.Count - 1);
            }
            if (body.Count == 0)
            {
                continue;
            }

            var common = body.Where(l => l.Length > 0)
                .Min(l => l.Length - l.TrimStart().Length);
            blocks.Add(string.Join("\n", body.Select(l => l.Length == 0 ? l : l[common..])));
        }

        return blocks;
    }
}
