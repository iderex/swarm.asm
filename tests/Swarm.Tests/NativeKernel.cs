using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Loads the freshly built swarm.kernel.dll by absolute path and routes every
/// <c>[DllImport("swarm.kernel.dll")]</c> in this assembly to it, so the tests
/// never depend on the OS library search path. Also the single place that
/// handles a Device Guard / Smart App Control load block: CI sets
/// <c>SWARM_REQUIRE_NATIVE</c> and must load for real; a dev machine may skip.
/// </summary>
public static class NativeKernel
{
    private static readonly Lazy<nint> Loaded = new(Load);

    /// <summary>The loaded module handle; loading (and the resolver) trigger on
    /// first access. Throws a skip on a local policy block.</summary>
    public static nint Handle => Loaded.Value;

    /// <summary>Registered at assembly load, before any test runs: the first
    /// P/Invoke always hits the resolver, so no test can accidentally fall
    /// back to the OS library search path by skipping a Handle touch.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterResolver()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(NativeKernel).Assembly,
            (name, _, _) => name == "swarm.kernel.dll" ? Handle : nint.Zero);
    }

    private static nint Load()
    {
        try
        {
            return NativeLibrary.Load(Build.DllPath);
        }
        catch (DllNotFoundException ex) when (IsPolicyBlock(ex))
        {
            if (Environment.GetEnvironmentVariable("SWARM_REQUIRE_NATIVE") == "1")
            {
                throw;
            }
            Assert.Skip("native DLL load blocked by local execution policy (SWARM_REQUIRE_NATIVE not set)");
            throw; // unreachable: Assert.Skip throws
        }
    }

    private static bool IsPolicyBlock(Exception ex) =>
        ex.Message.Contains("0x800711C7", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Device Guard", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("application control", StringComparison.OrdinalIgnoreCase);
}
