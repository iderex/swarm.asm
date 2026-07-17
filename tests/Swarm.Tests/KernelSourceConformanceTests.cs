using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Source-level structural fitness functions for the simulation kernel
/// (src/kernel/*.inc). Where <see cref="ConformanceTests"/> pins the shipped
/// binary, these pin the kernel SOURCE against masterplan decisions 2 and 4:
/// the kernel is pure computation - no OS seam, imports, writable state, or
/// MXCSR/AVX-state ownership, and only IEEE-correctly-rounded ops - and every
/// routine carries a register-contract header. A PR that regresses either fails
/// the build with the exact offending file, line, and token.
/// </summary>
public sealed class KernelSourceConformanceTests
{
    // The kernel slices, scanned as text (never assembled here). Sorted so the
    // failure list is stable across runs.
    private static string[] KernelIncFiles()
    {
        var dir = Path.Combine(Build.RepoRoot, "src", "kernel");
        var files = Directory.GetFiles(dir, "*.inc");
        Array.Sort(files, StringComparer.Ordinal);
        Assert.NotEmpty(files); // a moved/renamed kernel dir must fail loudly, not pass vacuously
        return files;
    }

    // FASM comments run from the first ';' to end of line. Strip them BEFORE any
    // token match: prose that mentions a banned mnemonic (or the word "section"
    // inside "non-writable ... section") must never trip the scan, and a banned
    // instruction can only be flagged where it is real code.
    private static string StripComment(string line)
    {
        int semi = line.IndexOf(';');
        return semi < 0 ? line : line[..semi];
    }

    // Mnemonic/keyword boundary. Assembly identifiers are [A-Za-z0-9_] plus the
    // FASM label punctuation . $ @ ? ~ #. A banned token matches only as a
    // standalone mnemonic/directive - never inside a longer identifier (so the
    // allowed SSE ops movss / mulss / cvttss2si / sqrtss never read as fmul /
    // fst / fsqrt) and never as a label of the same name (a trailing ':' is
    // excluded on the right).
    private const string BackBoundary = @"(?<![A-Za-z0-9_.$@?~#])";
    private const string FwdBoundary = @"(?![A-Za-z0-9_.$@?~#:])";

