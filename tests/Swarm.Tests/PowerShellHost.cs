using System.Diagnostics;

namespace Swarm.Tests;

/// <summary>
/// The PowerShell host the script-running tests start: <c>pwsh</c> where it is
/// on PATH, else Windows PowerShell. Resolved once per test process, because
/// the probe starts a process and per call it would be most of a test's wall
/// time.
/// </summary>
internal static class PowerShellHost
{
    public static string Exe => Resolved.Value;

    private static readonly Lazy<string> Resolved = new(Find);

    private static string Find()
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate)
                {
                    WorkingDirectory = Build.RepoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("exit 0");
                using var p = Process.Start(psi);
                if (p is null)
                {
                    continue;
                }
                p.WaitForExit(30_000);
                if (p.ExitCode == 0)
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
}
