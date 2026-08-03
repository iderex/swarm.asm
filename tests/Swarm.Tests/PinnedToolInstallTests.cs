using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the tool half of the supply-chain invariant (issue #144).
/// SHA-pinning an action pins the action, not the third-party executable that
/// action downloads into the runner: a step can be pinned to an exact commit
/// and still install whatever the tool's latest release is that morning.
/// zizmor audits the first half. Its unpinned-tools audit covers the second
/// half only for the two actions in its hard-coded table (setup-trivy and
/// load-secrets-action), so nothing refuses an unpinned setup-uv. This test
/// does.
///
/// The rule: a step using one of <see cref="ToolInstallerActions"/> must give
/// both a literal <c>version:</c> and a literal <c>checksum:</c>. A version
/// without a checksum still trusts whatever the download mirror serves, and a
/// checksum without a version cannot match a moving target, so neither alone
/// counts.
/// </summary>
public sealed class PinnedToolInstallTests
{
    // Actions that fetch a third-party executable and put it on PATH. Matched
    // as a substring of the `uses:` value, so a fork or a renamed major still
    // matches. Add an entry when a workflow starts installing a tool this way;
    // an entry that no workflow uses is inert, not a failure.
    private static readonly string[] ToolInstallerActions = ["astral-sh/setup-uv"];

    // `- uses: owner/action@sha` or a `uses:` key inside a `- name:` step.
    // Group 1 is the indent, group 2 the optional list dash and its padding
    // (their combined width is the key column), group 3 the action reference.
    private static readonly Regex UsesLine = new(@"^( *)(- +)?uses:\s*(\S+)", RegexOptions.Compiled);

    // A literal scalar: not empty, not an expression, not the word `latest`.
    private static readonly Regex ChecksumValue = new(@"^[0-9a-f]{64}$", RegexOptions.Compiled);

    [Fact]
    public void ToolInstallingActionsPinVersionAndChecksum()
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
                var m = UsesLine.Match(lines[i]);
                if (!m.Success)
                {
                    continue;
                }

                var action = ToolInstallerActions
                    .FirstOrDefault(a => m.Groups[3].Value.Contains(a, StringComparison.OrdinalIgnoreCase));
                if (action is null)
                {
                    continue;
                }

                // The `uses:` key's own column: every other key of the same
                // step sits at that column, and the step ends at the next
                // list item or the first dedent past it.
                int keyColumn = m.Groups[1].Value.Length + m.Groups[2].Value.Length;
                var (start, end) = StepBounds(lines, i, keyColumn);

                var version = ScalarIn(lines, start, end, "version");
                var checksum = ScalarIn(lines, start, end, "checksum");

                if (version is null || version.Length == 0 || version == "latest" || version.Contains("${{", StringComparison.Ordinal))
                {
                    offenders.Add(
                        $"{name}:{i + 1}: {action} installs an unpinned tool - " +
                        (version is null ? "no `version:` given" : $"`version: {version}`"));
                }

                if (checksum is null || !ChecksumValue.IsMatch(checksum))
                {
                    offenders.Add(
                        $"{name}:{i + 1}: {action} has no usable `checksum:` - " +
                        (checksum is null ? "none given" : $"got '{checksum}', expected 64 lowercase hex digits"));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a workflow step installs a third-party tool without pinning it. An action pinned " +
            "to a commit SHA still downloads whatever that tool's latest release is unless the " +
            "step names a version AND the SHA-256 of what it downloads (issue #144). zizmor's " +
            "unpinned-tools audit does not cover these actions, so this test is the only thing " +
            "that refuses it:\n  " +
            string.Join("\n  ", offenders));
    }

    // The half-open line range of the step owning the `uses:` line at
    // <paramref name="usesIndex"/>, whose keys sit at <paramref name="keyColumn"/>.
    private static (int Start, int End) StepBounds(string[] lines, int usesIndex, int keyColumn)
    {
        int start = 0;
        for (int i = usesIndex - 1; i >= 0; i--)
        {
            if (Skippable(lines[i]))
            {
                continue;
            }
            if (Indent(lines[i]) < keyColumn || IsListItemAt(lines[i], keyColumn))
            {
                start = IsListItemAt(lines[i], keyColumn) ? i : i + 1;
                break;
            }
        }

        int end = lines.Length;
        for (int i = usesIndex + 1; i < lines.Length; i++)
        {
            if (Skippable(lines[i]))
            {
                continue;
            }
            if (Indent(lines[i]) < keyColumn || IsListItemAt(lines[i], keyColumn))
            {
                end = i;
                break;
            }
        }

        return (start, end);
    }

    // The value of `key:` anywhere inside the step block, unquoted and with any
    // trailing comment removed. Null when the key is absent.
    private static string? ScalarIn(string[] lines, int start, int end, string key)
    {
        var pattern = new Regex(@"^\s*" + Regex.Escape(key) + @":\s*(.*)$");
        for (int i = start; i < end; i++)
        {
            if (lines[i].TrimStart().StartsWith('#'))
            {
                continue;
            }
            var m = pattern.Match(lines[i]);
            if (m.Success)
            {
                return Unquote(StripComment(m.Groups[1].Value.Trim()));
            }
        }
        return null;
    }

    private static string StripComment(string value)
    {
        // Only an unquoted trailing `#` starts a comment. Values here are
        // quoted scalars or bare tokens, so this is enough.
        if (value.Length > 0 && (value[0] == '"' || value[0] == '\''))
        {
            int close = value.IndexOf(value[0], 1);
            return close < 0 ? value : value[..(close + 1)];
        }
        int hash = value.IndexOf('#');
        return hash < 0 ? value : value[..hash].TrimEnd();
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;

    private static bool Skippable(string line) =>
        line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    // `- key:` whose key sits at column <paramref name="keyColumn"/>, i.e. the
    // start of a sibling step. The padding after the dash is counted rather
    // than assumed to be one space.
    private static bool IsListItemAt(string line, int keyColumn)
    {
        int dash = Indent(line);
        if (dash >= line.Length || line[dash] != '-')
        {
            return false;
        }
        int key = dash + 1;
        while (key < line.Length && line[key] == ' ')
        {
            key++;
        }
        return key == keyColumn;
    }
}
