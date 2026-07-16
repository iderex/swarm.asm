using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Structural fitness functions for swarm.exe. These encode the masterplan's
/// hard constraints as executable rules so a PR that violates one fails the
/// build (docs/MASTERPLAN.md, "Hard constraints"). Every constraint that is
/// promised in prose is enforced here in bytes.
/// </summary>
public sealed class ConformanceTests
{
    // Decision 5: the shipped executable's whole dependency surface.
    private static readonly string[] AllowedImports = ["kernel32.dll", "user32.dll", "gdi32.dll"];

    // Substrings that betray a C runtime dependency. Startup is `start:`,
    // never `main`, so none of these may appear.
    private static readonly string[] CrtMarkers = ["msvcrt", "vcruntime", "ucrtbase", "api-ms-win-crt", "libcmt", "mingw"];

    private const int SizeBudgetBytes = 64 * 1024;
    private const uint ExpectedAbiVersion = 1; // must track SWARM_ABI_VERSION in src/kernel/abi.inc

    [Fact]
    public void PeImportWhitelist_OnlyKernelUserGdi()
    {
        var pe = PeImage.Load(Build.ExePath);
        var offenders = pe.ImportedDlls
            .Where(d => !AllowedImports.Contains(d, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"swarm.exe imports outside the allowlist: {string.Join(", ", offenders)}. " +
            $"Imported: {string.Join(", ", pe.ImportedDlls)}");
    }

    [Fact]
    public void NoCrtImports()
    {
        var pe = PeImage.Load(Build.ExePath);
        var crt = pe.ImportedDlls
            .Where(d => CrtMarkers.Any(m => d.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(crt.Length == 0, $"swarm.exe pulls in a C runtime: {string.Join(", ", crt)}");
    }

    [Fact]
    public void ExeSizeBudget_UnderSixtyFourKiB()
    {
        var pe = PeImage.Load(Build.ExePath);
        Assert.True(
            pe.FileSize <= SizeBudgetBytes,
            $"swarm.exe is {pe.FileSize} bytes, over the {SizeBudgetBytes}-byte budget");
    }

    [Fact]
    public void NuGetLockFileCommitted()
    {
        // Locked-mode restore only enforces against a lock file that exists,
        // and the implicit restore silently REGENERATES a missing one before
        // any test runs (empirically verified) — so probing the disk cannot
        // pin this invariant. The git index is the state CI checked out: a
        // PR that drops the lock file fails here even after restore has
        // self-healed the working tree.
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = Build.RepoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("ls-files");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("tests/Swarm.Tests/packages.lock.json");

        using var git = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("could not start git");
        var tracked = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();

        Assert.Equal(0, git.ExitCode);
        Assert.False(
            string.IsNullOrEmpty(tracked),
            "tests/Swarm.Tests/packages.lock.json is not tracked by git - dependency versions would float silently");
    }

    [DllImport("swarm.kernel.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern uint swarm_version();

    [Fact]
    public void AbiVersionMatches()
    {
        // Load the freshly built kernel DLL by its absolute path, then let the
        // DllImport above bind to the already-loaded module by name.
        nint handle;
        try
        {
            handle = NativeLibrary.Load(Build.DllPath);
        }
        catch (DllNotFoundException ex) when (IsPolicyBlock(ex))
        {
            // Device Guard / Smart App Control blocks freshly built unsigned
            // modules on some hosts. CI (which sets SWARM_REQUIRE_NATIVE) has
            // no such policy and must run this; a dev machine may skip it.
            if (Environment.GetEnvironmentVariable("SWARM_REQUIRE_NATIVE") == "1")
            {
                throw;
            }
            Assert.Skip("native DLL load blocked by local execution policy (SWARM_REQUIRE_NATIVE not set)");
            return;
        }

        try
        {
            Assert.Equal(ExpectedAbiVersion, swarm_version());
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static bool IsPolicyBlock(Exception ex) =>
        ex.Message.Contains("0x800711C7", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Device Guard", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("application control", StringComparison.OrdinalIgnoreCase);
}
