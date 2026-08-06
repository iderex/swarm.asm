using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The long-run fuzz entry for `swarm_parse_preset` (#184), the engine's only
/// untrusted-input surface.
///
/// <see cref="ParserTests.FuzzNeverCrashesNeverPartiallyApplies"/> already runs
/// a bounded property at a fixed budget on every pull request. This is the
/// entry that can be turned up far beyond what a pull request should wait for,
/// and it is separable from any workflow: it is a command line away.
///
/// HOW TO REPLAY A FAILURE. Every iteration is generated from a seed of its
/// own, derived from the root seed and the iteration index, and the failure
/// message prints that seed. Set it as the root and run a budget of one, and
/// the same bytes come back:
///
/// <code>
/// $env:SWARM_FUZZ_SEED = "0x1234ABCD"   # the seed the failure printed
/// $env:SWARM_FUZZ_BUDGET = "1"
/// Swarm.Tests.exe --filter-method "*LongRunParserFuzz*"
/// </code>
///
/// That works because iteration 0 of root seed S is generated from S itself,
/// so a printed iteration seed replayed as a root seed reproduces exactly one
/// case. Turning it up is the same two variables:
/// `SWARM_FUZZ_BUDGET=5000000` with no seed set runs the default root at a
/// scheduled-job budget.
///
/// WHAT THIS CANNOT DO, stated because it bounds the result. A fuzzer that
/// never reaches an interesting state passes forever while testing nothing, so
/// the generator is checked by <see cref="TheGeneratorReachesEveryOutcome"/>
/// rather than assumed - but reaching the twelve error codes is not the same
/// as reaching every branch behind them, and no coverage measurement is taken
/// here.
/// </summary>
public sealed class ParserFuzzTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_parse_preset(byte[] text, uint len, ref SwarmParams p);

    /// <summary>Small enough that every pull request pays for it without
    /// noticing, and non-zero so the entry is never merely present.</summary>
    private const int DefaultBudget = 4000;

    private const ulong DefaultSeed = 0x5761726D46757A7A; // "SwarmFuzz"

    /// <summary>The rejection codes the four named mutation classes can
    /// produce. See <see cref="TheGeneratorReachesEveryOutcome"/> for why 4
    /// and 12 are not among them.</summary>
    private static readonly uint[] Reachable = [1, 2, 3, 5, 6, 7, 8, 9, 10, 11];

    [Fact]
    public void LongRunParserFuzz()
    {
        _ = NativeKernel.Handle;

        ulong root = Env("SWARM_FUZZ_SEED", DefaultSeed);
        int budget = (int)Env("SWARM_FUZZ_BUDGET", DefaultBudget);
        Assert.True(budget > 0, "SWARM_FUZZ_BUDGET must be positive");

        for (int i = 0; i < budget; i++)
        {
            ulong seed = IterationSeed(root, i);
            var (input, how) = Generate(seed);

            // Invariant 1 is that control returns from here at all. A crash in
            // the parser takes the process with it, which no assertion can
            // catch and no assertion needs to.
            var p = Sentinel();
            int rc = swarm_parse_preset(input, (uint)input.Length, ref p);

            if (rc == 0)
            {
                AssertAccepted(p, seed, how);
            }
            else
            {
                AssertRejectedCleanly(rc, p, seed, how);
            }
        }
    }

    /// <summary>
    /// Non-vacuity for the generator. A mutation set that only ever produced
    /// garbage bytes, or only ever produced valid presets, would leave the
    /// entry above green while exercising almost nothing.
    ///
    /// The set asserted here is CLOSED in both directions: every code in it
    /// must be reached and no code outside it may be, so a class that stops
    /// producing what it is named for fails here, and so does one that starts
    /// producing something new without this census being reconsidered.
    ///
    /// TWO CODES ARE DELIBERATELY ABSENT, and the reason is a property of the
    /// four mutation classes this issue names rather than an oversight.
    /// PERR_MISSING_KEY (4) needs a key line REMOVED while the matrix line
    /// survives, and none of truncation, duplication, numeral substitution or
    /// matrix widening removes a line from the middle of a file.
    /// PERR_EXTRA_TOKEN (12) needs a surplus token on a STRUCTURAL line, and
    /// the widening class widens matrix data rows, which is PERR_MATRIX_SHAPE
    /// instead. Both are covered, by named cases rather than by chance, in
    /// <see cref="ParserTests.StructuralErrors"/>. Claiming this entry reaches
    /// them would be the more comfortable sentence and the false one.
    /// </summary>
    [Fact]
    public void TheGeneratorReachesEveryOutcome()
    {
        _ = NativeKernel.Handle;

        var reached = new HashSet<uint>();
        bool accepted = false;

        for (int i = 0; i < DefaultBudget; i++)
        {
            var (input, _) = Generate(IterationSeed(DefaultSeed, i));
            var p = Sentinel();
            int rc = swarm_parse_preset(input, (uint)input.Length, ref p);
            if (rc == 0)
            {
                accepted = true;
            }
            else
            {
                reached.Add(Decode(rc, IterationSeed(DefaultSeed, i), "census").Code);
            }
        }

        Assert.True(accepted, $"{DefaultBudget} iterations produced no accepted preset at all");

        var missing = Reachable.Except(reached).OrderBy(c => c).ToArray();
        Assert.True(
            missing.Length == 0,
            $"the generator never reached error code(s) {string.Join(", ", missing)} in {DefaultBudget} "
                + "iterations, so the fuzz entry above is not exercising those rejection paths. A "
                + "mutation class has most likely stopped producing what it is named for.");

        var surprising = reached.Except(Reachable).OrderBy(c => c).ToArray();
        Assert.True(
            surprising.Length == 0,
            $"the generator reached error code(s) {string.Join(", ", surprising)}, which the doc comment "
                + "above says these four mutation classes cannot produce. That sentence is now false and "
                + "has to move: either widen this set with the reason, or find out what changed in the "
                + "parser to make the code reachable from here.");
    }

    /// <summary>
    /// The replay path, as an executed check rather than a paragraph. What the
    /// failure message promises is that its printed seed, set as the root at a
    /// budget of one, produces the same bytes. That holds because the input is
    /// a pure function of the iteration seed and because iteration 0 of a root
    /// is the root itself, and both halves are asserted here over a spread of
    /// indices rather than left to be re-derived by a reader at 3am.
    /// </summary>
    [Fact]
    public void AnyIterationReplaysAsARootSeed()
    {
        foreach (int i in new[] { 0, 1, 2, 37, 1000, 4001, 999_983 })
        {
            ulong printed = IterationSeed(DefaultSeed, i);

            var (original, howOriginal) = Generate(printed);
            var (replayed, howReplayed) = Generate(IterationSeed(printed, 0));

            Assert.Equal(printed, IterationSeed(printed, 0));
            Assert.Equal(howOriginal, howReplayed);
            Assert.Equal(original, replayed);
        }

        // And distinct indices are distinct cases, or the replay above would
        // be true of a generator that ignored its seed.
        var seeds = Enumerable.Range(0, 1000).Select(i => IterationSeed(DefaultSeed, i)).ToArray();
        Assert.Equal(seeds.Length, seeds.Distinct().Count());
    }

    // --- the invariants ----------------------------------------------------

    private static void AssertAccepted(in SwarmParams p, ulong seed, string how)
    {
        // An accepted preset is inside every range src/kernel/abi.inc declares.
        Assert.True(p.Version == 1, Replay(seed, how, $"accepted with version {p.Version}"));
        Assert.True(p.N is >= 1 and <= 1_048_576, Replay(seed, how, $"accepted with n {p.N}"));
        Assert.True(p.SpeciesN is >= 1 and <= 8, Replay(seed, how, $"accepted with species {p.SpeciesN}"));
        Assert.True(p.RMax > 0f && p.RMax <= 0.25f, Replay(seed, how, $"accepted with rmax {p.RMax}"));
        Assert.True(p.Beta >= 0.05f && p.Beta <= 0.95f, Replay(seed, how, $"accepted with beta {p.Beta}"));
        Assert.True(p.Dt > 0f && p.Dt <= 0.1f, Replay(seed, how, $"accepted with dt {p.Dt}"));
        Assert.True(p.Friction >= 0f && p.Friction <= 1f, Replay(seed, how, $"accepted with friction {p.Friction}"));
        Assert.True(p.ForceScale > 0f && p.ForceScale <= 100f, Replay(seed, how, $"accepted with force {p.ForceScale}"));
        for (int c = 0; c < 64; c++)
        {
            float m = p.Matrix[c];
            Assert.True(m >= -1f && m <= 1f, Replay(seed, how, $"accepted with matrix[{c}] = {m}"));
        }
    }

    private static void AssertRejectedCleanly(int rc, in SwarmParams p, ulong seed, string how)
    {
        var (code, line) = Decode(rc, seed, how);
        Assert.True(code is >= 1 and <= 12, Replay(seed, how, $"rejected with unknown error code {code}"));
        Assert.True(line <= 0xFFFFF, Replay(seed, how, $"rejected with line field {line} outside its 20 bits"));

        // Invariant 2, the two-phase commit: the single [out] dereference is
        // past every validation, so a rejection leaves the caller's struct
        // byte-untouched. Anything else is a partial application.
        Assert.True(
            IsSentinel(p),
            Replay(seed, how, $"rejected with error {code} but wrote to the output struct"));
    }

    private static string Replay(ulong seed, string how, string what) =>
        $"{what}. Mutation class: {how}. Replay this exact input with "
            + $"SWARM_FUZZ_SEED=0x{seed:X16} and SWARM_FUZZ_BUDGET=1.";

    // --- the generator -----------------------------------------------------

    private const string ValidPreset =
        "swarm 1\n" +
        "n 4096\n" +
        "species 3\n" +
        "seed 0x1D\n" +
        "rmax 0.05\n" +
        "beta 0.3\n" +
        "dt 0.02\n" +
        "friction 0.71\n" +
        "force 10.0\n" +
        "matrix\n" +
        "0.5 -0.2 1\n" +
        "-1 0.123456 0.0\n" +
        "0.25 -0.75 0.9\n" +
        "end\n";

    /// <summary>The numerals worth aiming at: each sits on, or just past, a
    /// boundary the parser has to decide about.</summary>
    private static readonly string[] BoundaryNumerals =
    [
        "0", "-0", "0.0", "1", "-1", "0.25", "0.2500001", "0.05", "0.0499999",
        "1048576", "1048577", "0x0", "0xFFFFFFFFFFFFFFFF", "0x10000000000000000",
        "0.1234567", "99999999999999999999", "1e5", "+1", ".5", "5.", "-",
        "0.95", "0.950001", "100.0", "100.0001", "0.1", "0.100001", "NaN", "inf",
    ];

    private static readonly string[] Keys =
        ["swarm", "n", "species", "seed", "rmax", "beta", "dt", "friction", "force"];

    /// <summary>
    /// One input from one seed. The class is returned alongside so a failure
    /// message says which mutation produced it without the reader re-deriving
    /// it from the seed.
    /// </summary>
    private static (byte[] Input, string How) Generate(ulong seed)
    {
        var rng = new SplitMix(seed);
        return (int)(rng.Next() % 5) switch
        {
            0 => (RandomBytes(rng), "random bytes"),
            1 => (Truncation(rng), "truncation"),
            2 => (DuplicatedKey(rng), "duplicated key"),
            3 => (BoundaryNumeral(rng), "boundary numeral"),
            _ => (OversizedMatrix(rng), "oversized matrix"),
        };
    }

    private static byte[] RandomBytes(SplitMix rng)
    {
        var b = new byte[(int)(rng.Next() % 400)];
        for (int i = 0; i < b.Length; i++)
        {
            // A quarter of the bytes are drawn from the grammar's own alphabet,
            // so a purely random stream still reaches the tokenizer sometimes
            // instead of dying on the first byte every time.
            b[i] = (rng.Next() % 4 == 0)
                ? (byte)" \nabcdefimnorstuwx0123456789.-x"[(int)(rng.Next() % 30)]
                : (byte)(rng.Next() % 256);
        }
        return b;
    }

    private static byte[] Truncation(SplitMix rng)
    {
        var full = Encoding.ASCII.GetBytes(ValidPreset);
        return full[..(int)(rng.Next() % (ulong)(full.Length + 1))];
    }

    private static byte[] DuplicatedKey(SplitMix rng)
    {
        var lines = ValidPreset.Split('\n').ToList();
        string key = Keys[(int)(rng.Next() % (ulong)Keys.Length)];
        int src = lines.FindIndex(l => l.StartsWith(key + " ", StringComparison.Ordinal));
        if (src < 0)
        {
            return Encoding.ASCII.GetBytes(ValidPreset);
        }
        // Reinsert the same line somewhere else, so the duplicate can land
        // before or after the matrix as well as next to its original.
        lines.Insert((int)(rng.Next() % (ulong)(lines.Count + 1)), lines[src]);
        return Encoding.ASCII.GetBytes(string.Join('\n', lines));
    }

    private static byte[] BoundaryNumeral(SplitMix rng)
    {
        var lines = ValidPreset.Split('\n').ToList();
        string key = Keys[(int)(rng.Next() % (ulong)Keys.Length)];
        int at = lines.FindIndex(l => l.StartsWith(key + " ", StringComparison.Ordinal));
        if (at < 0)
        {
            return Encoding.ASCII.GetBytes(ValidPreset);
        }
        string numeral = BoundaryNumerals[(int)(rng.Next() % (ulong)BoundaryNumerals.Length)];
        lines[at] = $"{key} {numeral}";
        return Encoding.ASCII.GetBytes(string.Join('\n', lines));
    }

    private static byte[] OversizedMatrix(SplitMix rng)
    {
        var lines = ValidPreset.Split('\n').ToList();
        int end = lines.FindIndex(l => l == "end");
        if (end < 0)
        {
            return Encoding.ASCII.GetBytes(ValidPreset);
        }

        if (rng.Next() % 2 == 0)
        {
            // Too many rows, sometimes far past the 8-row ceiling.
            int extra = 1 + (int)(rng.Next() % 12);
            for (int i = 0; i < extra; i++)
            {
                lines.Insert(end, "0.1 0.2 0.3");
            }
        }
        else
        {
            // Too many columns on one row, sometimes far past 8.
            int row = end - 1 - (int)(rng.Next() % 3);
            int extra = 1 + (int)(rng.Next() % 12);
            lines[row] = lines[row] + string.Concat(Enumerable.Repeat(" 0.1", extra));
        }
        return Encoding.ASCII.GetBytes(string.Join('\n', lines));
    }

    // --- plumbing ----------------------------------------------------------

    /// <summary>
    /// SplitMix64, the same generator the engine's own RNG is built on. Used
    /// here because it is seedable, has no framework version to drift under,
    /// and derives an iteration seed from a root seed in one step.
    /// </summary>
    private sealed class SplitMix(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// Iteration 0 of root S is S itself, which is what makes a printed
    /// iteration seed replayable as a root seed at a budget of one.
    ///
    /// Constant time in the index, which is the whole point at a scheduled
    /// budget: deriving iteration i by stepping a generator i times makes the
    /// run quadratic and a large budget never finishes. The mix is SplitMix64's
    /// own finaliser over the index scaled by the golden-ratio constant, so
    /// neighbouring indices land far apart.
    /// </summary>
    private static ulong IterationSeed(ulong root, int i) =>
        i == 0 ? root : Finalise(root ^ ((ulong)(uint)i * 0x9E3779B97F4A7C15));

    private static ulong Finalise(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    private static ulong Env(string name, ulong fallback)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
        {
            return fallback;
        }
        v = v.Trim();
        bool hex = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        ulong parsed = hex
            ? Convert.ToUInt64(v[2..], 16)
            : ulong.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        return parsed;
    }

    /// <summary>The packed error encoding from src/kernel/abi.inc:
    /// bit 31 set, bit 30 reserved zero, the code in bits 30:20, the line in
    /// the low 20. Bits 31 and 30 are asserted rather than masked away,
    /// because a code that arrives without them is an encoding defect and not
    /// a decoding detail.</summary>
    private static (uint Code, uint Line) Decode(int rc, ulong seed, string how)
    {
        uint u = unchecked((uint)rc);
        Assert.True((u & 0x8000_0000) != 0, Replay(seed, how, $"error 0x{u:X8} does not set bit 31"));
        Assert.True((u & 0x4000_0000) == 0, Replay(seed, how, $"error 0x{u:X8} sets reserved bit 30"));
        return ((u >> 20) & 0x7FF, u & 0xFFFFF);
    }

    private static SwarmParams Sentinel()
    {
        var p = default(SwarmParams);
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref p, 1)).Fill(0xCD);
        return p;
    }

    private static bool IsSentinel(in SwarmParams p)
    {
        foreach (byte b in MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in p, 1)))
        {
            if (b != 0xCD)
            {
                return false;
            }
        }
        return true;
    }
}
