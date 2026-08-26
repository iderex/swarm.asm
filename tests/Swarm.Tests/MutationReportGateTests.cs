using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the accounting taken on issue #295: the mutation job's publish
/// step is gated on a step that read whether a report exists, and never on
/// <c>always()</c> alone.
///
/// What went wrong. That step carried <c>if: always()</c> with the reason that
/// "a red run's partial report is the thing a reader needs", together with
/// <c>if-no-files-found: error</c>. Stryker writes its report when a run ENDS,
/// so a run that did not finish mutating leaves the directory empty and the
/// step fails on an absence it cannot avoid. Run 32805302524 was killed by the
/// job bound and run 32854516422 aborted on its initial test run; both left
/// nought artifacts with that step red.
///
/// The rule: in <c>mutation.yml</c>, a step that uploads an artifact carries an
/// <c>if:</c> naming the accounting step's output, and a step invoking
/// <c>tools/mutation-report-gate.ps1</c> exists to produce it. Restoring the
/// bare <c>always()</c> is one line, it reads like a simplification, and
/// nothing else in the tree would notice.
///
/// What this does NOT cover, so it is not credited with more than it does: it
/// reads the workflow text and the script's presence, never a run. Whether the
/// gate's branches fire correctly on a runner is not decidable here - those
/// were fired against fixture directories on the pull request that added them,
/// which is where a claim about them belongs.
/// </summary>
public sealed class MutationReportGateTests
{
    private const string GateScript = "mutation-report-gate.ps1";
    private const string UploadAction = "actions/upload-artifact@";

    private static string WorkflowPath =>
        Path.Combine(Build.RepoRoot, ".github", "workflows", "mutation.yml");

    [Fact]
    public void TheGateScriptTheMutationWorkflowCallsExists()
    {
        var script = Path.Combine(Build.RepoRoot, "tools", GateScript);
        Assert.True(
            File.Exists(script),
            $"expected the mutation job's report accounting script at {script}");
    }

    [Fact]
    public void TheMutationWorkflowInvokesTheGateScript()
    {
        var lines = ReadWorkflow();

        var invocations = lines
            .Select(l => l.TrimStart())
            .Where(l => !l.StartsWith('#'))
            .Count(l => l.Contains(GateScript, StringComparison.Ordinal));

        Assert.True(
            invocations > 0,
            $"no step in mutation.yml invokes tools/{GateScript}; the publish step's "
                + "condition would then reference an output nothing produces");
    }

    [Fact]
    public void EveryUploadInTheMutationWorkflowIsGatedOnTheReportVerdict()
    {
        var lines = ReadWorkflow();

        var uploads = 0;
        var offenders = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#') || !trimmed.Contains(UploadAction, StringComparison.Ordinal))
            {
                continue;
            }

            uploads++;

            // The `if:` belongs to the same step, so it is looked for between
            // this line and the previous step boundary. A step starts at a
            // `- name:` at this file's step indentation.
            var condition = ConditionOfStepEndingAt(lines, i);

            if (condition is null)
            {
                offenders.Add($"line {i + 1}: upload step carries no `if:` at all");
                continue;
            }

            if (!condition.Contains("steps.report.outputs.have", StringComparison.Ordinal))
            {
                offenders.Add($"line {i + 1}: upload gated on `{condition}`, which does not read the report verdict");
            }
        }

        Assert.True(uploads > 0, "expected mutation.yml to upload the report at all");
        Assert.True(
            offenders.Count == 0,
            "an upload in mutation.yml is not gated on the report accounting step. "
                + "A run that did not finish mutating writes no report, so an ungated "
                + "upload fails on an absence it cannot avoid:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
    }

    private static string[] ReadWorkflow()
    {
        Assert.True(File.Exists(WorkflowPath), $"expected {WorkflowPath} to exist");
        return File.ReadAllLines(WorkflowPath);
    }

    /// <summary>
    /// Walks back from <paramref name="index"/> to the step that contains it and
    /// returns that step's <c>if:</c> value, or null when it declares none. A
    /// step begins at a line whose first non-space characters are "- ".
    /// </summary>
    private static string? ConditionOfStepEndingAt(string[] lines, int index)
    {
        string? condition = null;

        for (int i = index; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();

            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith("if:", StringComparison.Ordinal))
            {
                condition = trimmed["if:".Length..].Trim();
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                // The step's own first line. An `if:` above it belongs to a
                // different step, so the walk stops here either way.
                return condition;
            }
        }

        return condition;
    }
}
