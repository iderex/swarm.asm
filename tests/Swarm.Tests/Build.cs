using System.Diagnostics;

namespace Swarm.Tests;

/// <summary>
/// Locates the repo root and the build artifacts, assembling them exactly once
/// per test run - unconditionally, so the tested binaries always match the
/// current sources and are exactly the ones <c>build.ps1</c> produces. The
/// harness never assembles differently from the shipping build.
///
/// The lazy below is at its default <c>ExecutionAndPublication</c>, which
/// CACHES a thrown exception and re-throws it to every later caller, so one
/// failed assembly fails every test in this host that needs the binaries -
/// 274 of 362 at the tree measured on #304. That is kept deliberately: a
/// build that cannot be produced is a permanent condition for this process,
/// and retrying it per test would run <c>build.ps1</c> hundreds of times to
/// reach the same answer. What was repaired instead is the failure that had
/// nothing to do with the sources - an output another live process holds
/// mapped, which <c>build.ps1</c> now publishes through a rename (#305).
/// </summary>
public static class Build
{
    private static readonly Lazy<string> RepoRootLazy = new(FindRepoRoot);
    private static readonly Lazy<string> EnsureLazy = new(Assemble);

    public static string RepoRoot => RepoRootLazy.Value;

    /// <summary>Absolute path to build/swarm.exe, assembled on first access.</summary>
    public static string ExePath => Path.Combine(EnsureLazy.Value, "swarm.exe");

    /// <summary>Absolute path to build/swarm.kernel.dll, assembled on first access.</summary>
    public static string DllPath => Path.Combine(EnsureLazy.Value, "swarm.kernel.dll");

    // Generous because a cold first run downloads the FASM toolchain; a warm
    // assembly takes well under a second.
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    private static string Assemble()
    {
        var buildDir = Path.Combine(RepoRoot, "build");
        var script = Path.Combine(RepoRoot, "build.ps1");

        var psi = new ProcessStartInfo("powershell")
        {
            WorkingDirectory = RepoRoot,
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

        // Both pipes are drained asynchronously: a synchronous ReadToEnd on
        // one stream deadlocks against a child blocked writing the other.
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit((int)BuildTimeout.TotalMilliseconds))
        {
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(); // drain the readers so the message is complete
            throw new InvalidOperationException(
                $"build.ps1 did not finish within {BuildTimeout.TotalMinutes} minutes:\n{stdout}\n{stderr}");
        }
        proc.WaitForExit(); // flush the async readers after the bounded wait

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"build.ps1 failed (exit {proc.ExitCode}):\n{stdout}\n{stderr}");
        }

        return buildDir;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "build.ps1")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("repo root (the directory holding build.ps1) not found above the test binary");
    }
}
