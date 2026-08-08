using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The two files under <c>tests/fixtures/preset/</c> are what the CI preset
/// smoke feeds to <c>swarm.exe</c>, and the whole value of that smoke step is
/// that one of them is accepted and the other is refused. Neither property is
/// visible in the exit codes the step reads: an exit of 1 says the run stopped,
/// not that the grammar was what stopped it, and an exit of 0 says a window
/// opened, not that it opened on the scene the file names.
///
/// So the fixtures are pinned here, through the same
/// <c>swarm_parse_preset</c> the exe calls. A fixture edited until it no longer
/// discriminates turns the CI step into a pair of runs that pass for reasons
/// nobody checked, and that is the failure this file refuses.
/// </summary>
public sealed class PresetFixtureTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_parse_preset(byte[] text, uint len, ref SwarmParams p);

    // src/kernel/abi.inc.
    private const uint PerrRange = 8;

    // The one line the pair differs in, 1-based, and what the packed error
    // therefore has to name. `force 99999.0` is outside the pinned range for
    // force_scale; every other line is byte-identical to the accepted file.
    private const uint DifferingLine = 9;

    private static string FixtureDir =>
        Path.Combine(Build.RepoRoot, "tests", "fixtures", "preset");

    private static byte[] Read(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDir, name));

    private static int Parse(byte[] bytes, ref SwarmParams p)
    {
        _ = NativeKernel.Handle;
        return swarm_parse_preset(bytes, (uint)bytes.Length, ref p);
    }

    [Fact]
    public void TheAcceptedFixtureParsesIntoTheSceneItNames()
    {
        var p = default(SwarmParams);
        Assert.Equal(0, Parse(Read("accepted.txt"), ref p));

        // Read back rather than asserted from the file text: these are the
        // numbers the capture header carries when the exe runs this fixture,
        // and they are how a reader tells an applied preset from an ignored
        // one.
        Assert.Equal(1u, p.Version);
        Assert.Equal(4096u, p.N);
        Assert.Equal(3u, p.SpeciesN);
        Assert.Equal(0x1DuL, p.Seed);
        Assert.Equal(0.05f, p.RMax);

        // The grammar has no flags key, so the parser leaves the field zero
        // and FLAG_GRID is the exe's own decision (masterplan decision 10).
        Assert.Equal(0u, p.Flags);
    }

    [Fact]
    public void TheRejectedFixtureIsRefusedForTheReasonTheSmokeStepClaims()
    {
        var p = default(SwarmParams);
        int rc = Parse(Read("rejected.txt"), ref p);

        uint u = unchecked((uint)rc);
        Assert.True((u & 0x8000_0000) != 0, $"a refusal must set bit 31; got 0x{u:X8}");
        Assert.Equal(PerrRange, (u >> 20) & 0x7FF);
        Assert.Equal(DifferingLine, u & 0xFFFFF);
    }

    /// <summary>
    /// The pair is only a pair while it differs in one line. Two files that
    /// drifted apart would still pass the two tests above while the smoke step
    /// stopped comparing anything a reader could reason about.
    /// </summary>
    [Fact]
    public void TheFixturesDifferInExactlyTheOneLineThatMakesOneOfThemIllegal()
    {
        var accepted = File.ReadAllLines(Path.Combine(FixtureDir, "accepted.txt"));
        var rejected = File.ReadAllLines(Path.Combine(FixtureDir, "rejected.txt"));

        Assert.Equal(accepted.Length, rejected.Length);

        var differing = new List<int>();
        for (int i = 0; i < accepted.Length; i++)
        {
            if (accepted[i] != rejected[i])
            {
                differing.Add(i + 1);
            }
        }

        Assert.Equal([(int)DifferingLine], differing);
    }
}
