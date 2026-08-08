using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The attended half of the preset refusal contract (#126): a refused preset
/// puts the error code and the 1-based line in front of a person, and only
/// then exits 1.
///
/// Every other refusal test in this suite runs the exe under <c>-smoke</c>,
/// where <c>preset_fail</c> deliberately skips the box so an unattended run
/// cannot hang on a dialog nobody can dismiss. That leaves the
/// <c>MessageBoxA</c> call itself unexecuted by anything, and the one thing it
/// is for - the text a person reads - asserted nowhere.
///
/// The box is observed on a desktop of this test's own making rather than on
/// the interactive one. <c>MB_SETFOREGROUND</c> means showing it on the
/// interactive desktop would steal focus from whoever is at the machine, and a
/// test that interrupts a person is a test that gets switched off. A desktop
/// created with <c>CreateDesktopW</c> lives in the same window station, needs
/// no elevation and no policy change, and nothing drawn on it reaches a
/// screen; <c>EnumDesktopWindows</c> then reads it. The dialog is dismissed
/// with <c>WM_CLOSE</c> so the exit code is the shipped path's own rather than
/// a kill.
/// </summary>
public sealed class PresetRefusalDialogTests
{
    /// <summary>src/kernel/abi.inc PERR_RANGE, the code rejected.txt trips.</summary>
    private const uint PerrRange = 8;

    /// <summary>The 1-based line rejected.txt trips it on (PresetFixtureTests).</summary>
    private const uint RefusedLine = 9;

    /// <summary>swarm.asm packs bit 31, PERR_* in 20..30, the line in 0..19.</summary>
    private static int PackedReturn => unchecked((int)(0x80000000u | (PerrRange << 20) | RefusedLine));

    private static string RejectedFixture =>
        Path.Combine(Build.RepoRoot, "tests", "fixtures", "preset", "rejected.txt");

    [Fact]
    public void ARefusedPresetShowsTheCodeAndLineInABoxAndThenExitsOne()
    {
        using var desktop = HiddenDesktop.Create();

        using var child = desktop.Launch($"\"{Build.ExePath}\" \"{RejectedFixture}\"");
        var dialog = child.WaitForFirstWindow(TimeSpan.FromSeconds(60));

        Assert.Equal("#32770", Win32.ClassNameOf(dialog));
        Assert.Equal("swarm.asm", Win32.CaptionOf(dialog));

        // The static control carries the sentence; GetWindowText does not read
        // a control in another process, so the text is asked for by message.
        var text = Win32.TextOfEveryChild(dialog);

        Assert.Contains(RejectedFixture, text, StringComparison.Ordinal);
        Assert.Contains(
            $"error {PerrRange} on line {RefusedLine} (the parser returned {PackedReturn})",
            text,
            StringComparison.Ordinal);
        Assert.Contains("Nothing was applied and nothing was drawn.", text, StringComparison.Ordinal);

        Assert.True(
            Win32.PostMessageW(dialog, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero),
            $"WM_CLOSE could not be posted to the dialog: {Marshal.GetLastWin32Error()}");

        Assert.Equal(1, child.WaitForExit(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// The non-vacuity leg, and the one that makes the leg above mean
    /// something. The same fixture through the same detector on the same
    /// desktop, with <c>-smoke</c> added: no window at all, and still exit 1.
    ///
    /// Without it, a detector that reported a window for any reason - a stray
    /// window on the desktop, a bug that matched the wrong process - would
    /// leave the first test green while observing nothing about the box. With
    /// it, "a window appeared" and "no window appeared" are both produced by
    /// this code against the same binary, and only the mode differs.
    ///
    /// What it does not prove: the absence is watched until the process exits
    /// and a little beyond, not forever, so it refuses a box that is shown
    /// during the run rather than one that could be shown after it. The
    /// process ends inside the window either way, which is what makes that
    /// bound the whole life of the run.
    /// </summary>
    [Fact]
    public void TheSameRefusalUnderSmokeShowsNoWindowAndStillExitsOne()
    {
        using var desktop = HiddenDesktop.Create();

        using var child = desktop.Launch($"\"{Build.ExePath}\" -smoke \"{RejectedFixture}\"");
        var seen = child.WatchForAnyWindowUntilExit(TimeSpan.FromSeconds(60));

        Assert.Null(seen.Window);
        Assert.Equal(1, seen.ExitCode);
    }
}

/// <summary>
/// A desktop in this window station that no screen shows, plus the process
/// launches that land on it. <c>ProcessStartInfo</c> cannot name a desktop, so
/// the launch is <c>CreateProcessW</c> with <c>STARTUPINFOW.lpDesktop</c> set.
/// </summary>
internal sealed class HiddenDesktop : IDisposable
{
    private IntPtr handle;
    private readonly string qualifiedName;

    private HiddenDesktop(IntPtr handle, string qualifiedName)
    {
        this.handle = handle;
        this.qualifiedName = qualifiedName;
    }

    public static HiddenDesktop Create()
    {
        // Unique per run: two desktops cannot share a name, and a leftover from
        // a killed run would otherwise be reused with its windows still on it.
        var name = "swarm-preset-" + Guid.NewGuid().ToString("N");
        var h = Win32.CreateDesktopW(name, null, IntPtr.Zero, 0, Win32.GENERIC_ALL, IntPtr.Zero);
        if (h == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (Environment.GetEnvironmentVariable("SWARM_REQUIRE_NATIVE") == "1")
            {
                throw new InvalidOperationException(
                    $"CreateDesktopW failed with {err}; the refusal dialog cannot be observed off the interactive desktop");
            }
            Assert.Skip(
                $"no private desktop available on this host (CreateDesktopW failed with {err}); "
                    + "the refusal dialog is not observed here (SWARM_REQUIRE_NATIVE not set)");
        }

        return new HiddenDesktop(h, @"WinSta0\" + name);
    }

    public ChildProcess Launch(string commandLine)
    {
        var si = new Win32.STARTUPINFOW
        {
            cb = Marshal.SizeOf<Win32.STARTUPINFOW>(),
            lpDesktop = this.qualifiedName,
        };

        // CreateProcessW may write into the command-line buffer, so it gets a
        // mutable copy with room to spare rather than the interned literal.
        var mutable = new StringBuilder(commandLine, commandLine.Length + 64);

        if (!Win32.CreateProcessW(
                null, mutable, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                Build.RepoRoot, ref si, out var pi))
        {
            var err = Marshal.GetLastWin32Error();

            // 0x800711C7 / 3222153159 is the application-control refusal this
            // machine produces intermittently. CI is the authoritative gate for
            // running the assembled image, so a local block is a skip and a
            // block under the CI flag is a failure.
            if (Environment.GetEnvironmentVariable("SWARM_REQUIRE_NATIVE") == "1")
            {
                throw new InvalidOperationException($"CreateProcessW failed with {err} for: {commandLine}");
            }
            Assert.Skip(
                $"swarm.exe could not be started here (CreateProcessW failed with {err}); "
                    + "the refusal dialog is not observed (SWARM_REQUIRE_NATIVE not set)");
        }

        Win32.CloseHandle(pi.hThread);
        return new ChildProcess(pi.hProcess, pi.dwProcessId, this.handle);
    }

    public void Dispose()
    {
        if (this.handle != IntPtr.Zero)
        {
            Win32.CloseDesktop(this.handle);
            this.handle = IntPtr.Zero;
        }
    }
}

/// <summary>One launched exe and the windows it puts on the hidden desktop.</summary>
internal sealed class ChildProcess : IDisposable
{
    private readonly uint pid;
    private readonly IntPtr desktop;
    private IntPtr process;

    public ChildProcess(IntPtr process, uint pid, IntPtr desktop)
    {
        this.process = process;
        this.pid = pid;
        this.desktop = desktop;
    }

    public IntPtr WaitForFirstWindow(TimeSpan within)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < within)
        {
            var found = this.FirstWindow();
            if (found != IntPtr.Zero)
            {
                return found;
            }

            if (Win32.WaitForSingleObject(this.process, 50) == Win32.WAIT_OBJECT_0)
            {
                Win32.GetExitCodeProcess(this.process, out var code);
                throw new InvalidOperationException(
                    $"swarm.exe exited with {code} without ever showing a window; the refusal box was expected first");
            }
        }

        throw new TimeoutException(
            $"no window from swarm.exe on the hidden desktop within {within.TotalSeconds:0} s, and the process is still running");
    }

    public (IntPtr? Window, int ExitCode) WatchForAnyWindowUntilExit(TimeSpan within)
    {
        var clock = Stopwatch.StartNew();
        int? exit = null;

        while (clock.Elapsed < within)
        {
            var found = this.FirstWindow();
            if (found != IntPtr.Zero)
            {
                return (found, exit ?? -1);
            }

            if (exit is not null)
            {
                // One further sweep after the exit was seen, so a window put up
                // in the last instant is not missed by the polling interval.
                return (null, exit.Value);
            }

            if (Win32.WaitForSingleObject(this.process, 50) == Win32.WAIT_OBJECT_0)
            {
                Win32.GetExitCodeProcess(this.process, out var code);
                exit = unchecked((int)code);
            }
        }

        throw new TimeoutException($"swarm.exe -smoke did not exit within {within.TotalSeconds:0} s");
    }

    public int WaitForExit(TimeSpan within)
    {
        if (Win32.WaitForSingleObject(this.process, (uint)within.TotalMilliseconds) != Win32.WAIT_OBJECT_0)
        {
            throw new TimeoutException($"swarm.exe did not exit within {within.TotalSeconds:0} s of the dialog being closed");
        }

        Win32.GetExitCodeProcess(this.process, out var code);
        return unchecked((int)code);
    }

    private IntPtr FirstWindow()
    {
        var mine = IntPtr.Zero;
        Win32.EnumDesktopWindows(this.desktop, (h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out var owner);
            if (owner != this.pid)
            {
                return true;
            }
            mine = h;
            return false;
        }, IntPtr.Zero);
        return mine;
    }

    public void Dispose()
    {
        if (this.process == IntPtr.Zero)
        {
            return;
        }

        // A test that failed mid-way leaves a modal box waiting on a desktop
        // nobody can reach, so the process is ended rather than orphaned.
        if (Win32.WaitForSingleObject(this.process, 0) != Win32.WAIT_OBJECT_0)
        {
            Win32.TerminateProcess(this.process, 0xDEAD);
            Win32.WaitForSingleObject(this.process, 5000);
        }

        Win32.CloseHandle(this.process);
        this.process = IntPtr.Zero;
    }
}

internal static class Win32
{
    public const uint GENERIC_ALL = 0x10000000;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WM_CLOSE = 0x0010;
    private const uint WM_GETTEXT = 0x000D;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    public static string ClassNameOf(IntPtr hWnd)
    {
        var buf = new StringBuilder(256);
        return GetClassNameW(hWnd, buf, buf.Capacity) > 0 ? buf.ToString() : string.Empty;
    }

    public static string CaptionOf(IntPtr hWnd)
    {
        var buf = new StringBuilder(512);
        return GetWindowTextW(hWnd, buf, buf.Capacity) > 0 ? buf.ToString() : string.Empty;
    }

    /// <summary>
    /// Every child control's text, concatenated. WM_GETTEXT is marshalled
    /// across the process boundary where GetWindowText is not, which is why the
    /// static carrying the sentence is asked rather than read.
    /// </summary>
    public static string TextOfEveryChild(IntPtr parent)
    {
        var all = new StringBuilder();
        EnumChildWindows(parent, (child, _) =>
        {
            var buf = new StringBuilder(2048);
            if (SendMessageTimeoutW(child, WM_GETTEXT, (IntPtr)buf.Capacity, buf, SMTO_ABORTIFHUNG, 5000, out _) != IntPtr.Zero)
            {
                all.AppendLine(buf.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return all.ToString();
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateDesktopW(
        string lpszDesktop, string? lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder buf, int max);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder buf, int max);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
