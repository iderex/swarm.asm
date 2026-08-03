using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the decision taken on issue #189: the steps that run zizmor keep
/// a GitHub API token.
///
/// Five of zizmor 1.26.1's audits are online-only and load only when a token is
/// present - impostor-commit, ref-confusion, known-vulnerable-actions,
/// stale-action-refs and ref-version-mismatch. All five reason about the
/// <c>uses:</c> references this repository pins by commit SHA, which is most of
/// what the workflow-security gate is there to check. Measured on the runner at
/// <c>-v</c>: without a token the registry logs "skipping &lt;audit&gt;: can't
/// run without a GitHub API token" five times, and the run still exits 0 with
/// byte-identical findings. That is the failure this test exists for. The gate
/// goes on passing, so nothing else notices that it now checks less.
///
/// The rule: any step whose <c>run:</c> command invokes zizmor must carry a
/// token in its own <c>env:</c>. Per step and not at job level on purpose - the
/// token belongs to the steps that need it, not to the step that downloads and
/// executes a third-party binary next to them.
/// </summary>
public sealed class ZizmorTokenTests
{
    // The env keys zizmor 1.26.1 reads a token from (`--gh-token [env: GH_TOKEN
    // or GITHUB_TOKEN or ZIZMOR_GITHUB_TOKEN]`). Any of them satisfies the rule;
    // which one is a style choice, having none is the regression.
    private static readonly string[] TokenKeys = ["GH_TOKEN", "GITHUB_TOKEN", "ZIZMOR_GITHUB_TOKEN"];

    // A YAML list item: `- key: value` or `- key:`. Group 1 is the indent up to
    // the dash, which is the column sibling items start at.
    private static readonly Regex ListItem = new(@"^( *)-\s+\S", RegexOptions.Compiled);

    [Fact]
    public void ZizmorStepsCarryAGitHubApiToken()
    {
        var workflowsDir = Path.Combine(Build.RepoRoot, ".github", "workflows");
        Assert.True(Directory.Exists(workflowsDir), "expected .github/workflows to exist");

        var workflowFiles = Directory.GetFiles(workflowsDir, "*.yml")
            .Concat(Directory.GetFiles(workflowsDir, "*.yaml"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(workflowFiles); // a moved/renamed workflows dir must fail loudly, not pass vacuously

        var offenders = new List<string>();
        var invocations = 0;

        foreach (var path in workflowFiles)
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);

            foreach (var (start, end) in StepBlocks(lines))
            {
                var command = RunCommand(lines, start, end);
                if (command is null || !command.Contains("zizmor", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                invocations++;
                if (!HasTokenEnv(lines, start, end))
                {
                    offenders.Add($"{name}:{start + 1}: runs zizmor with no {string.Join(" / ", TokenKeys)} in its env");
                }
            }
        }

        // Without this, deleting the gate itself would leave the test green:
        // no zizmor step, no offender, everything passes. The workflow-security
        // gate runs zizmor twice, once for the SARIF and once to fail the build.
        Assert.True(
            invocations >= 2,
            $"expected the workflow-security gate to invoke zizmor at least twice, found {invocations}. " +
            "Either the gate was removed or this scan no longer recognises it - both make the check below vacuous.");

        Assert.True(
            offenders.Count == 0,
            "a workflow step runs zizmor without a GitHub API token. Five of zizmor's audits are " +
            "online-only (impostor-commit, ref-confusion, known-vulnerable-actions, stale-action-refs, " +
            "ref-version-mismatch) and load only when a token is present: without one the run still " +
            "exits 0 and reports the same findings while silently checking less, which is exactly " +
            "what this repository pins its actions by SHA to prevent (issue #189):\n  " +
            string.Join("\n  ", offenders));
    }

    // Every YAML list item in the file, as a half-open line range. A step is
    // just a list item here, and the ranges of nested lists are harmless: they
    // carry no `run:` and are skipped by the caller.
    private static IEnumerable<(int Start, int End)> StepBlocks(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var m = ListItem.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            int dashColumn = m.Groups[1].Value.Length;
            int end = lines.Length;
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (Skippable(lines[j]))
                {
                    continue;
                }
                // The item ends at the first line that dedents past its dash,
                // or at the next dash in the same column.
                if (Indent(lines[j]) < dashColumn || (Indent(lines[j]) == dashColumn && lines[j].TrimStart().StartsWith('-')))
                {
                    end = j;
                    break;
                }
            }

            yield return (i, end);
        }
    }

    // The step's shell command: the inline value of `run:`, or the block scalar
    // indented under it. Null when the step has no `run:` at all - a `uses:`
    // step runs no command of its own. Comment lines are excluded so that a
    // comment naming zizmor is never mistaken for an invocation of it.
    private static string? RunCommand(string[] lines, int start, int end)
    {
        var run = new Regex(@"^(\s*)(?:-\s+)?run:\s*(.*)$");
        for (int i = start; i < end; i++)
        {
            if (IsComment(lines[i]))
            {
                continue;
            }

            var m = run.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            var inline = m.Groups[2].Value.Trim();
            if (inline.Length > 0 && inline != "|" && inline != ">" && inline != "|-" && inline != ">-")
            {
                return inline;
            }

            // A block scalar: every following line indented past the `run:` key
            // is part of the command.
            int keyIndent = m.Groups[1].Value.Length;
            var body = new List<string>();
            for (int j = i + 1; j < end; j++)
            {
                if (lines[j].Trim().Length == 0)
                {
                    continue;
                }
                if (Indent(lines[j]) <= keyIndent)
                {
                    break;
                }
                body.Add(lines[j]);
            }
            return string.Join("\n", body);
        }
        return null;
    }

    // A token key with a non-empty value anywhere in the step block. `env:` is
    // the only place such a key can sit inside a step, so its exact nesting is
    // not worth re-deriving here.
    private static bool HasTokenEnv(string[] lines, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (IsComment(lines[i]))
            {
                continue;
            }
            var trimmed = lines[i].TrimStart();
            foreach (var key in TokenKeys)
            {
                if (trimmed.StartsWith(key + ":", StringComparison.Ordinal) &&
                    trimmed[(key.Length + 1)..].Trim().Length > 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');

    private static bool Skippable(string line) => line.Trim().Length == 0 || IsComment(line);

    private static int Indent(string line) => line.Length - line.TrimStart().Length;
}