    private static Regex WholeWord(string token) =>
        new(BackBoundary + Regex.Escape(token) + FwdBoundary,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Banned as real code anywhere in src/kernel/*.inc (masterplan decisions 2/4):
    private static readonly string[] BannedTokens =
    [
        "invoke", "import", "library",              // no OS seam / import table in the kernel
        "ldmxcsr", "stmxcsr", "vzeroupper",         // MXCSR and AVX-state transitions belong to the seam
        "rdtsc", "rdrand", "rdseed",                // no nondeterministic sources; the RNG is owned + seeded
        "vrsqrtps", "vrsqrtss", "vrcpps", "vrcpss", // approximate reciprocals break IEEE determinism
        "fld", "fild", "fst", "fstp", "fadd",       // the x87 stack is banned outright (SoA + SSE/AVX only)
        "fsub", "fmul", "fdiv", "fsqrt", "fabs",
        "fchs", "fxch", "fcom",
    ];

    /// <summary>
    /// Kernel purity: no OS calls, imports, writable sections, nondeterministic
    /// sources, x87, or approximate-reciprocal ops in src/kernel/*.inc. Comments
    /// are stripped first; the remaining code is matched whole-word so the
    /// allowed SSE mnemonics are never mistaken for the banned x87 ones.
    /// </summary>
    [Fact]
    public void KernelSourcePurityScan()
    {
        var banned = BannedTokens.Select(t => (Token: t, Rx: WholeWord(t))).ToArray();
        var sectionRx = WholeWord("section");
        Regex[] writableRx = [WholeWord("writable"), WholeWord("writeable")];

        var offenders = new List<string>();
        foreach (var path in KernelIncFiles())
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = StripComment(lines[i]);
                if (code.Length == 0)
                {
                    continue;
                }

                foreach (var (token, rx) in banned)
                {
                    if (rx.IsMatch(code))
                    {
                        offenders.Add($"{name}:{i + 1}: banned token '{token}'");
                    }
                }

                // "writable section": a section directive granting write access.
                if (sectionRx.IsMatch(code) && writableRx.Any(w => w.IsMatch(code)))
                {
                    offenders.Add($"{name}:{i + 1}: writable section directive");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "src/kernel is not pure - masterplan decisions 2/4 forbid OS calls, imports, " +
            "writable sections, nondeterministic sources, x87, and approximate-reciprocal ops:\n  " +
            string.Join("\n  ", offenders));
    }

    // A column-0 label: `name:` starting in the first column (a routine or a
    // data-table entry). Local labels (.foo, indented) and constants
    // (NAME = value, no colon) never match. Group 2 is any inline body text.
    private static readonly Regex LabelRegex =
        new(@"^([A-Za-z_][A-Za-z0-9_]*):(.*)$", RegexOptions.CultureInvariant);

    // FASM data-definition / reservation directives. A column-0 label whose
    // first body token is one of these is a DATA table (kr_table, kr_matrix,
    // swarm_palette), not a routine, and carries no register contract.
    private static readonly HashSet<string> DataDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "db", "dw", "dd", "dp", "dq", "dt", "du", "file",
        "rb", "rw", "rd", "rp", "rq", "rt", "times",
    };

    // A register-contract field line inside a banner block: `;   in:`, `; out:`,
    // `; in/out:`, or `; clobbers:` (the routine-contract header convention).
    // `clobbers:` is present on every routine except the pure tail-dispatch stub
    // pass_core (step.inc), which declares `in:` only - hence "at least one
    // field" rather than "clobbers required", so the current tree stays green.
    private static readonly Regex ContractField =
        new(@"^\s*;\s*(clobbers|in/out|in|out)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AlignDirective =
        new(@"^align\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every routine in src/kernel/*.inc carries a register-contract header.
    ///
    /// Heuristic: enumerate column-0 `name:` labels. A label is a ROUTINE unless
    /// its first body token is a data directive (then it is a data table). For
    /// each routine, the contiguous comment block immediately above it - blank
    /// and `align` lines are transparent, the first real code/data line ends the
    /// walk - must contain at least one register-contract field
    /// (in:/out:/in/out:/clobbers:). This is zero-false-positive on the current
    /// tree (data tables kr_table/kr_matrix/swarm_palette and every .local label
    /// are excluded) yet fails if a routine is added as a bare label without a
    /// header banner.
    /// </summary>
    [Fact]
    public void RegisterContractHeaderPresent()
    {
        var offenders = new List<string>();
        foreach (var path in KernelIncFiles())
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = LabelRegex.Match(lines[i]);
                if (!m.Success)
                {
                    continue;
                }

                if (IsDataLabel(m.Groups[2].Value, lines, i))
                {
                    continue;
                }

                if (!HasContractHeader(lines, i))
                {
                    offenders.Add($"{name}:{i + 1}: routine '{m.Groups[1].Value}' has no register-contract header");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "every routine in src/kernel must carry a register-contract header " +
            "(a `; ----` banner with in:/out:/clobbers: fields):\n  " +
            string.Join("\n  ", offenders));
    }

    // A column-0 label is a data table when its first non-blank body token is a
    // FASM data directive. The token may sit inline after the colon or on a
    // following line (comments and blanks are skipped).
    private static bool IsDataLabel(string inlineBody, string[] lines, int labelIndex)
    {
        var first = StripComment(inlineBody).Trim();
        for (int j = labelIndex + 1; first.Length == 0 && j < lines.Length; j++)
        {
            first = StripComment(lines[j]).Trim();
        }

        var mnemonic = Regex.Match(first, @"^([A-Za-z][A-Za-z0-9]*)");
        return mnemonic.Success && DataDirectives.Contains(mnemonic.Groups[1].Value);
    }

    // Walk upward from the label over the contiguous banner block; blank and
    // `align` lines are transparent, the first real code/data line ends it.
    private static bool HasContractHeader(string[] lines, int labelIndex)
    {
        for (int k = labelIndex - 1; k >= 0; k--)
        {
            var trimmed = lines[k].Trim();
            if (trimmed.Length == 0 || AlignDirective.IsMatch(trimmed))
            {
                continue; // transparent
            }

            if (trimmed.StartsWith(';'))
            {
                if (ContractField.IsMatch(lines[k]))
                {
                    return true;
                }

                continue; // still inside the comment block
            }

            break; // first real code/data line: the block has ended
        }

        return false;
    }
}
