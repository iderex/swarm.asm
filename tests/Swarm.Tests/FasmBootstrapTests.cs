using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The assembler bootstrap's hash gate, proven against a deliberately wrong
/// archive (issue #344).
///
/// <c>tools/get-fasm.ps1</c> is the one place the build takes an executable
/// from outside the tree, and the archive it takes can arrive over plain HTTP
/// or out of a CI cache, so the pinned SHA-256 comparison is the whole of the
/// integrity story. A gate never shown to refuse anything is indistinguishable
/// from no gate, so both legs below run the real script, from a copy whose
/// archive directory the test controls, and assert the verdict, each under
/// both PowerShell hosts that run the script in anger:
///
/// <list type="bullet">
///   <item>an archive of the wrong bytes reds, nothing is unpacked, and the
///         archive is gone so a later local run downloads rather than refusing
///         forever,</item>
///   <item>and the real archive greens, unpacks, and is KEPT - the
///         non-vacuity control, and the contract the CI cache step rests on:
///         what it saves is a verified archive, and what it restores goes
///         through the same comparison.</item>
/// </list>
///
/// There is no tampered-archive leg beside the wrong-bytes one on purpose:
/// the script hashes the file before it looks at anything else, so a flipped
/// byte and random bytes reach the comparison as the same kind of input, and
/// a second leg would add a PowerShell launch to every suite run for no path
/// the first does not take. The four launches this class does make cost
/// between ten and twenty-five seconds of serial test time on the reference
/// machine at rest, most of it host start-up, and have reached forty under
/// load. On the hosted runner they cannot be resolved inside the noise: the
/// harness's own reported duration over the green runs of the pull request
/// that landed this, read when this sentence was written, ranged from 1m07s
/// to 1m24s, the earliest four above the 1m07s to 1m14s of the three runs
/// before it and the later ones inside that band.
///
/// WHAT THIS DOES NOT COVER. Neither leg downloads anything: the archive is
/// placed where the script keeps one, so the network path - https, the http
/// fallback, and the move into the archive directory - is proven by the CI
/// bootstrap step and by nothing here. The control reads the real archive
/// from <c>tools/fasm-archive/</c> after forcing the build, so it never races
/// the bootstrap that creates it; a toolchain bootstrapped before the script
/// kept archives has none, and the control says so rather than passing. On a
/// hosted runner the bootstrap step runs first on a fresh checkout, so there
/// the control refuses to skip.
/// </summary>
public sealed class FasmBootstrapTests : IDisposable
{
    // The version is read out of the script and the name around it mirrors
    // the script's own `fasmw-$Version.zip`: a pin bump that left a literal
    // behind would have the refusal leg plant its archive at a name the
    // script no longer looks for, and the script would go to the network.
    private static readonly string ArchiveName = ArchiveNameFromScript();

    private static string ArchiveNameFromScript()
    {
        var script = File.ReadAllText(Path.Combine(Build.RepoRoot, "tools", "get-fasm.ps1"));
        var m = Regex.Match(script, @"^\$Version\s*=\s*'([^']+)'", RegexOptions.Multiline);
        if (!m.Success)
        {
            throw new InvalidOperationException("tools/get-fasm.ps1 no longer declares $Version = '...', so the archive name cannot be derived");
        }
        return "fasmw-" + m.Groups[1].Value + ".zip";
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "swarm-fasm-bootstrap-" + Guid.NewGuid().ToString("N")[..12]);

