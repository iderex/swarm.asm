using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The showcase set is four scenes and not four spellings of one.
///
/// <see cref="PresetWalkTests"/> already refuses a committed preset that stops
/// parsing, and that is the whole of what a walk can say: a directory of four
/// identical matrices under four names passes it without complaint. The claim
/// here is the other one, and it is the reason the showcase exists at all - a
/// first run should show visibly different ecosystems rather than four
/// variations of one drift.
///
/// WHAT IS MEASURED AND WHAT IS NOT. Each scene is stepped through the shipped
/// kernel and four numbers are taken off the settled state: mean speed, the
/// share of a 128x128 wrapped bin grid holding anything, the count in the bin
/// an average particle sits in, and the share of a particle's bin-mates
/// carrying its own species. Every pair has to differ by <see cref="Margin"/>
/// on at least one of them. That refuses a near-duplicate, which is the
/// mistake somebody adding a fifth scene will actually make. It cannot judge
/// whether the descriptions in <c>presets/README.md</c> match what is on the
/// screen; that was settled by looking at rendered frames, and a matrix edited
/// later needs looking at again.
///
/// WHY THE THRESHOLD SURVIVES A KERNEL CHANGE. The four numbers are properties
/// of the settled ecology rather than of one trajectory, which is measurable
/// rather than hopeful in two directions. Across 400, 800, 1800 and 3600 steps
/// every scene holds its numbers, and across the AVX2 and scalar paths - whose
/// states after 400 steps share no bits at all, the system being chaotic - the
/// largest disagreement in any of the sixteen figures is 9.3%. The thinnest
/// pair below clears the threshold by more than that, and the transcripts are
/// in the pull request that added this file.
/// </summary>
public sealed class ShowcasePresetTests
{
    /// <summary>
    /// The showcase scenes, named rather than walked. A set is judged as a
    /// set, and a walk cannot say which files were meant to be compared -
    /// `headline.txt` and `dense.txt` are the same scene at two densities on
    /// purpose, so a walk here would refuse the bench pair for being what it
    /// is. A fifth showcase scene is added to this list by hand and has to
    /// earn its place against every scene already in it.
    /// </summary>
    private static readonly string[] Scenes =
        ["cells.txt", "chasers.txt", "knots.txt", "rosettes.txt"];

    /// <summary>
    /// Steps before the state is read. Long enough that every scene has
    /// settled: the same four numbers at 800, 1800 and 3600 steps move by less
    /// than the pair margin below.
    /// </summary>
    private const uint Steps = 400;

    /// <summary>
    /// The factor a pair has to reach on some axis. Below the 2.29x the
    /// thinnest pair actually shows, so a kernel change that moves the numbers
    /// by the measured 9.3% cross-path spread does not redden this; far above
    /// the 1.09x a one-change neighbour shows, which is the leg below.
    /// </summary>
    private const double Margin = 1.8;

    /// <summary>src/kernel/abi.inc FLAG_GRID, which src/swarm.asm applies to
    /// every preset it loads: the harness has to run what the exe runs.</summary>
    private const uint FlagGrid = 1;

    private static string PresetDir => Path.Combine(Build.RepoRoot, "presets");

    [Fact]
    public void EveryShowcaseSceneIsCommitted()
    {
        foreach (var scene in Scenes)
        {
            Assert.True(
                File.Exists(Path.Combine(PresetDir, scene)),
                $"{scene} is named as a showcase scene and is not in {PresetDir}");
        }
    }

    [Fact]
    public void NoTwoShowcaseScenesSettleIntoTheSameEcology()
    {
        var measured = Scenes.Select(s => (Name: s, Sig: Measure(Path.Combine(PresetDir, s)))).ToList();

        var alike = new List<string>();
        for (int i = 0; i < measured.Count; i++)
        {
            for (int j = i + 1; j < measured.Count; j++)
            {
                var (axis, ratio) = WidestAxis(measured[i].Sig, measured[j].Sig);
                if (ratio < Margin)
                {
                    alike.Add(
                        $"{measured[i].Name} and {measured[j].Name} differ by at most {ratio:F2}x "
                            + $"(on {axis}), under the {Margin:F1}x a distinct scene owes: "
                            + $"{measured[i].Sig} vs {measured[j].Sig}");
                }
            }
        }

        Assert.Empty(alike);
    }

