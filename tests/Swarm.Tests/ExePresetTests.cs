using System.Runtime.InteropServices;
using Xunit;
using Xunit.Sdk;

namespace Swarm.Tests;

/// <summary>
/// The shipped preset inside build/swarm.exe is the M1 acceptance
/// configuration, read back out of the assembled image rather than out of the
/// source it was written in (#170).
///
/// Three fields carry the claim. `n` is the acceptance count. `FLAG_GRID` is
/// what makes that count reachable - the same population on the brute-force
/// path is roughly 53 ms/pass on one core. `rmax` is the masterplan's M1
/// amendment bound: the cost is rmax-dependent and the amendment pins the
/// acceptance preset at 0.05 or below.
///
/// The extraction is deliberately structural. It knows no offset and no seed;
/// it decodes every position in the image as a candidate SwarmParams and keeps
/// the ones every field of which is inside the range src/kernel/abi.inc
/// declares for it. Exactly one position in the image may survive that filter.
/// Zero and two are both failures, so an image that stopped carrying a preset
/// cannot leave this suite green by matching nothing.
/// </summary>
public sealed class ExePresetTests
{
    [Fact]
    public void ExePresetIsTheM1AcceptanceConfiguration()
    {
        var preset = ExePreset.Extract(File.ReadAllBytes(Build.ExePath));
        ExePreset.AssertM1Acceptance(preset);
    }

    /// <summary>
    /// The must-catch half. The assertions above are worth exactly what they
    /// refuse, so each drift is built as bytes, pushed through the same
    /// extraction, and required to fail.
    ///
    /// The first leg is the non-vacuity one: the unmutated preset, embedded in
    /// the same synthetic image the mutations use, must pass. Without it a
    /// harness that failed on everything would look like a working guard.
    /// </summary>
    [Fact]
    public void ExePresetGuardGoesRedOnDrift()
    {
        var shipped = ExePreset.Extract(File.ReadAllBytes(Build.ExePath));

        // Non-vacuity: the synthetic image itself is not what makes the
        // mutations below fail.
        ExePreset.AssertM1Acceptance(ExePreset.Extract(ExePreset.Embed(shipped)));

        var brute = shipped;
        brute.Flags &= ~ExePreset.FlagGrid;
        AssertDriftIsCaught(brute, "FLAG_GRID");

        var preview = shipped;
        preview.N = 3500;
        AssertDriftIsCaught(preview, "n");

        // rmax is the field the M1 amendment is actually about, and 0.08 is
        // the value the preset carried before it became the acceptance one.
        var wide = shipped;
        wide.RMax = 0.08f;
        AssertDriftIsCaught(wide, "rmax");
    }

