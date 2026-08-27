using System.Diagnostics;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Locks the property that an assembly succeeds while a live process holds a
/// previous output mapped (#305).
///
/// fasm writes its output in place, and Windows refuses a write to an image
/// another process has loaded. This host is such a process: every P/Invoke in
/// the harness routes through <see cref="NativeKernel"/>, which loads
/// <c>build/swarm.kernel.dll</c> and keeps it mapped for the life of the run.
/// So before <c>build.ps1</c> published through a rename, a second assembly
/// against a loaded DLL failed with `error: write failed` and exit 255.
///
/// That failure is not a build problem, it is a harness problem: <c>Build</c>
/// holds the assembly behind a <c>Lazy</c> whose default mode caches the
/// throw, so one refused write fails every later test in the host that needs
/// the assembled binaries - 274 of the 362 cases at the tree the mutation run
/// of 2026-08-25 was taken at, which is exactly the fixed failing core that
/// run published as kill attribution (#304).
///
/// The test asserts the exit code rather than the message, because the repair
/// may legitimately change how the publish is done; what may not change is
/// that a held output stops being a reason to fail.
/// </summary>
public sealed class BuildInUseOutputTests
{
    [Fact]
    public void AssemblingAgainSucceedsWhileThisHostHoldsTheKernelDllMapped()
    {
        // Touching the handle is what maps the DLL into this process; it also
        // takes the honest skip where local policy blocks the load, in which
        // case there is nothing holding the file and nothing to assert.
        Assert.NotEqual(nint.Zero, NativeKernel.Handle);

        var script = Path.Combine(Build.RepoRoot, "build.ps1");
        Assert.True(File.Exists(script), $"expected build.ps1 at {script}");

        var psi = new ProcessStartInfo("powershell")
        {
            WorkingDirectory = Build.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start powershell to assemble the build");

        // Both pipes drained asynchronously: a synchronous read on one of them
        // deadlocks against a child blocked writing the other.
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        Assert.True(
            proc.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds),
            "build.ps1 did not finish within 5 minutes");
        proc.WaitForExit(); // flush the async readers after the bounded wait

        Assert.True(
            proc.ExitCode == 0,
            $"build.ps1 failed (exit {proc.ExitCode}) while this host holds "
                + $"{Path.GetFileName(Build.DllPath)} mapped:\n{stdout}\n{stderr}");

        // The publish must leave a usable pair behind, not just exit clean.
        Assert.True(File.Exists(Build.ExePath), $"expected {Build.ExePath} after the run");
        Assert.True(File.Exists(Build.DllPath), $"expected {Build.DllPath} after the run");
    }
}