    /// <summary>
    /// The must-catch leg, and the one-character mistake is the realistic one:
    /// a fifth scene authored by copying a fourth and nudging one matrix cell.
    /// Without this the guard above would be satisfied by a threshold nothing
    /// could ever cross.
    /// </summary>
    [Fact]
    public void AOneChangeNeighbourOfACommittedSceneIsRefused()
    {
        var original = Path.Combine(PresetDir, "knots.txt");
        var text = File.ReadAllText(original);

        // One cell of the matrix, 0.5 -> 0.55, and nothing else.
        const string row = "0.5 0.5 -0.7 0.5";
        Assert.Contains(row, text, StringComparison.Ordinal);
        var nudged = ReplaceFirst(text, row, "0.55 0.5 -0.7 0.5");
        Assert.NotEqual(text, nudged);

        var copy = Path.Combine(Path.GetTempPath(), $"swarm-neighbour-{Guid.NewGuid():N}.txt");
        File.WriteAllText(copy, nudged, new System.Text.ASCIIEncoding());
        try
        {
            var (axis, ratio) = WidestAxis(Measure(original), Measure(copy));
            Assert.True(
                ratio < Margin,
                $"a one-change neighbour of knots.txt reached {ratio:F2}x on {axis}, so the "
                    + "distinctness guard is not judging the ecology it claims to judge");
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>
    /// And the other half of the claim: each scene loads and runs live in the
    /// shipped exe, window and all, rather than only parsing.
    ///
    /// It runs on a desktop of this test's own making for the reason
    /// <see cref="PresetRefusalDialogTests"/> gives: four full-screen-ish
    /// windows appearing in front of whoever is at the machine is a test that
    /// gets switched off. The window is closed with WM_CLOSE so the exit code
    /// is the shipped path's own rather than a kill.
    /// </summary>
    [Fact]
    public void EveryShowcaseSceneRunsLiveInTheShippedExe()
    {
        using var desktop = HiddenDesktop.Create();

        foreach (var scene in Scenes)
        {
            var path = Path.Combine(PresetDir, scene);
            using var child = desktop.Launch($"\"{Build.ExePath}\" \"{path}\"");
            var window = child.WaitForFirstWindow(TimeSpan.FromSeconds(60));

            // The simulation window, not a refusal box: the box is class
            // #32770, so this distinguishes running from being refused.
            Assert.Equal("SWARM", Win32.ClassNameOf(window));
            Assert.Equal("swarm.asm", Win32.CaptionOf(window));

            // Long enough to be a run rather than a first paint. The window is
            // still there afterwards, which is what says the loop survived it.
            Thread.Sleep(1500);
            Assert.Equal("SWARM", Win32.ClassNameOf(window));

            Assert.True(
                Win32.PostMessageW(window, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero),
                $"WM_CLOSE could not be posted to the {scene} window: {Marshal.GetLastWin32Error()}");

            Assert.Equal(0, child.WaitForExit(TimeSpan.FromSeconds(30)));
        }
    }

    private static string ReplaceFirst(string text, string find, string with)
    {
        int at = text.IndexOf(find, StringComparison.Ordinal);
        return at < 0 ? text : text[..at] + with + text[(at + find.Length)..];
    }

    private static (string Axis, double Ratio) WidestAxis(Ecology a, Ecology b)
    {
        (string, double, double)[] axes =
        [
            ("mean speed", a.Speed, b.Speed),
            ("occupancy", a.Occupancy, b.Occupancy),
            ("crowding", a.Crowding, b.Crowding),
            ("same-species share", a.SameSpecies, b.SameSpecies),
        ];

        var widest = ("none", 1.0);
        foreach (var (name, x, y) in axes)
        {
            double lo = Math.Min(x, y), hi = Math.Max(x, y);

            // Two scenes that both settled to zero on an axis agree on it;
            // only one of them at zero is an unbounded difference. Collapsing
            // those two cases into a division would call a pair of frozen
            // scenes infinitely distinct.
            double ratio = hi <= 0 ? 1.0 : (lo <= 0 ? double.PositiveInfinity : hi / lo);
            if (ratio > widest.Item2)
            {
                widest = (name, ratio);
            }
        }
        return widest;
    }

    /// <summary>The four numbers that stand for what a scene settles into.</summary>
    private readonly record struct Ecology(
        double Speed, double Occupancy, double Crowding, double SameSpecies)
    {
        public override string ToString() =>
            $"speed {this.Speed:F3}, occupancy {this.Occupancy:F4}, "
                + $"crowding {this.Crowding:F2}, same-species {this.SameSpecies:F4}";
    }

    private static unsafe Ecology Measure(string presetPath)
    {
        _ = NativeKernel.Handle;

        var bytes = File.ReadAllBytes(presetPath);
        var p = default(SwarmParams);
        int rc = swarm_parse_preset(bytes, (uint)bytes.Length, ref p);
        Assert.Equal(0, rc);
        p.Flags = FlagGrid;

        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));
            swarm_step(arena, Steps);

            uint n = p.N;
            var x = new float[n];
            var y = new float[n];
            var vx = new float[n];
            var vy = new float[n];
            var species = new uint[n];
            Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, species));

            return Summarise(x, y, vx, vy, species, (int)p.SpeciesN);
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    /// <summary>
    /// The bin grid is 128x128 rather than the simulation's own: a statistic
    /// read off the structure the kernel bins by would move whenever the
    /// layout rule moves, for a reason that has nothing to do with the scene.
    /// </summary>
    private const int Bins = 128;

    private static Ecology Summarise(
        float[] x, float[] y, float[] vx, float[] vy, uint[] species, int speciesN)
    {
        int n = x.Length;

        double speed = 0;
        for (int i = 0; i < n; i++)
        {
            speed += Math.Sqrt(((double)vx[i] * vx[i]) + ((double)vy[i] * vy[i]));
        }
        speed /= n;

        var total = new int[Bins * Bins];
        var perSpecies = new int[Bins * Bins * 8];
        for (int i = 0; i < n; i++)
        {
            int b = (Bin(y[i]) * Bins) + Bin(x[i]);
            total[b]++;
            perSpecies[(b * 8) + (int)species[i]]++;
        }

        int occupied = 0;
        double sumOfSquares = 0;
        foreach (var c in total)
        {
            if (c > 0)
            {
                occupied++;
            }
            sumOfSquares += (double)c * c;
        }

        // Ordered pairs inside a bin, and how many of them are same-species.
        // 1.0 is a colony of one species; 1/speciesN is fully mixed.
        double same = 0;
        double pairs = 0;
        for (int b = 0; b < Bins * Bins; b++)
        {
            if (total[b] < 2)
            {
                continue;
            }
            for (int s = 0; s < speciesN; s++)
            {
                int c = perSpecies[(b * 8) + s];
                same += (double)c * (c - 1);
            }
            pairs += (double)total[b] * (total[b] - 1);
        }

        return new Ecology(
            speed,
            occupied / (double)(Bins * Bins),
            sumOfSquares / n,
            pairs == 0 ? 1.0 : same / pairs);
    }

    /// <summary>
    /// Positions are in [0, 1) by the wrap, but the bin is clamped rather than
    /// trusted: a value that reached exactly 1.0 would index one past the row.
    /// </summary>
    private static int Bin(float v)
    {
        int b = (int)(v * Bins);
        return b < 0 ? 0 : (b >= Bins ? Bins - 1 : b);
    }

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_parse_preset(byte[] text, uint len, ref SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern unsafe int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern unsafe void swarm_step(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern unsafe int swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);
}
