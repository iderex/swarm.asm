using System.Diagnostics;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The assembler bootstrap's hash gate, proven against deliberately wrong
/// archives (issue #344).
///
/// <c>tools/get-fasm.ps1</c> is the one place the build takes an executable
/// from outside the tree, and the archive it takes can arrive over plain HTTP
/// or out of a CI cache, so the pinned SHA-256 comparison is the whole of the
/// integrity story. A gate never shown to refuse anything is indistinguishable
/// from no gate, so every leg below runs the real script, from a copy whose
/// archive directory the test controls, and asserts the verdict:
///
/// <list type="bullet">
///   <item>an archive of the wrong bytes reds, nothing is unpacked, and the
///         archive is gone so the next run downloads rather than refusing
///         forever,</item>
///   <item>the real archive with one byte flipped reds the same way - the
///         near-miss, because a check that only refuses garbage has not been
///         shown to refuse a tampered archive,</item>
///   <item>and the real archive greens, unpacks, and is KEPT - the
///         non-vacuity control, and the contract the CI cache step rests on:
///         what it saves is a verified archive, and what it restores goes
///         through the same comparison.</item>
/// </list>
///
/// WHAT THIS DOES NOT COVER. No leg here downloads anything: the archive is
/// placed where the script keeps one, so the network path - https, the http
/// fallback, and the move into the archive directory - is proven by the CI
/// bootstrap step and by nothing here. The two legs that need the real archive
/// read it from <c>tools/fasm-archive/</c>; a toolchain bootstrapped before the
/// script kept archives has none, and those legs say so rather than passing.
/// </summary>
public sealed class FasmBootstrapTests : IDisposable
{
    private const string ArchiveName = "fasmw-1.73.35.zip";

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
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    private string ToolsDir => Path.Combine(_root, "tools");
    private string ArchivePath => Path.Combine(ToolsDir, "fasm-archive", ArchiveName);
    private string ExePath => Path.Combine(ToolsDir, "fasm", "FASM.EXE");

    private static string RealArchive => Path.Combine(Build.RepoRoot, "tools", "fasm-archive", ArchiveName);

    [Fact]
    public void AnArchiveOfTheWrongBytesIsRefusedAndNothingIsUnpacked()
    {
        PlaceArchive(Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());

        var (exit, output) = RunBootstrap();

        Assert.NotEqual(0, exit);
        Assert.Contains("hash mismatch", output, StringComparison.Ordinal);
        Assert.False(File.Exists(ExePath), "the script unpacked an archive whose hash it refused");
        Assert.False(Directory.Exists(Path.Combine(ToolsDir, "fasm")), "the script created the toolchain directory for a refused archive");
        Assert.False(File.Exists(ArchivePath), "the refused archive was left in place, so every later run would refuse it again instead of downloading");
    }

    [Fact]
    public void TheRealArchiveWithOneByteFlippedIsRefused()
    {
        var bytes = File.ReadAllBytes(RequireRealArchive());
        bytes[bytes.Length / 2] ^= 0x01;
        PlaceArchive(bytes);

        var (exit, output) = RunBootstrap();

        Assert.NotEqual(0, exit);
        Assert.Contains("hash mismatch", output, StringComparison.Ordinal);
        Assert.False(File.Exists(ExePath), "the script unpacked a tampered archive");
    }

    [Fact]
    public void TheRealArchiveUnpacksAndIsKeptForTheCache()
    {
        PlaceArchive(File.ReadAllBytes(RequireRealArchive()));

        var (exit, output) = RunBootstrap();

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
        if (File.Exists(RealArchive))
        {
            return RealArchive;
        }

        var why = "tools/fasm-archive/" + ArchiveName + " is not in the tree; the toolchain was bootstrapped " +
                  "before the script kept its archive. Delete tools/fasm and run ./tools/get-fasm.ps1 once.";
        if (Environment.GetEnvironmentVariable("SWARM_REQUIRE_NATIVE") == "1")
        {
            throw new InvalidOperationException("SWARM_REQUIRE_NATIVE=1 and " + why);
        }
        Assert.Skip(why);
        return RealArchive; // unreachable: Assert.Skip throws
    }

    private (int Exit, string Output) RunBootstrap()
    {
        var psi = new ProcessStartInfo(PowerShellHost.Exe)
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

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start a PowerShell host");

        // Both pipes drained asynchronously, the convention Build.cs states and
        // for the reason it states: a synchronous read on one stream deadlocks
        // against a child blocked writing the other.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "get-fasm.ps1 did not exit within 60s");
        return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
