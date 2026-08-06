using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The control word the FP cores run under is observable, and it is the pinned
/// one (#154).
///
/// Until this export existed the pin was engine state that was set and then
/// taken on trust: `src/platform/seam.inc` pins it, `src/platform/pool.inc`
/// pins each worker, and nothing anywhere could read back what a core actually
/// saw. The kernel tier cannot report it either, by design - `stmxcsr` is on
/// the forbidden-mnemonic list for `src/kernel/`, and this closes the hole at
/// the platform tier instead of weakening that ban.
///
/// `swarm_mxcsr` wears the ordinary FP-export seam, so what it reports is the
/// word in force where a core would run, not the word the caller happened to
/// arrive with.
/// </summary>
public sealed class MxcsrTests
{
    /// <summary>src/platform/seam.inc SEAM_MXCSR: FTZ and DAZ set, every
    /// exception masked, round-nearest.</summary>
    private const uint SeamMxcsr = 0x9FC0;

    private const uint FlushToZero = 1u << 15;
    private const uint DenormalsAreZero = 1u << 6;
    private const uint ExceptionMasks = 0x1F80;   // the six mask bits, PM..IM

    [DllImport("swarm.kernel.dll")]
    private static extern uint swarm_mxcsr();

    [Fact]
    public void CoresRunUnderThePinnedControlWord()
    {
        _ = NativeKernel.Handle; // skips rather than fails on a local load block

        uint word = swarm_mxcsr();

        Assert.Equal(SeamMxcsr, word);

        // The same value read field by field, because the number alone says
        // nothing about which property drifted when it changes. Determinism
        // against the scalar reference rests on these three: both denormal
        // controls on, and every exception masked so no FP fault can be raised
        // out of a core.
        Assert.Equal(FlushToZero, word & FlushToZero);
        Assert.Equal(DenormalsAreZero, word & DenormalsAreZero);
        Assert.Equal(ExceptionMasks, word & ExceptionMasks);
    }
}
