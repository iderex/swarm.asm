using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The preset parser is the engine's fail-closed input boundary (masterplan
/// decision 10): strict grammar, every key exactly once, pinned ranges,
/// two-phase commit — any error leaves the output byte-untouched and returns
/// a packed negative code carrying the offending line. These tests pin the
/// grammar, the error encoding, the pinned decimal-to-f32 conversion, and the
/// never-crash / never-partially-apply fuzz property.
/// </summary>
public sealed class ParserTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_parse_preset(byte[] text, uint len, ref SwarmParams p);

    // --- helpers -----------------------------------------------------------

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

    private static int Parse(string text, ref SwarmParams p)
    {
        _ = NativeKernel.Handle;
        var bytes = Encoding.ASCII.GetBytes(text);
        return swarm_parse_preset(bytes, (uint)bytes.Length, ref p);
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

    private static (uint Code, uint Line) Decode(int rc)
    {
        uint u = unchecked((uint)rc);
        Assert.True((u & 0x8000_0000) != 0, "error must set bit 31");
        Assert.True((u & 0x4000_0000) == 0, "bit 30 is reserved and must be 0");
        return ((u >> 20) & 0x7FF, u & 0xFFFFF);
    }

    /// <summary>The pinned decimal-to-f32 conversion the asm implements:
    /// integer mantissa, one f64 divide by 10^frac, one rounding to f32.</summary>
    private static float PinnedF32(long mantissa, int fracDigits)
    {
        double[] pow10 = [1, 10, 100, 1_000, 10_000, 100_000, 1_000_000];
        return (float)(mantissa / pow10[fracDigits]);
    }

    // Error codes per src/kernel/abi.inc.
    private const uint PerrVersion = 1, PerrUnknownKey = 2, PerrDupKey = 3, PerrMissingKey = 4,
        PerrNumSyntax = 5, PerrNumOverflow = 6, PerrNumFrac = 7, PerrRange = 8,
        PerrMatrixShape = 9, PerrMissingEnd = 10, PerrTrailing = 11, PerrExtraToken = 12;

    // --- structural: size of the seam struct --------------------------------

    [Fact]
    public void SwarmParamsMirrorIs304Bytes()
    {
        Assert.Equal(304, Marshal.SizeOf<SwarmParams>());
    }

    // --- happy path ----------------------------------------------------------

    [Fact]
    public void ValidRoundTrip()
    {
        var p = Sentinel();
        int rc = Parse(ValidPreset, ref p);

        Assert.Equal(0, rc);
        Assert.Equal(1u, p.Version);
        Assert.Equal(4096u, p.N);
        Assert.Equal(3u, p.SpeciesN);
        Assert.Equal(0x1Du, p.Seed);
        Assert.Equal(PinnedF32(5, 2), p.RMax);
        Assert.Equal(PinnedF32(3, 1), p.Beta);
        Assert.Equal(PinnedF32(2, 2), p.Dt);
        Assert.Equal(PinnedF32(71, 2), p.Friction);
        Assert.Equal(PinnedF32(100, 1), p.ForceScale);
        Assert.Equal(0u, p.ForcePath);
        Assert.Equal(0u, p.Flags);

        // Matrix: row-major, stride 8; unused slots zeroed.
        float[] expected = [0.5f, -0.2f, 1f, -1f, PinnedF32(123456, 6), 0f, 0.25f, -0.75f, 0.9f];
        Assert.Equal(PinnedF32(-2, 1), p.Matrix[1]); // bit-exact pinned conversion
        Assert.Equal(expected[0], p.Matrix[0]);
        Assert.Equal(expected[2], p.Matrix[2]);
        Assert.Equal(expected[3], p.Matrix[8 + 0]);
        Assert.Equal(expected[4], p.Matrix[8 + 1]);
        Assert.Equal(expected[5], p.Matrix[8 + 2]);
        Assert.Equal(expected[6], p.Matrix[16 + 0]);
        Assert.Equal(expected[7], p.Matrix[16 + 1]);
        Assert.Equal(expected[8], p.Matrix[16 + 2]);
        Assert.Equal(0f, p.Matrix[3]);   // beyond species_n: untouched zero
        Assert.Equal(0f, p.Matrix[63]);
    }

    [Fact]
    public void CrlfTabsBlankLinesAndDecimalSeedAccepted()
    {
        var text =
            "swarm 1\r\n" +
            "\r\n" +
            "n\t4096\r\n" +
            "species 2\r\n" +
            "seed 12345\r\n" +
            "rmax 0.05\r\n" +
            "beta 0.3\r\n" +
            "\r\n" +
            "dt 0.02\r\n" +
            "friction 0.71\r\n" +
            "force 10.0\r\n" +
            "matrix\r\n" +
            " 1 -1\r\n" +
            "\t0 0.5\r\n" +
            "end\r\n" +
            "\r\n";
        var p = Sentinel();
        Assert.Equal(0, Parse(text, ref p));
        Assert.Equal(12345u, p.Seed);
        Assert.Equal(1f, p.Matrix[0]);
        Assert.Equal(0.5f, p.Matrix[8 + 1]);
    }

    [Fact]
    public void KeyOrderIsFree()
    {
        var text =
            "swarm 1\n" +
            "force 10.0\nfriction 0.71\ndt 0.02\nbeta 0.3\nrmax 0.05\nseed 1\nspecies 1\nn 1\n" +
            "matrix\n0\nend\n";
        var p = Sentinel();
        Assert.Equal(0, Parse(text, ref p));
        Assert.Equal(1u, p.N);
    }

    // --- structural errors, with line numbers --------------------------------

    [Theory]
    [InlineData("swarm 2\nrest\n", PerrVersion, 1u)]
    [InlineData("swarms 1\n", PerrVersion, 1u)]
    [InlineData("swarm 1\nbogus 3\n", PerrUnknownKey, 2u)]
    [InlineData("swarm 1\nn 5\nn 6\n", PerrDupKey, 3u)]
    [InlineData("swarm 1\nn 5\nmatrix\n", PerrMissingKey, 3u)]
    [InlineData("swarm 1\nn\n", PerrNumSyntax, 2u)]
    [InlineData("swarm 1 extra\n", PerrExtraToken, 1u)]
    [InlineData("swarm 1\nn 5 6\n", PerrExtraToken, 2u)]
    [InlineData("", PerrMissingEnd, 1u)]
    [InlineData("swarm 1\nn 4096\n", PerrMissingEnd, 3u)]
    public void StructuralErrors(string text, uint code, uint line)
    {
        var p = Sentinel();
        int rc = Parse(text, ref p);
        Assert.True(rc < 0);
        var (c, l) = Decode(rc);
        Assert.Equal(code, c);
        Assert.Equal(line, l);
        Assert.True(IsSentinel(p), "output must stay untouched on error");
    }

    private static string WithKey(string key, string value)
    {
        // The valid preset with one key line replaced.
        var lines = ValidPreset.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(key + " ", StringComparison.Ordinal))
            {
                lines[i] = $"{key} {value}";
            }
        }
        return string.Join('\n', lines);
    }

    // --- number grammar -------------------------------------------------------

    [Theory]
    [InlineData("n", "0x10", PerrNumSyntax)]     // hex only for seed
    [InlineData("n", "-5", PerrNumSyntax)]
    [InlineData("n", "5.0", PerrNumSyntax)]      // integer key takes no dot
    [InlineData("rmax", ".5", PerrNumSyntax)]
    [InlineData("rmax", "5.", PerrNumSyntax)]
    [InlineData("rmax", "-", PerrNumSyntax)]
    [InlineData("rmax", "1e-3", PerrNumSyntax)]  // no exponent form
    [InlineData("rmax", "0.1234567", PerrNumFrac)]
    [InlineData("seed", "18446744073709551616", PerrNumOverflow)] // 2^64
    [InlineData("seed", "0x1FFFFFFFFFFFFFFFF", PerrNumOverflow)]  // 17 hex digits
    [InlineData("beta", "99999999999999999", PerrNumOverflow)]    // mantissa cap
    public void NumberGrammarRejections(string key, string value, uint code)
    {
        var p = Sentinel();
        int rc = Parse(WithKey(key, value), ref p);
        var (c, _) = Decode(rc);
        Assert.Equal(code, c);
        Assert.True(IsSentinel(p));
    }

    [Fact]
    public void SeedHexUpperAndLowerCaseAccepted()
    {
        var p = Sentinel();
        Assert.Equal(0, Parse(WithKey("seed", "0xDeadBeefCafe1234"), ref p));
        Assert.Equal(0xDEADBEEFCAFE1234u, p.Seed);
    }

    // --- ranges: strictness per the pinned table ------------------------------

    [Theory]
    [InlineData("n", "0")]
    [InlineData("n", "1048577")]
    [InlineData("species", "0")]
    [InlineData("species", "9")]
    [InlineData("rmax", "0.0")]          // strict lower bound
    [InlineData("rmax", "0.250001")]
    [InlineData("beta", "0.049999")]
    [InlineData("beta", "0.950001")]
    [InlineData("dt", "0.0")]            // strict lower bound
    [InlineData("dt", "0.100001")]
    [InlineData("friction", "1.000001")]
    [InlineData("force", "0.0")]         // strict lower bound
    // values are chosen distinctly outside the range in f32: near 100 the
    // f32 gap is ~7.6e-6, so 100.000001 would round back to 100.0 (in range).
    [InlineData("force", "100.01")]
    public void RangeRejections(string key, string value)
    {
        var p = Sentinel();
        int rc = Parse(WithKey(key, value), ref p);
        var (c, _) = Decode(rc);
        Assert.Equal(PerrRange, c);
        Assert.True(IsSentinel(p));
    }

    [Theory]
    [InlineData("n", "1048576")]
    [InlineData("species", "8")]         // needs 8 matrix rows -> built below
    [InlineData("rmax", "0.25")]         // non-strict upper bound
    [InlineData("beta", "0.05")]
    [InlineData("beta", "0.95")]
    [InlineData("dt", "0.1")]
    [InlineData("friction", "0.0")]      // non-strict lower bound
    [InlineData("friction", "1.0")]
    [InlineData("force", "100.0")]
    public void BoundaryValuesAccepted(string key, string value)
    {
        string text;
        if (key == "species")
        {
            var row = string.Join(' ', Enumerable.Repeat("0.1", 8));
            text =
                "swarm 1\nn 64\nspecies 8\nseed 1\nrmax 0.05\nbeta 0.3\ndt 0.02\n" +
                "friction 0.71\nforce 10.0\nmatrix\n" +
                string.Concat(Enumerable.Repeat(row + "\n", 8)) + "end\n";
        }
        else
        {
            text = WithKey(key, value);
        }
        var p = Sentinel();
        Assert.Equal(0, Parse(text, ref p));
    }

    // --- matrix shapes ---------------------------------------------------------

    [Theory]
    [InlineData("0.5 -0.2 1\n-1 0.1\n0.2 0.3 0.4\nend\n", PerrMatrixShape)]     // short row
    [InlineData("0.5 -0.2 1 0\n-1 0.1 0\n0.2 0.3 0.4\nend\n", PerrMatrixShape)] // long row
    [InlineData("0.5 -0.2 1\n-1 0.1 0\nend\n", PerrMatrixShape)]                // few rows
    [InlineData("0.5 -0.2 1\n-1 0.1 0\n0.2 0.3 0.4\n1 1 1\nend\n", PerrMatrixShape)] // many rows
    [InlineData("0.5 -0.2 2\n-1 0.1 0\n0.2 0.3 0.4\nend\n", PerrRange)]         // entry out of [-1,1]
    public void MatrixShapeAndRangeErrors(string matrixBlock, uint code)
    {
        var text = ValidPreset[..ValidPreset.IndexOf("matrix\n", StringComparison.Ordinal)]
            + "matrix\n" + matrixBlock;
        var p = Sentinel();
        int rc = Parse(text, ref p);
        var (c, _) = Decode(rc);
        Assert.Equal(code, c);
        Assert.True(IsSentinel(p));
    }

    [Fact]
    public void TrailingContentAfterEndRejected()
    {
        var p = Sentinel();
        // ValidPreset ends "end\n" (line 14); the appended "\n \nrogue\n" adds
        // an empty line 15, a blank line 16, then "rogue" on line 17.
        int rc = Parse(ValidPreset + "\n \nrogue\n", ref p);
        var (c, l) = Decode(rc);
        Assert.Equal(PerrTrailing, c);
        Assert.Equal(17u, l);
        Assert.True(IsSentinel(p));
    }

    // --- the fuzz property: never crash, never partially apply -----------------

    [Fact]
    public void FuzzNeverCrashesNeverPartiallyApplies()
    {
        _ = NativeKernel.Handle;
        var rng = new Random(1234); // seeded: determinism is a repo invariant
        var valid = Encoding.ASCII.GetBytes(ValidPreset);

        for (int i = 0; i < 3000; i++)
        {
            byte[] input;
            if (i % 2 == 0)
            {
                input = new byte[rng.Next(0, 400)];
                rng.NextBytes(input);
            }
            else
            {
                // structured mutation of a valid preset
                input = (byte[])valid.Clone();
                switch (rng.Next(4))
                {
                    case 0 when input.Length > 0: // flip a byte
                        input[rng.Next(input.Length)] = (byte)rng.Next(256);
                        break;
                    case 1: // truncate
                        input = input[..rng.Next(input.Length + 1)];
                        break;
                    case 2: // duplicate a slice
                        int s = rng.Next(input.Length);
                        int e = rng.Next(s, input.Length);
                        input = [.. input[..e], .. input[s..e], .. input[e..]];
                        break;
                    case 3 when input.Length > 0: // zero a byte (embedded NUL)
                        input[rng.Next(input.Length)] = 0;
                        break;
                }
            }

            var p = Sentinel();
            int rc = swarm_parse_preset(input, (uint)input.Length, ref p);

            if (rc == 0)
            {
                Assert.Equal(1u, p.Version);
                Assert.InRange(p.N, 1u, 1_048_576u);
                Assert.InRange(p.SpeciesN, 1u, 8u);
            }
            else
            {
                var (code, line) = Decode(rc);
                Assert.InRange(code, 1u, 12u);
                Assert.True(line <= 0xFFFFF);
                Assert.True(IsSentinel(p),
                    $"iteration {i}: output partially applied on error {code}");
            }
        }
    }
}