    public FasmBootstrapTests()
    {
        // The script derives every path from its own location, so a copy in a
        // fresh `tools/` directory is the real script with a directory the
        // test owns.
        Directory.CreateDirectory(ToolsDir);
        File.Copy(Path.Combine(Build.RepoRoot, "tools", "get-fasm.ps1"), Path.Combine(ToolsDir, "get-fasm.ps1"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    private string ToolsDir => Path.Combine(_root, "tools");
    private string ArchivePath => Path.Combine(ToolsDir, "fasm-archive", ArchiveName);
    private string ExePath => Path.Combine(ToolsDir, "fasm", "FASM.EXE");

    // Both hosts that run this script in anger: build.ps1 (and so Build.cs)
    // starts Windows PowerShell, the workflows start pwsh. A host that cannot
    // be started here is skipped with the reason; on a hosted runner both are
    // present, and a start failure there is refused rather than skipped.
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void AnArchiveOfTheWrongBytesIsRefusedAndNothingIsUnpacked(string host)
    {
        PlaceArchive(Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());

        var (exit, output) = RunBootstrap(host);

        Assert.NotEqual(0, exit);
        Assert.Contains("hash mismatch", output, StringComparison.Ordinal);
        Assert.False(File.Exists(ExePath), "the script unpacked an archive whose hash it refused");
        Assert.False(Directory.Exists(Path.Combine(ToolsDir, "fasm")), "the script created the toolchain directory for a refused archive");
        Assert.False(File.Exists(ArchivePath), "the refused archive was left in place, so every later run would refuse it again instead of downloading");
    }

    // Under both hosts as well: the acceptance path under Windows PowerShell
    // is the one Build.cs takes on a cold clone, and a module the script can
    // no longer resolve shows up there and not in the refusal rows.
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void TheRealArchiveUnpacksAndIsKeptForTheCache(string host)
    {
        PlaceArchive(File.ReadAllBytes(RequireRealArchive()));

        var (exit, output) = RunBootstrap(host);

        Assert.True(exit == 0, "the real archive was refused:\n" + output);
        Assert.Contains("SHA-256 verified", output, StringComparison.Ordinal);
        Assert.True(File.Exists(ExePath), "the toolchain was not unpacked from the verified archive");
        Assert.True(File.Exists(ArchivePath), "the verified archive was deleted, so the CI cache step would have nothing to save");
    }

    private void PlaceArchive(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath)!);
        File.WriteAllBytes(ArchivePath, bytes);
    }

    private static string RequireRealArchive()
    {
        // Forcing the build first means the bootstrap that may create the
        // archive has finished before it is read, whatever order the test
        // collections run in.
        _ = Build.ExePath;

        var real = Path.Combine(Build.RepoRoot, "tools", "fasm-archive", ArchiveName);
        if (File.Exists(real))
        {
            return real;
        }

        var why = "tools/fasm-archive/" + ArchiveName + " is not in the tree; the toolchain was bootstrapped " +
                  "before the script kept its archive. Delete tools/fasm and run ./tools/get-fasm.ps1 once.";
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            // The hosted runner bootstraps on a fresh checkout before the
            // harness runs, so an absent archive there is a broken step, not
            // a stale machine.
            throw new InvalidOperationException("on a hosted runner, " + why);
        }
        Assert.Skip(why);
        return real; // unreachable: Assert.Skip throws
    }

    private (int Exit, string Output) RunBootstrap(string host)
    {
        var psi = new ProcessStartInfo(host)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Path.Combine(ToolsDir, "get-fasm.ps1"));

        Process p;
        try
        {
            p = Process.Start(psi)
                ?? throw new InvalidOperationException("could not start " + host);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                // Both hosts ship on the hosted runner, so a start failure
                // there is a broken image or a blocked host, never a reason
                // to skip the refusal proof.
                throw new InvalidOperationException("on a hosted runner, " + host + " did not start: " + e.Message, e);
            }
            Assert.Skip(host + " could not be started here: " + e.Message);
            throw; // unreachable: Assert.Skip throws
        }
        using var _ = p;

        // Both pipes drained asynchronously, the convention Build.cs states and
        // for the reason it states: a synchronous read on one stream deadlocks
        // against a child blocked writing the other.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            // Build.cs's convention again: a hung host is killed with its
            // children, so it neither holds the temp directory nor outlives
            // the test.
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
            Assert.Fail("get-fasm.ps1 did not exit within 60s");
        }
        return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
