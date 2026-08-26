using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the recording taken on issue #297: the mutation job runs the
/// harness itself before it hands the suite to Stryker, and that run is not
/// allowed to pass silently.
///
/// What went wrong. Run 32854516422 aborted on "Initial testrun has more than
/// 50% failing tests" and named one test in the whole job log. The class it
/// named holds two of the suite's tests, so the printed name cannot be what
/// tripped the threshold, and Stryker aborts before it writes a report, so
/// nothing else records which tests failed. The names were unrecoverable from
/// the run and from every re-dispatch of the same shape.
///
/// The rule: a step in <c>mutation.yml</c> runs <c>dotnet test</c> on the
/// harness project ahead of the step that invokes Stryker, and it carries no
/// <c>continue-on-error</c>. Deleting it is one line, it reads like a saved
/// minute on a job that already takes twenty, and nothing else in the tree
/// would notice.
///
/// What this does NOT cover: it reads the workflow text, never a run. Whether
/// the harness passes on a hosted runner is the question the step exists to
/// answer and is not decidable from here.
/// </summary>
public sealed class MutationInitialRunTests
{
    private const string HarnessProject = "tests/Swarm.Tests/Swarm.Tests.csproj";
    private const string Stryker = "dotnet-stryker";

    private static string WorkflowPath =>
        Path.Combine(Build.RepoRoot, ".github", "workflows", "mutation.yml");

    [Fact]
    public void TheMutationWorkflowRunsTheHarnessBeforeItMutates()
    {
        var lines = ReadWorkflow();

        int suite = IndexOfFirstCommand(lines, l =>
            l.Contains("dotnet test", StringComparison.Ordinal)
            && l.Contains(HarnessProject, StringComparison.Ordinal));

        int mutate = IndexOfFirstCommand(lines, l => l.Contains(Stryker, StringComparison.Ordinal));

        Assert.True(
            suite >= 0,
            $"no step in mutation.yml runs `dotnet test {HarnessProject}`. Without it a "
                + "Stryker abort on its initial test run names one test and the rest are "
                + "recoverable from nothing (#297)");
        Assert.True(mutate >= 0, "expected mutation.yml to invoke dotnet-stryker at all");
        Assert.True(
            suite < mutate,
            $"the harness run is at line {suite + 1} and Stryker at line {mutate + 1}: running "
                + "the suite after the mutate step records nothing about the run that aborted");
    }

    [Fact]
    public void TheHarnessRunIsAllowedToFailTheJob()
    {
        var lines = ReadWorkflow();

        int suite = IndexOfFirstCommand(lines, l =>
            l.Contains("dotnet test", StringComparison.Ordinal)
            && l.Contains(HarnessProject, StringComparison.Ordinal));
        Assert.True(suite >= 0, $"no step in mutation.yml runs `dotnet test {HarnessProject}`");

        var swallowed = LinesOfStepEndingAt(lines, suite)
            .Any(l => l.StartsWith("continue-on-error:", StringComparison.Ordinal)
                      && !l.Contains("false", StringComparison.Ordinal));

        Assert.False(
            swallowed,
            "the harness run in mutation.yml carries continue-on-error, so a red suite would "
                + "leave the job free to report a mutation score measured against it");
    }

    private static string[] ReadWorkflow()
    {
        Assert.True(File.Exists(WorkflowPath), $"expected {WorkflowPath} to exist");
        return File.ReadAllLines(WorkflowPath);
    }

    /// <summary>
    /// First line carrying <paramref name="match"/> that is not a comment, so a
    /// step described in prose above does not count as one that runs.
    /// </summary>
    private static int IndexOfFirstCommand(string[] lines, Func<string, bool> match)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith('#') && match(trimmed))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The trimmed lines of the step containing <paramref name="index"/>, walking
    /// back to the <c>- </c> that opens it.
    /// </summary>
    private static List<string> LinesOfStepEndingAt(string[] lines, int index)
    {
        var collected = new List<string>();

        for (int i = index; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            collected.Add(trimmed);

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }
        }

        return collected;
    }
}
