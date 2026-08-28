using Xunit;
using System.Diagnostics;

namespace Swarm.Tests;

/// <summary>
/// The DCO sign-off gate, proven against fixture repositories (issue #143).
///
/// <c>CONTRIBUTING.md</c> states that every commit must be signed off. Before
/// <c>tools/check-dco.ps1</c> nothing checked it, and four unsigned commits on
/// PR #140 survived three review rounds. A gate that is never shown to refuse
/// anything is indistinguishable from no gate, so every leg below builds a real
/// repository, runs the real script over it, and asserts the verdict:
///
/// <list type="bullet">
///   <item>an unsigned commit reds,</item>
///   <item>a sign-off whose address is not the author's reds,</item>
///   <item>a correctly signed range greens - the non-vacuity control, without
///         which a script that always failed would satisfy every other leg,</item>
///   <item>and each fail-closed input - a shallow clone, an unresolvable ref,
///         an empty range, a missing repository, an absent git - reds rather
///         than passing.</item>
/// </list>
///
/// The fixture commits are deliberately NOT signed with a key. The subject here
/// is the Signed-off-by trailer, which is a line of the message and has nothing
/// to do with a signature, and CI holds no signing key - so signing the fixtures
/// would add "the key was unavailable" as a way for this test to fail for a
/// reason it is not about.
/// </summary>
public sealed class DcoSignOffTests : IDisposable
{
    private const string AuthorName = "Fixture Author";
    private const string AuthorEmail = "fixture@example.invalid";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "swarm-dco-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // git marks objects read-only; clear it before the recursive delete.
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp fixture is not a test failure. Both exceptions are
            // caught because a read-only or ACL-blocked git object throws the
            // second one, not the first, and a teardown throw would fail a passing
            // test with the wrong cause attached.
        }
    }

    [Fact]
    public void UnsignedCommitIsRefused()
    {
        var repo = NewRepo();
        var baseSha = Commit(repo, "base.txt", "Base commit", signOff: false);
        Commit(repo, "work.txt", "Add a thing", signOff: false);

        var (exit, output) = RunCheck(repo, baseSha, "HEAD");

        Assert.Equal(1, exit);
        Assert.Contains("no Signed-off-by trailer", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SignOffFromAnotherAddressIsRefused()
    {
        var repo = NewRepo();
        var baseSha = Commit(repo, "base.txt", "Base commit", signOff: false);
        Commit(
            repo,
            "work.txt",
            "Add a thing\n\nSigned-off-by: Someone Else <someone.else@example.invalid>",
            signOff: false);

        var (exit, output) = RunCheck(repo, baseSha, "HEAD");

        Assert.Equal(1, exit);
        Assert.Contains("signed off as someone.else@example.invalid", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The non-vacuity control. Without it every other leg here is satisfied by a
    /// script that refuses unconditionally.
    /// </summary>
    [Fact]
    public void CorrectlySignedRangeIsAccepted()
    {
        var repo = NewRepo();
        var baseSha = Commit(repo, "base.txt", "Base commit", signOff: false);
        Commit(repo, "work.txt", "Add a thing", signOff: true);
        Commit(repo, "more.txt", "Add another thing", signOff: true);

        var (exit, output) = RunCheck(repo, baseSha, "HEAD");

        Assert.Equal(0, exit);
        Assert.Contains("2 non-merge commit(s)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exemption needs BOTH halves: the exact address, and GitHub's own
    /// record that the bot opened the pull request. The commit's author email is
    /// a field its author types, so an exemption keyed on it alone belongs to
    /// whoever spells the address.
    ///
    /// Four fixtures, one identical unsigned commit: exempt only when the bot
    /// both authored it and opened the pull request.
    /// </summary>
    [Theory]
    // the real thing
    [InlineData("49699333+dependabot[bot]@users.noreply.github.com", "dependabot[bot]", 0)]
    // the same commit on a pull request a person opened - impersonation
    [InlineData("49699333+dependabot[bot]@users.noreply.github.com", "iderex", 1)]
    // a casing variant, which `-contains` would have matched and `-ceq` does not
    [InlineData("49699333+DEPENDABOT[BOT]@users.noreply.github.com", "dependabot[bot]", 1)]
    // a neighbouring bot address: the entry is one identity, not a pattern
    [InlineData("1+some-other[bot]@users.noreply.github.com", "dependabot[bot]", 1)]
    public void TheBotExemptionNeedsBothTheAddressAndTheOpeningAccount(
        string authorEmail,
        string openedBy,
        int expected)
    {
        var repo = NewRepo();
        var baseSha = Commit(repo, "base.txt", "Base commit", signOff: false);
        Commit(
            repo,
            "bump.txt",
            "Bump a dependency",
            signOff: false,
            authorName: "a bot",
            authorEmail: authorEmail);

        Assert.Equal(expected, RunCheck(repo, baseSha, "HEAD", openedBy).Exit);
    }

    /// <summary>
    /// The bypass that greened an arbitrary unsigned commit: the fields used to
    /// be read as one <c>%an&lt;US&gt;%ae&lt;US&gt;%B</c> string and split on U+001F, on
    /// the claim that no git identity can hold that byte. git stores it verbatim,
    /// so an author name carrying one shifts every field left - the address the
    /// check compares against then comes out of the author's own name, and any
    /// trailer they like matches it.
    /// </summary>
    [Fact]
    public void SeparatorInsideTheAuthorNameCannotForgeAMatch()
    {
        var repo = NewRepo();
        var baseSha = Commit(repo, "base.txt", "Base commit", signOff: false);
        Commit(
            repo,
            "work.txt",
            "Add a thing\n\nSigned-off-by: Someone <victim@example.invalid>",
            signOff: false,
            // The separator itself, written as an escape so this file stays ASCII.
            authorName: "Mallory\u001Fvictim@example.invalid",
            authorEmail: "mallory@example.invalid");

        var (exit, output) = RunCheck(repo, baseSha, "HEAD");

        Assert.Equal(1, exit);
        Assert.Contains("signed off as victim@example.invalid", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line that looks like a trailer is not one. Anchored at column zero, so
    /// an indented example inside the body certifies nothing; and the name may
    /// hold no angle brackets, so a line carrying two addresses cannot have its
    /// second one read as the certifying one while a reader sees the first.
    /// </summary>
    [Theory]
    [InlineData("Docs: show how to sign off\n\nExample:\n\n    Signed-off-by: F <f@example.invalid>")]
    [InlineData("Add a thing\n\nSigned-off-by: Bob <bob@example.invalid> <fixture@example.invalid>")]
    public void ALineThatMerelyLooksLikeATrailerDoesNotCertify(string message)
    {
        var repo = NewRepo();
        var baseSha = Commit(repo, "base.txt", "Base commit", signOff: false);
        Commit(repo, "work.txt", message, signOff: false);

        Assert.Equal(1, RunCheck(repo, baseSha, "HEAD").Exit);
    }

    [Fact]
    public void EmptyRangeIsRefusedRatherThanReadAsClean()
    {
        var repo = NewRepo();
        var head = Commit(repo, "base.txt", "Base commit", signOff: true);

        var (exit, output) = RunCheck(repo, head, head);

        Assert.Equal(1, exit);
        Assert.Contains("no non-merge commits", output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvableRefIsRefused()
    {
        var repo = NewRepo();
        Commit(repo, "base.txt", "Base commit", signOff: true);

        var (exit, output) = RunCheck(repo, "refs/heads/no-such-branch", "HEAD");

        Assert.Equal(1, exit);
        Assert.Contains("DCO check refused", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShallowCloneIsRefused()
    {
        var origin = NewRepo();
        var baseSha = Commit(origin, "base.txt", "Base commit", signOff: true);
        Commit(origin, "work.txt", "Add a thing", signOff: false);

        var shallow = Path.Combine(_root, "shallow-" + Guid.NewGuid().ToString("N")[..8]);
        Git(_root, "clone", "--depth", "1", "--no-local", origin.Replace('\\', '/'), shallow);

        var (exit, output) = RunCheck(shallow, baseSha, "HEAD");

        Assert.Equal(1, exit);
        Assert.Contains("not known to be complete", output, StringComparison.Ordinal);
        Assert.Contains("git answered 'true'", output, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRepositoryIsRefused()
    {
        var absent = Path.Combine(_root, "not-a-repo-" + Guid.NewGuid().ToString("N")[..8]);

        var (exit, output) = RunCheck(absent, "HEAD~1", "HEAD");

        Assert.Equal(1, exit);
        Assert.Contains("does not exist", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal the script is most exposed to, and the one that was measured
    /// rather than reasoned about: calling a command that is not on PATH raises a
    /// NON-terminating error in PowerShell, and <c>pwsh -File</c> on a script that
    /// only produced one exits 0. Without the up-front resolve, a runner with no
    /// git would take this check green while reading nothing at all.
    ///
    /// PATH is emptied for the child, and the host is launched by absolute path so
    /// it still starts.
    /// </summary>
    [Fact]
    public void MissingGitIsRefused()
    {
        var repo = NewRepo();
        var baseSha = Commit(repo, "base.txt", "Base commit", signOff: false);
        Commit(repo, "work.txt", "Add a thing", signOff: false);

        var script = Path.Combine(Build.RepoRoot, "tools", "check-dco.ps1");
        var host = ResolveOnPath(PowerShellExe());

        var (exit, output) = Run(
            host,
            Build.RepoRoot,
            emptyPath: true,
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script,
            "-Base", baseSha, "-Head", "HEAD", "-RepoRoot", repo);

        Assert.Equal(1, exit);
        Assert.Contains("git is not on PATH", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The job's wiring, not just the script it calls. Every leg above would go on
    /// passing with <c>dco.yml</c> deleted, its <c>run:</c> line pointed at
    /// something inert, or its full-history checkout removed - and the script
    /// refuses a shallow clone, so a checkout without <c>fetch-depth: 0</c> would
    /// red every pull request. The other workflow gates in this suite assert their
    /// own wiring for the same reason.
    /// </summary>
    [Fact]
    public void TheWorkflowRunsTheCheckerOverTheWholeHistory()
    {
        var path = Path.Combine(Build.RepoRoot, ".github", "workflows", "dco.yml");
        Assert.True(File.Exists(path), "the DCO workflow is missing at .github/workflows/dco.yml");
        var yaml = File.ReadAllText(path);

        Assert.Contains("-File ./tools/check-dco.ps1", yaml, StringComparison.Ordinal);
        Assert.Contains("fetch-depth: 0", yaml, StringComparison.Ordinal);
        // The three inputs reach the script through the environment, never through
        // `run:` interpolation - which is both the injection-safe form and the one
        // that keeps zizmor quiet.
        foreach (var name in new[] { "BASE_SHA", "HEAD_SHA", "PR_AUTHOR" })
        {
            Assert.Contains($"{name}: ", yaml, StringComparison.Ordinal);
            Assert.Contains($"\"${name}\"", yaml, StringComparison.Ordinal);
        }

        // The job name is the context the ruleset names; renaming it silently
        // would leave a required check that never reports again.
        Assert.Contains("name: dco", yaml, StringComparison.Ordinal);
    }

    // --- fixture plumbing -------------------------------------------------

    private string NewRepo()
    {
        var path = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        Git(path, "init", "--initial-branch=main");
        Git(path, "config", "user.name", AuthorName);
        Git(path, "config", "user.email", AuthorEmail);
        // Local to this throwaway repository. See the class remarks: the subject
        // is the trailer, not the signature, and CI holds no key.
        Git(path, "config", "commit.gpgsign", "false");
        return path;
    }

    private static string Commit(
        string repo,
        string file,
        string message,
        bool signOff,
        string? authorName = null,
        string? authorEmail = null)
    {
        File.WriteAllText(Path.Combine(repo, file), message);
        Git(repo, "add", file);

        var args = new List<string>();
        if (authorName is not null && authorEmail is not null)
        {
            // -c so the sign-off `-s` adds names the fixture author too; --author
            // alone would leave the trailer reading the repository's identity and
            // the test would be measuring the wrong disagreement.
            args.AddRange(["-c", $"user.name={authorName}", "-c", $"user.email={authorEmail}"]);
        }

        args.AddRange(["commit", "-m", message]);
        if (signOff)
        {
            args.Add("-s");
        }

        Git(repo, args.ToArray());

        return Git(repo, "rev-parse", "HEAD").Trim();
    }

    private static (int Exit, string Output) RunCheck(
        string repo,
        string @base,
        string head,
        string openedBy = "a-person")
    {
        var script = Path.Combine(Build.RepoRoot, "tools", "check-dco.ps1");
        Assert.True(File.Exists(script), $"the checker is missing at {script}");

        return Run(
            PowerShellExe(),
            Build.RepoRoot,
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script,
            "-Base", @base, "-Head", head, "-RepoRoot", repo,
            "-PullRequestAuthor", openedBy);
    }

    // pwsh where it exists (what the workflow invokes), and Windows PowerShell
    // otherwise. The script is written to the intersection of the two so either
    // host reaches the same verdict. Resolved once: the probe below starts a
    // process, and per-RunCheck that was most of this class's wall time.
    private static readonly Lazy<string> PowerShellHost = new(FindPowerShellExe);

    private static string PowerShellExe() => PowerShellHost.Value;

    private static string FindPowerShellExe()
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                var probe = Run(candidate, Build.RepoRoot, "-NoProfile", "-Command", "exit 0");
                if (probe.Exit == 0)
                {
                    return candidate;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not on PATH; try the next.
            }
        }

        throw new InvalidOperationException("neither pwsh nor powershell is on PATH");
    }

    private static string Git(string cwd, params string[] args)
    {
        var (exit, output) = Run("git", cwd, args);
        Assert.True(exit == 0, $"git {string.Join(' ', args)} failed in {cwd}:\n{output}");
        return output;
    }

    // The absolute path of an executable on PATH, so a child can be started with
    // PATH emptied and still find its host.
    private static string ResolveOnPath(string exe)
    {
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(separator))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), exe + extension);
                if (candidate.Length > 0 && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException($"{exe} is not on PATH");
    }

    private static (int Exit, string Output) Run(string exe, string cwd, params string[] args) =>
        Run(exe, cwd, emptyPath: false, args);

    private static (int Exit, string Output) Run(
        string exe,
        string cwd,
        bool emptyPath,
        params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (emptyPath)
        {
            psi.Environment["PATH"] = string.Empty;
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");

        // Both pipes are drained asynchronously, the convention Build.cs already
        // states and for the reason it states: a synchronous ReadToEnd on one
        // stream deadlocks against a child blocked writing the other. `git clone`
        // writes progress to stderr while the reader is parked on stdout, and a
        // deadlock here never reaches WaitForExit, so the timeout below cannot
        // fire - the run hangs until CI's job timeout kills it with no failing
        // test name attached.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), $"{exe} did not exit within 60s");
        return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
