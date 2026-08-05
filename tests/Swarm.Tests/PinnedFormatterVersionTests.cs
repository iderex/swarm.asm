using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for issue #191: the Prettier pin is written in more than one place
/// and nothing refused the copies drifting apart.
///
/// <c>.github/workflows/ci.yml</c> runs the docs formatting gate at an exact
/// version, and <c>CONTRIBUTING.md</c> presents its copy as the local
/// equivalent of that gate. There is no npm manifest anywhere in the tree, so
/// Dependabot sees neither copy and no bot reconciles them. Bumping one and
/// forgetting the other leaves a contributor formatting against a different
/// Prettier than the gate runs, which shows up as a red check on a diff they
/// cannot reproduce locally.
///
/// The rule: every tracked file may name a Prettier version, and all of them
/// must name the same one. The value itself is not asserted, so a deliberate
/// joint bump stays a one-line edit in each file and needs no change here.
///
/// This file is tracked and is scanned like any other, so a version literal
/// written into the prose above would become a copy this test then requires to
/// agree. That is the intended behaviour and the reason none is written here.
/// </summary>
public sealed class PinnedFormatterVersionTests
{
    // `prettier@` followed by the version token, up to whitespace or a quote.
    // The pattern's own text in this file does not match it: the character
    // after the `@` here is `(`, which the version class excludes.
    private static readonly Regex PrettierPin =
        new(@"prettier@([A-Za-z0-9][^\s""'`]*)", RegexOptions.Compiled);

    [Fact]
    public void PrettierPinIsIdenticalEverywhereItAppears()
    {
        var hits = new List<string>();
        var versions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var relative in TrackedFiles())
        {
            var full = Path.Combine(Build.RepoRoot, relative);
            if (!File.Exists(full) || LooksBinary(full))
            {
                continue;
            }

            var lines = File.ReadAllLines(full);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in PrettierPin.Matches(lines[i]))
                {
                    var version = m.Groups[1].Value;
                    versions.Add(version);
                    hits.Add($"{relative}:{i + 1}: prettier at {version}");
                }
            }
        }

        // A scanner that finds nothing passes everything. Both copies the issue
        // names are load-bearing, so fewer than two means either the scan
        // stopped reaching them or a copy was deleted; both are worth a
        // deliberate look rather than a silent green.
        Assert.True(
            hits.Count >= 2,
            $"expected the Prettier pin in at least two tracked files (the CI gate and the " +
            $"local command that documents it), found {hits.Count}. If a copy was removed on " +
            $"purpose, this count is the thing to revisit:\n  " + string.Join("\n  ", hits));

        Assert.True(
            versions.Count == 1,
            "the Prettier pin disagrees between the places that carry it. The version in " +
            ".github/workflows/ci.yml is the gate; every other copy documents that gate and " +
            "has to name the same version, or the documented local command stops being the " +
            "one CI runs (issue #191). Nothing else reconciles them: there is no npm manifest " +
            "in the tree, so Dependabot does not see either copy:\n  " +
            string.Join("\n  ", hits));
    }

    // The files git has, rather than the files on disk. Local-only documents
    // sit in this working tree untracked (`.gitignore` carries CLAUDE.md and
    // docs/internal/), and a scan that read them would fail here and pass in
    // CI, or the reverse. The same reasoning as
    // ConformanceTests.PackagesLockFileIsTracked: the index is what CI checked
    // out.
    private static string[] TrackedFiles()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = Build.RepoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("ls-files");

        using var git = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("could not start git");
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        Assert.Equal(0, git.ExitCode);

        var files = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        Assert.NotEmpty(files); // an empty index means the scan covered nothing
        return files;
    }

    // A NUL in the first 8 KiB. Checked on content rather than on an extension
    // list, so a binary the tree does not carry yet (a README capture, say)
    // needs no edit here to be skipped.
    private static bool LooksBinary(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8192];
        int read = stream.Read(head);
        return head[..read].IndexOf((byte)0) >= 0;
    }
}
