using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the reach of the Trojan-Source scan (issue #113).
///
/// The unicode-guard workflow rejects bidirectional and zero-width control
/// characters (CVE-2021-42574) over a pathspec it passes to <c>git grep</c>.
/// A path that is not in that pathspec is not scanned, and nothing about a
/// green check says which paths those were: the job prints the same success
/// message whether it covered five roots or one. That is how <c>tests/</c>
/// came to be missing in the first place, with the C# harness compiling and
/// running unscanned while the guard reported a clean tree.
///
/// The rule: every root in <see cref="RequiredScanRoots"/> appears in the
/// pathspec of a <c>git grep</c> invocation in the guard. Adding a root is
/// free, removing one is refused here, and the comment in the workflow
/// explains the rule rather than enforcing it.
///
/// What this does NOT cover, so a reader does not credit it with more than it
/// does: the character set the guard matches and the flags it matches with.
/// Emptying the pattern or dropping <c>-P</c> leaves this test green, because
/// a scan over the right paths for nothing is still a scan over the right
/// paths. Filed separately.
/// </summary>
public sealed class UnicodeGuardScopeTests
{
    // Each root the guard must scan, with the reason it is in the set. Prose
    // rather than a bare list, because the failure message is the only place a
    // future reader learns why a root they are about to delete was there.
    private static readonly (string Root, string Why)[] RequiredScanRoots =
    [
        ("src", "the engine sources, which assemble into the shipped binary"),
        ("tools", "the toolchain bootstrap, which downloads and unpacks the assembler"),
        ("*.ps1", "every PowerShell script, which Windows PowerShell 5.1 parses as ANSI when unsigned and BOM-less"),
        (".github", "the workflows, which are the gates everything else is trusted through"),
        ("tests", "the C# harness, which compiles and runs and holds the conformance gates (issue #113)"),
    ];

    // The scan command inside the workflow's shell block. Anchored at the start
    // of the line, because the guard's own error message contains both the
    // words `git grep` and a `--`, and an unanchored match would count that
    // echo as an invocation and let the vacuity check below pass on a workflow
    // with no scan left in it. Group 1 is the command's arguments.
    private static readonly Regex ScanLine = new(@"^\s*(?:run:\s*)?git\s+grep\b(.*)$", RegexOptions.Compiled);

    [Fact]
    public void UnicodeGuardScansEveryRequiredRoot()
    {
        var guard = Path.Combine(Build.RepoRoot, ".github", "workflows", "unicode-guard.yml");
        Assert.True(
            File.Exists(guard),
            $"expected the Trojan-Source guard at {guard}. A renamed or deleted workflow must fail here " +
            "rather than let this test pass by finding nothing to check.");

        var pathspec = new List<string>();
        var scans = 0;
        foreach (var line in File.ReadAllLines(guard))
        {
            // The workflow's shell block is full of `#` comment lines, several
            // of which quote the scan command while explaining it. Only a real
            // command line counts.
            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var m = ScanLine.Match(line);
            if (!m.Success)
            {
                continue;
            }

            scans++;
            pathspec.AddRange(Pathspec(m.Groups[1].Value));
        }

        // Without this, deleting the scan would leave the test green: no scan
        // line, no missing root, everything passes.
        Assert.True(
            scans > 0,
            "found no `git grep` invocation in unicode-guard.yml. Either the Trojan-Source scan was " +
            "removed or this test no longer recognises it, and both make the check below vacuous.");

        var missing = RequiredScanRoots
            .Where(r => !pathspec.Contains(r.Root, StringComparer.Ordinal))
            .Select(r => $"{r.Root}: {r.Why}")
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "the Trojan-Source scan no longer covers every root that holds code a machine acts on. " +
            "Bidirectional and zero-width control characters (CVE-2021-42574) make a file render " +
            "differently from how it executes, and an unscanned root gets the same green check as a " +
            "scanned one, so the loss is silent (issue #113). Roots missing from the pathspec in " +
            $".github/workflows/unicode-guard.yml, and why each is in the set:\n  {string.Join("\n  ", missing)}\n" +
            "If the pathspec was deliberately widened rather than narrowed, for instance to a single " +
            "`.`, update RequiredScanRoots in this file to match: this test compares literal tokens " +
            "and does not reason about what a broader pathspec happens to include.");
    }

    // The pathspec of a `git grep` command: the arguments after the `--`
    // separator, with the shell quoting removed and a trailing `|| rc=$?` or
    // `;` dropped. Empty when the command has no `--`, which is a git grep over
    // the whole tree: a widening, and one this test reports as every root
    // missing rather than guessing that a broader pathspec still includes them.
    // `'*.ps1'` and `*.ps1` are the same root, so the comparison above is
    // against the unquoted token.
    private static IEnumerable<string> Pathspec(string arguments)
    {
        var tokens = arguments
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeWhile(t => t != "||" && t != ";")
            .ToArray();

        int separator = Array.IndexOf(tokens, "--");
        return separator < 0
            ? []
            : tokens[(separator + 1)..].Select(Unquote);
    }

    private static string Unquote(string token) =>
        token.Length >= 2 && (token[0] == '"' || token[0] == '\'') && token[^1] == token[0]
            ? token[1..^1]
            : token;
}
