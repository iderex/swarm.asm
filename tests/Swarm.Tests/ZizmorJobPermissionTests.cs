using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the separation taken on issue #193: the job that runs zizmor
/// does not hold <c>security-events: write</c>.
///
/// <c>secrets.GITHUB_TOKEN</c> is minted with the permissions of the job it is
/// read in. While the audit and the SARIF upload lived in one job, that job
/// held the write grant because the upload needed it, and the token handed to
/// the steps that fetch and execute a third-party wheel could therefore write
/// code-scanning results. The two needs are separable: the audit wants
/// read-only API access to resolve action refs, the upload wants the write
/// grant and runs no third-party executable.
///
/// The rule: no job containing a step whose <c>run:</c> invokes zizmor may
/// declare <c>security-events: write</c>. Merging them back is one line, it
/// looks like a simplification, and nothing else in the tree would notice.
///
/// "Invokes zizmor" is a case-insensitive substring of the <c>run:</c> line, so
/// the <c>ZIZMOR_VERSION</c> interpolation counts as well as the command. That
/// is deliberate and was found by trying to build a fixture without it: the
/// direction it errs in is treating more jobs as zizmor jobs, which is the safe
/// one for a rule about who may not hold a grant.
///
/// What this does NOT cover, so it is not credited with more than it does: it
/// reads the workflow text, not the token a runner actually mints, and it says
/// nothing about whether the SARIF still reaches the code-scanning tab or
/// whether the gate still fails the build. Those were verified by execution on
/// the pull request that split the job, which is the only place they can be.
/// </summary>
public sealed class ZizmorJobPermissionTests
{
    private const string WriteGrant = "security-events: write";

    [Fact]
    public void NoZizmorJobHoldsTheSecurityEventsWriteGrant()
    {
        var workflowsDir = Path.Combine(Build.RepoRoot, ".github", "workflows");
        Assert.True(Directory.Exists(workflowsDir), "expected .github/workflows to exist");

        var workflowFiles = Directory.GetFiles(workflowsDir, "*.yml")
            .Concat(Directory.GetFiles(workflowsDir, "*.yaml"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(workflowFiles);

        var offenders = new List<string>();
        var zizmorJobs = 0;

        foreach (var path in workflowFiles)
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);

            foreach (var (jobName, start, end) in JobBlocks(lines))
            {
                var runsZizmor = false;
                var grantLine = -1;

                for (int i = start; i < end; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith('#'))
                    {
                        continue;
                    }

                    // A `run:` whose command mentions zizmor. Only the command,
                    // not the prose around it: the workflow's own header
                    // explains the split and says the word repeatedly.
                    if (trimmed.StartsWith("run:", StringComparison.Ordinal) &&
                        trimmed.Contains("zizmor", StringComparison.OrdinalIgnoreCase))
                    {
                        runsZizmor = true;
                    }

                    if (trimmed.StartsWith(WriteGrant, StringComparison.Ordinal))
                    {
                        grantLine = i + 1;
                    }
                }

                if (!runsZizmor)
                {
                    continue;
                }

                zizmorJobs++;
                if (grantLine > 0)
                {
                    offenders.Add($"{name}: job '{jobName}' runs zizmor and declares {WriteGrant} at line {grantLine}");
                }
            }
        }

        // Without this the check is vacuous: a renamed workflow, a zizmor
        // invocation moved into a composite action, or a parser that stops
        // recognising the job header all leave zero zizmor jobs and zero
        // offenders, which reads exactly like compliance.
        Assert.True(
            zizmorJobs > 0,
            "found no job running zizmor in .github/workflows. Either the workflow-security gate was " +
            "removed or this test no longer recognises it, and both make the check below vacuous.");

        Assert.True(
            offenders.Count == 0,
            "a job that executes zizmor also holds the code-scanning write grant. secrets.GITHUB_TOKEN " +
            "is minted with the permissions of the job it is read in, so that grant reaches the steps " +
            "that download and run a third-party wheel - the widest ingestion surface in the workflow " +
            "and the one with the least need for it (issue #193). Keep the audit job on contents: read " +
            "and let a separate job hold security-events: write for the upload:\n  " +
            string.Join("\n  ", offenders));
    }

    // Every `jobs:` entry, as (name, first line index, exclusive end). A job
    // header is the first indent level under `jobs:`; the block runs until the
    // next line at that indent or shallower.
    private static IEnumerable<(string Name, int Start, int End)> JobBlocks(string[] lines)
    {
        int jobsAt = Array.FindIndex(lines, l => l.StartsWith("jobs:", StringComparison.Ordinal));
        if (jobsAt < 0)
        {
            yield break;
        }

        int jobIndent = -1;
        for (int i = jobsAt + 1; i < lines.Length; i++)
        {
            if (IsBlankOrComment(lines[i]))
            {
                continue;
            }

            int indent = Indent(lines[i]);
            if (indent == 0)
            {
                break; // back to a top-level key: the jobs block ended
            }

            if (jobIndent < 0)
            {
                jobIndent = indent;
            }

            if (indent != jobIndent || !lines[i].TrimEnd().EndsWith(':'))
            {
                continue;
            }

            var name = lines[i].Trim().TrimEnd(':');
            int end = i + 1;
            while (end < lines.Length &&
                   (IsBlankOrComment(lines[end]) || Indent(lines[end]) > jobIndent))
            {
                end++;
            }

            yield return (name, i, end);
        }
    }

    private static bool IsBlankOrComment(string line) =>
        line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    private static int Indent(string line) => line.Length - line.TrimStart().Length;
}