    private static void AssertDriftIsCaught(SwarmParams drifted, string field)
    {
        var extracted = ExePreset.Extract(ExePreset.Embed(drifted));
        var failure = Assert.ThrowsAny<XunitException>(() => ExePreset.AssertM1Acceptance(extracted));
        Assert.Contains(field, failure.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Finds and judges the SwarmParams the linker placed in a PE image. Kept
/// dependency free and offset free for the same reason PeImage is: the point
/// is to read the shipped bytes rather than to trust a description of them.
/// </summary>
public static class ExePreset
{
    /// <summary>src/kernel/abi.inc FLAG_GRID.</summary>
    public const uint FlagGrid = 1;

    /// <summary>src/kernel/abi.inc FLAG_SPLAT.</summary>
    public const uint FlagSplat = 2;

    /// <summary>src/kernel/abi.inc SP_FLAGS_VALID - the whole accepted mask.</summary>
    private const uint FlagsValid = FlagGrid | FlagSplat;

    /// <summary>src/kernel/abi.inc SP_SIZE.</summary>
    public static readonly int Size = Marshal.SizeOf<SwarmParams>();

    /// <summary>The masterplan's M1 amendment bound on the acceptance preset.</summary>
    private const float AcceptanceRMaxCeiling = 0.05f;

    /// <summary>The M1 acceptance count.</summary>
    private const uint AcceptanceN = 8192;

    /// <summary>
    /// The single position in <paramref name="image"/> that decodes as a
    /// SwarmParams every field of which is inside its declared range. Throws
    /// when that count is not one, so a preset that vanished from the image
    /// fails here rather than passing the assertions vacuously.
    /// </summary>
    public static SwarmParams Extract(ReadOnlySpan<byte> image)
    {
        var found = new List<int>();
        for (int off = 0; off + Size <= image.Length; off++)
        {
            if (IsInsideTheDeclaredRanges(MemoryMarshal.Read<SwarmParams>(image.Slice(off, Size))))
            {
                found.Add(off);
            }
        }

        if (found.Count != 1)
        {
            throw new InvalidDataException(
                $"expected exactly one SwarmParams-shaped run of bytes in the {image.Length}-byte image, "
                    + $"found {found.Count}"
                    + (found.Count == 0
                        ? ". Either the preset is gone or a field left the range src/kernel/abi.inc declares for it"
                        : $" (at {string.Join(", ", found)}). The filter no longer identifies the preset uniquely "
                            + "and this extraction cannot say which run is the shipped one."));
        }

        return MemoryMarshal.Read<SwarmParams>(image.Slice(found[0], Size));
    }

    /// <summary>
    /// The bytes of one preset, zero padded on both sides, so a constructed
    /// SwarmParams can be pushed through the same extraction the image goes
    /// through. Zero padding decodes as version 0, which no filter accepts.
    /// </summary>
    public static byte[] Embed(SwarmParams preset)
    {
        const int Pad = 64;
        var image = new byte[Pad + Size + Pad];
        MemoryMarshal.Write(image.AsSpan(Pad, Size), in preset);
        return image;
    }

    /// <summary>
    /// The three fields that make a preset the M1 acceptance configuration.
    /// Each message names the field, because the drift test matches on it.
    /// </summary>
    public static void AssertM1Acceptance(SwarmParams preset)
    {
        Assert.True(
            preset.N == AcceptanceN,
            $"the preset carries n = {preset.N}; the M1 acceptance count is {AcceptanceN}");

        Assert.True(
            (preset.Flags & FlagGrid) != 0,
            $"the preset has FLAG_GRID clear (flags = 0x{preset.Flags:X}), so the live exe runs the "
                + "brute-force path. At the acceptance count that path is roughly 53 ms/pass on one core");

        Assert.True(
            preset.RMax <= AcceptanceRMaxCeiling,
            $"the preset carries rmax = {preset.RMax}; the masterplan's M1 amendment pins the "
                + $"acceptance preset at rmax <= {AcceptanceRMaxCeiling} because the cost is rmax-dependent");
    }

    /// <summary>
    /// Every range in this method is transcribed from the field comments in
    /// src/kernel/abi.inc. A NaN fails every comparison here and is rejected,
    /// which is the intent.
    /// </summary>
    private static bool IsInsideTheDeclaredRanges(SwarmParams p)
    {
        if (p.Version != 1) return false;
        if (p.N < 1 || p.N > (1u << 20)) return false;
        if (p.SpeciesN < 1 || p.SpeciesN > 8) return false;
        if (!(p.RMax > 0f && p.RMax <= 0.25f)) return false;
        if (!(p.Beta >= 0.05f && p.Beta <= 0.95f)) return false;
        if (!(p.Dt > 0f && p.Dt <= 0.1f)) return false;
        if (!(p.Friction >= 0f && p.Friction <= 1f)) return false;
        if (!(p.ForceScale > 0f && p.ForceScale <= 100f)) return false;
        if (p.ForcePath > 3) return false;              // PATH_AUTO..PATH_SCALAR
        if ((p.Flags & ~FlagsValid) != 0) return false;

        for (int i = 0; i < 64; i++)
        {
            float m = p.Matrix[i];
            if (!(m >= -1f && m <= 1f)) return false;
        }
        return true;
    }
}
