using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The capture reduction (src/platform/frametime.inc), across the P/Invoke
/// seam. swarm.exe writes its frame-time report with this routine, and the
/// reason it is exported at all is that the report is otherwise unassertable:
/// the one thing a percentile can get wrong is which sorted element it names,
/// and that is invisible from outside a process nobody can call into.
///
/// The definition under test is not this file's invention. docs/BENCHMARKS.md
/// carries the snippet every recorded figure in this repository was recomputed
/// with, and <see cref="Snippet"/> is that snippet ported to C# character for
/// character. Where the two disagree about the same samples, the exe and the
/// document disagree about the same dump, which is the failure the export
/// exists to catch.
/// </summary>
public sealed class FrameStatsTests
{
    [DllImport("swarm.kernel.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int swarm_frame_stats(
        [In, Out] ulong[] samples, uint count, ulong qpcFreq, [In, Out] ulong[] outStats);

    /// <summary>Slot order of the out array: mean, p50, p99, max.</summary>
    private const int Mean = 0, P50 = 1, P99 = 2, Max = 3;

    /// <summary>A plausible QPC rate, and the one this machine reports.</summary>
    private const ulong Freq = 10_000_000;

    /// <summary>CAPTURE_FRAMES in src/swarm.asm - the count that ships.</summary>
    private const int CaptureFrames = 3600;

    /// <summary>
    /// docs/BENCHMARKS.md's recompute snippet, in C#, over raw ticks. The
    /// snippet sorts milliseconds; ticks to milliseconds is multiplication by
    /// a positive constant, so the order and therefore the chosen element are
    /// the same, and doing the conversion after the choice keeps this port
    /// exact where the snippet's own double arithmetic is not.
    ///
    /// Where the port stops being a transcription: the index expressions below
    /// are the snippet's own, character for character, and that is the half
    /// this file is really about. The rendering to whole microseconds is not -
    /// the snippet gets there by formatting a double to three decimals, and
    /// <see cref="ToMicroseconds"/> is that rounding stated in integers. The
    /// two part only where the exact value falls on a half microsecond.
    /// </summary>
    private static (ulong mean, ulong p50, ulong p99, ulong max) Snippet(ulong[] samples, ulong freq)
    {
        var s = (ulong[])samples.Clone();
        Array.Sort(s);
        int count = s.Length;
        ulong sum = 0;
        foreach (var t in s)
        {
            sum += t;
        }
        return (
            ToMicroseconds(sum / (ulong)count, freq),
            ToMicroseconds(s[(int)Math.Floor(0.50 * (count - 1))], freq),
            ToMicroseconds(s[(int)Math.Floor(0.99 * (count - 1))], freq),
            ToMicroseconds(s[count - 1], freq));
    }

    /// <summary>Ticks to whole microseconds, half up - what the asm does, and
    /// what the snippet's three-decimal millisecond formatting amounts to.</summary>
    private static ulong ToMicroseconds(ulong ticks, ulong freq) =>
        (ticks * 1_000_000 + freq / 2) / freq;

    private static ulong[] Run(ulong[] samples, ulong freq, out int status)
    {
        var stats = new ulong[4];
        status = swarm_frame_stats(samples, (uint)samples.Length, freq, stats);
        return stats;
    }

    /// <summary>
    /// A shipped-length capture whose samples are all distinct and at least a
    /// microsecond apart at <see cref="Freq"/>, shuffled. Both properties are
    /// load-bearing for the index legs: a tie between neighbouring elements
    /// would let a wrong index agree with the right one by accident, and the
    /// shuffle is what makes the sort do work.
    /// </summary>
    private static ulong[] DistinctShuffledCapture(int seed)
    {
        var samples = new ulong[CaptureFrames];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ulong)(i + 1) * 337;    // 33.7 us apart at Freq
        }
        var rng = new Random(seed);
        for (int i = samples.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (samples[i], samples[j]) = (samples[j], samples[i]);
        }
        return samples;
    }

    /// <summary>
    /// The load-bearing leg. A synthetic capture of the shipped length, with
    /// every sample distinct so no index can be right by a tie, checked figure
    /// for figure against the document's own definition.
    /// </summary>
    [Fact]
    public void AtTheShippedSampleCountEveryFigureMatchesTheDocumentsDefinition()
    {
        var samples = DistinctShuffledCapture(0x5EED);

        var expected = Snippet(samples, Freq);
        var stats = Run(samples, Freq, out int status);

        Assert.Equal(0, status);
        Assert.Equal(expected.mean, stats[Mean]);
        Assert.Equal(expected.p50, stats[P50]);
        Assert.Equal(expected.p99, stats[P99]);
        Assert.Equal(expected.max, stats[Max]);
    }

    /// <summary>
    /// The near-miss the leg above is built to catch: the percentile index is
    /// the one-character mistake somebody actually makes. With 3,600 distinct
    /// samples the neighbouring element is a different value, so an index off
    /// by one in either direction would have reddened that leg rather than
    /// slipping through on a tie. Asserted here rather than assumed, otherwise
    /// the equality above proves only that two implementations agree about
    /// something that could not have differed.
    /// </summary>
    [Fact]
    public void TheNeighbouringIndexWouldGiveADifferentFigure()
    {
        var samples = DistinctShuffledCapture(0x5EED);

        var stats = Run(samples, Freq, out int status);
        Assert.Equal(0, status);

        // swarm_frame_stats sorts in place, so the array now holds the order
        // every index below is against.
        int i50 = (int)Math.Floor(0.50 * (CaptureFrames - 1));
        int i99 = (int)Math.Floor(0.99 * (CaptureFrames - 1));
        Assert.Equal(1799, i50);
        Assert.Equal(3563, i99);

        Assert.NotEqual(ToMicroseconds(samples[i50 - 1], Freq), stats[P50]);
        Assert.NotEqual(ToMicroseconds(samples[i50 + 1], Freq), stats[P50]);
        Assert.NotEqual(ToMicroseconds(samples[i99 - 1], Freq), stats[P99]);
        Assert.NotEqual(ToMicroseconds(samples[i99 + 1], Freq), stats[P99]);
    }

    /// <summary>
    /// The sort is a permutation and it is ascending. A reduction that dropped
    /// or duplicated an element could still report four plausible figures, and
    /// the multiset comparison is what refuses that.
    /// </summary>
    [Fact]
    public void TheSamplesComeBackSortedAndAsAPermutationOfWhatWentIn()
    {
        var rng = new Random(7);
        var samples = new ulong[1000];
        for (int i = 0; i < samples.Length; i++)
        {
            // A narrow range on purpose: duplicates are what a heap sort with a
            // strict-comparison slip loses, and a capture produces plenty.
            samples[i] = (ulong)rng.Next(0, 40);
        }
        var before = (ulong[])samples.Clone();
        Array.Sort(before);

        Run(samples, Freq, out int status);

        Assert.Equal(0, status);
        Assert.Equal(before, samples);
    }

    /// <summary>
    /// Odd, even, prime and power-of-two lengths, plus the degenerate ones. A
    /// heap sort's build loop and its extract loop have different off-by-one
    /// risks at each parity, and the single-element case skips both.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(101)]
    [InlineData(256)]
    [InlineData(3599)]
    [InlineData(3600)]
    [InlineData(3601)]
    public void EveryLengthAgreesWithTheDocumentsDefinition(int count)
    {
        var rng = new Random(count);
        var samples = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (ulong)rng.NextInt64(1, 5_000_000);
        }

        var expected = Snippet(samples, Freq);
        var stats = Run(samples, Freq, out int status);

        Assert.Equal(0, status);
        Assert.Equal(expected.mean, stats[Mean]);
        Assert.Equal(expected.p50, stats[P50]);
        Assert.Equal(expected.p99, stats[P99]);
        Assert.Equal(expected.max, stats[Max]);
    }

    /// <summary>
    /// An already-ascending and an already-descending input. Reverse order is
    /// the shape that makes a quadratic sort visible and a heap sort's extract
    /// loop do the most work, and ascending order is the one a build loop can
    /// skip entirely if its bound is wrong.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MonotonicInputReducesTheSameWay(bool ascending)
    {
        var samples = new ulong[CaptureFrames];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ulong)(ascending ? i + 1 : samples.Length - i);
        }

        var expected = Snippet(samples, Freq);
        var stats = Run(samples, Freq, out int status);

        Assert.Equal(0, status);
        Assert.Equal(expected.mean, stats[Mean]);
        Assert.Equal(expected.p50, stats[P50]);
        Assert.Equal(expected.p99, stats[P99]);
        Assert.Equal(expected.max, stats[Max]);
    }

    /// <summary>
    /// Fail-closed, and the out array is what proves it: a caller that reads
    /// its slots after a refusal must find what it put there, never a partial
    /// answer that reads like a measurement.
    /// </summary>
    [Theory]
    [InlineData(0u, Freq)]
    [InlineData(4u, 0ul)]
    public void ARefusedReductionWritesNothing(uint count, ulong freq)
    {
        var samples = new ulong[] { 5, 1, 4, 2 };
        var stats = new ulong[4] { 111, 222, 333, 444 };

        int status = swarm_frame_stats(samples, count, freq, stats);

        Assert.Equal(1, status);
        Assert.Equal(new ulong[] { 111, 222, 333, 444 }, stats);
    }

    /// <summary>
    /// The non-vacuity leg for the refusals above: the same four samples with a
    /// count and a rate that are in range do overwrite the slots. Without it, a
    /// routine that never wrote anything at all would pass both refusal rows.
    /// </summary>
    [Fact]
    public void AnAcceptedReductionOverwritesTheSameSlots()
    {
        // 10,000 / 20,000 / 40,000 / 50,000 ticks at 10 MHz is 1 / 2 / 4 / 5
        // milliseconds. Mean is 3 ms; the p50 index is floor(0.50 * 3) = 1 and
        // the p99 index floor(0.99 * 3) = 2, so the figures are the second and
        // third smallest rather than anything a mean could produce.
        var samples = new ulong[] { 50_000, 10_000, 40_000, 20_000 };
        var stats = new ulong[4] { 111, 222, 333, 444 };

        int status = swarm_frame_stats(samples, (uint)samples.Length, Freq, stats);

        Assert.Equal(0, status);
        Assert.Equal(new ulong[] { 3_000, 2_000, 4_000, 5_000 }, stats);
    }

    /// <summary>
    /// A tick rate the QPC of a virtual machine really does report, chosen
    /// because it divides nothing evenly: the microsecond conversion rounds
    /// rather than truncates, and a truncating one would land a microsecond
    /// low on roughly half of these.
    /// </summary>
    [Fact]
    public void AnAwkwardTickRateStillMatchesTheDocumentsDefinition()
    {
        const ulong awkward = 3_579_545;
        var rng = new Random(11);
        var samples = new ulong[CaptureFrames];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ulong)rng.NextInt64(1, 900_000);
        }

        var expected = Snippet(samples, awkward);
        var stats = Run(samples, awkward, out int status);

        Assert.Equal(0, status);
        Assert.Equal(expected.mean, stats[Mean]);
        Assert.Equal(expected.p50, stats[P50]);
        Assert.Equal(expected.p99, stats[P99]);
        Assert.Equal(expected.max, stats[Max]);
    }
}
