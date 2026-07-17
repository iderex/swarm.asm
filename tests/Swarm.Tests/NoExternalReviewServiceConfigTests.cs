using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the maintainer's external-review-services policy (2026-07-17,
/// issue #50): all code review and quality gating happens in-house via the
/// adversarial lens gate (four refute-by-default lenses + simd-reviewer) - the
/// review of record for this project. No external review/quality-service
/// config may re-enter the tracked tree. A regression fails loudly, naming
/// the offending file (and line, for the workflow scan), never silently.
/// </summary>
public sealed class NoExternalReviewServiceConfigTests
{
    // Config files that, by their mere presence at the repo root, opt the
    // project back into an external review/quality service.
    private static readonly string[] BannedRootFiles =
    [
        ".coderabbit.yaml",
        ".coderabbit.yml",
        "sonar-project.properties",
        ".sonarcloud.properties",
    ];

    [Fact]
    public void NoExternalReviewServiceConfigAtRepoRoot()
    {
        var offenders = BannedRootFiles
            .Where(f => File.Exists(Path.Combine(Build.RepoRoot, f)))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "external review/quality-service config has re-entered the repo root: " +
            string.Join(", ", offenders) +
            ". Maintainer decision 2026-07-17 (issue #50): all review and quality " +
            "gating is in-house via the adversarial lens gate - the review of record. " +
            "Remove the file(s) rather than re-enable an external service.");
    }

    // Any substring that, appearing in a workflow file, betrays a step
    // invoking an external review/quality-service action (coderabbit-ai/*,
    // sonarsource/*, a sonarcloud scan, a copilot review-enabling action,
    // etc). Case-insensitive substring, scanned per line so an offender is
    // pinpointed - a new external-service integration must be a conscious,
    // reviewed exception to the policy, never a silent add.
    private static readonly string[] BannedWorkflowSubstrings = ["sonar", "coderabbit", "copilot"];

    [Fact]
    public void NoExternalReviewServiceStepInWorkflows()
    {
        var workflowsDir = Path.Combine(Build.RepoRoot, ".github", "workflows");
        Assert.True(Directory.Exists(workflowsDir), "expected .github/workflows to exist");

        var workflowFiles = Directory.GetFiles(workflowsDir, "*.yml")
            .Concat(Directory.GetFiles(workflowsDir, "*.yaml"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(workflowFiles); // a moved/renamed workflows dir must fail loudly, not pass vacuously

        var offenders = new List<string>();
        foreach (var path in workflowFiles)
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var token in BannedWorkflowSubstrings)
                {
                    if (lines[i].Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add($"{name}:{i + 1}: references '{token}' - {lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a workflow step references an external review/quality service. Maintainer " +
            "decision 2026-07-17 (issue #50): the adversarial lens gate is the review of " +
            "record - no sonar/coderabbit/copilot steps belong in CI:\n  " +
            string.Join("\n  ", offenders));
    }
}
