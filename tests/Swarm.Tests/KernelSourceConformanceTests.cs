using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Source-level structural fitness functions for the simulation kernel
/// (src/kernel/*.inc) and the two shells that host it (src/swarm.asm,
/// src/swarm_dll.asm). Where <see cref="ConformanceTests"/> pins the shipped
/// binary, these pin the SOURCE against masterplan decisions 2 and 4: the
/// kernel is pure computation - no OS seam, imports, writable state, or
/// MXCSR/AVX-state ownership, and only IEEE-correctly-rounded ops - and every
/// routine (kernel and shell) carries a register-contract header. A PR that
/// regresses either fails the build with the exact offending file, line, and
/// token.
///
/// The purity scan covers the kernel ONLY: the shells legitimately use
/// `invoke`/imports (they ARE the OS seam), so they are exempt from purity but
/// still header-checked.
/// </summary>
public sealed class KernelSourceConformanceTests
{
    // The kernel slices, scanned as text (never assembled here). Sorted so the
    // failure list is stable across runs.
    private static string[] KernelIncFiles()
    {
        var dir = Path.Combine(Build.RepoRoot, "src", "kernel");
        var incFiles = Directory.GetFiles(dir, "*.inc");
        Array.Sort(incFiles, StringComparer.Ordinal);
        Assert.NotEmpty(incFiles); // a moved/renamed kernel dir must fail loudly, not pass vacuously

        // Fail-closed: the kernel dir must hold ONLY .inc sources. A future
        // src/kernel/foo.asm (or .s) would otherwise slip past the *.inc glob
        // and go entirely unscanned - a silent purity/contract hole. Make that
        // a loud failure that forces a human decision, not a quiet gap.
        var stray = Directory.GetFiles(dir)
            .Where(f => !f.EndsWith(".inc", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.True(
            stray.Length == 0,
            "src/kernel must contain only .inc sources so the *.inc scan covers every kernel " +
            "file; stray non-.inc file(s): " + string.Join(", ", stray));

        return incFiles;
    }

    // The shell sources: the exe host (src/swarm.asm) and the test-DLL host
    // (src/swarm_dll.asm). These are the OS seam - exempt from the purity scan,
    // but their routines still carry register contracts that must be checked.
    private static string[] ShellAsmFiles()
    {
        var dir = Path.Combine(Build.RepoRoot, "src");
        var files = Directory.GetFiles(dir, "*.asm");
        Array.Sort(files, StringComparer.Ordinal);
        Assert.NotEmpty(files); // a moved/renamed shell must fail loudly, not pass vacuously
        return files;
    }

    // FASM comments run from the first UNQUOTED ';' to end of line. Strip them
    // BEFORE any token match: prose that mentions a banned mnemonic (or the word
    // "section" inside "non-writable ... section") must never trip the scan, and
    // a banned instruction can only be flagged where it is real code. The strip
    // is quote-aware so a ';' inside a FASM string/char literal (e.g. db ';')
    // does not truncate real code that follows on the same line (defence in
    // depth - no such literal exists in the tree today). FASM escapes a quote
    // inside a same-quoted literal by doubling it ('' or "").
    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < line.Length && line[i + 1] == quote)
                    {
                        i++; // doubled quote = escaped literal quote, stays in the string
                        continue;
                    }

                    quote = '\0'; // closing quote
                }
            }
            else if (c == ';')
            {
                return line[..i];
            }
            else if (c is '\'' or '"')
            {
                quote = c; // opening quote
            }
        }

        return line;
    }

    // Mnemonic/keyword boundary. Assembly identifiers are [A-Za-z0-9_] plus the
    // FASM label punctuation . $ @ ? ~ #. A banned token matches only as a
    // standalone mnemonic/directive - never inside a longer identifier (so the
    // allowed SSE ops movss / mulss / cvttss2si / sqrtss never read as fmul /
    // fst / fsqrt, and the allowed correctly-rounded sqrtss is never read as the
    // banned approximate rsqrtss) and never as a label of the same name (a
    // trailing ':' is excluded on the right).
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
        "vrsqrtps", "vrsqrtss", "vrcpps", "vrcpss", // approximate reciprocals (VEX) break IEEE determinism
        "rsqrtps", "rsqrtss", "rcpps", "rcpss",     // ... and their legacy-SSE encodings, equally approximate
        "fld", "fild", "fst", "fstp", "fadd",       // the x87 stack is banned outright (SoA + SSE/AVX only)
        "fsub", "fmul", "fdiv", "fsqrt", "fabs",
        "fchs", "fxch", "fcom",
    ];

    /// <summary>
    /// Kernel purity: no OS calls, imports, writable sections, nondeterministic
    /// sources, x87, or approximate-reciprocal ops in src/kernel/*.inc. Comments
    /// are stripped first; the remaining code is matched whole-word so the
    /// allowed SSE mnemonics are never mistaken for the banned x87 ones, and the
    /// correctly-rounded sqrtss is never mistaken for the banned rsqrtss.
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

    /// <summary>
    /// The comment strip ignores a ';' inside a FASM string/char literal, so a
    /// (future) line carrying both a quoted literal and real code cannot hide a
    /// banned token behind the literal's semicolon (item 5, defence in depth - no
    /// such literal exists in the tree today). A naive first-';' split would
    /// truncate at the quoted semicolon and drop everything after it.
    /// </summary>
    [Fact]
    public void StripCommentIgnoresSemicolonInLiteral()
    {
        // A ';' inside a quoted literal is code, not a comment: a banned token
        // after the literal survives the strip and can still be flagged.
        const string hidden = "        db ';' rsqrtss xmm0, xmm0";
        Assert.Contains("rsqrtss", StripComment(hidden));
        Assert.DoesNotContain("rsqrtss", hidden[..hidden.IndexOf(';')]); // what a naive split would keep

        // Double-quoted literal, same guarantee.
        Assert.Contains("rcpps", StripComment("        db \";\" rcpps xmm0, xmm0"));

        // A real (unquoted) trailing comment is still stripped; the literal stays.
        var stripped = StripComment("        db ';'  ; trailing comment");
        Assert.Contains("db ';'", stripped);
        Assert.DoesNotContain("trailing", stripped);

        // A doubled quote escapes the delimiter; the ';' inside stays in the string.
        Assert.Contains("'a''b;c'", StripComment("        db 'a''b;c' ; note"));

        // Ordinary cases unchanged: a leading comment strips to empty, and a
        // normal trailing comment is removed while the allowed sqrtss is kept.
        Assert.Equal("", StripComment("; a comment mentioning rsqrtss"));
        Assert.Contains("sqrtss", StripComment("        sqrtss  xmm4, xmm4 ; r"));
    }

    /// <summary>
    /// Regression for the struct-init false-positive (item 1): a future data
    /// table written `mytable: WNDCLASSEX ...` (colon + inline struct type) is
    /// DATA, not a routine, so the header scans must classify it as a data label
    /// and never flag it for a missing register contract. A real routine (first
    /// body token an instruction, not a struct type) stays a routine, and an
    /// unrecognised struct type fails closed (scanned as a routine).
    /// </summary>
    [Fact]
    public void IsDataLabelRecognisesStructInitDataTables()
    {
        // Colon + inline struct type: each of the win64a.inc struct macros the
        // shell uses names a data table, not a routine.
        Assert.True(IsDataLabelFor("mytable: WNDCLASSEX sizeof.WNDCLASSEX, CS_OWNDC, WindowProc"));
        Assert.True(IsDataLabelFor("r: RECT 0, 0, 16, 16"));
        Assert.True(IsDataLabelFor("bmi: BITMAPINFOHEADER sizeof.BITMAPINFOHEADER, 8, -8, 1, 32"));
        Assert.True(IsDataLabelFor("m: MSG"));
        Assert.True(IsDataLabelFor("t: TCHAR 'x', 0"));

        // The plain data directives still register as data (unchanged behaviour).
        Assert.True(IsDataLabelFor("tbl: dd 1, 2, 3"));

        // Struct type on the line BELOW the label (an intervening comment/blank
        // is transparent), mirroring how a real table might be laid out.
        string[] split = ["mytable:", "        ; the window class", "        WNDCLASSEX sizeof.WNDCLASSEX, 0"];
        Assert.True(IsDataLabel(LabelRegex.Match(split[0]).Groups[2].Value, split, 0));

        // A real routine is NOT a data label: its first token is an instruction,
        // so it stays subject to the register-contract header requirement.
        Assert.False(IsDataLabelFor("do_thing: mov eax, 1"));
        Assert.False(IsDataLabelFor("bogus_routine: ret"));

        // An unknown struct type is deliberately NOT whitelisted: it fails closed
        // (scanned as a routine) so a new struct-init table forces a conscious
        // addition to DataStructTypes rather than silently slipping the scan.
        Assert.False(IsDataLabelFor("pt: POINT 1, 2"));
    }

    // Match a single source line as a column-0 label and run IsDataLabel on it,
    // exactly as the header scans do (LabelRegex group 2 = the inline body).
    private static bool IsDataLabelFor(string line)
    {
        var m = LabelRegex.Match(line);
        Assert.True(m.Success, $"not a column-0 label: {line}");
        return IsDataLabel(m.Groups[2].Value, [line], 0);
    }

    // A column-0 label: `name:` starting in the first column (a routine or a
    // data-table entry). Local labels (.foo, indented) and constants
    // (NAME = value, no colon) never match. Group 2 is any inline body text.
    private static readonly Regex LabelRegex =
        new(@"^([A-Za-z_][A-Za-z0-9_]*):(.*)$", RegexOptions.CultureInvariant);

    // A `proc NAME ...` definition (win64a.inc macro). The shells declare
    // WindowProc this way rather than as a bare `name:` label, so the shell scan
    // recognises it as a routine too. Group 1 is the routine name.
    private static readonly Regex ProcRegex =
        new(@"^\s*proc\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);

    // FASM data-definition / reservation directives. A column-0 label whose
    // first body token is one of these is a DATA table (kr_table, kr_matrix,
    // swarm_palette, sim_params), not a routine, and carries no register contract.
    private static readonly HashSet<string> DataDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "db", "dw", "dd", "dp", "dq", "dt", "du", "file",
        "rb", "rw", "rd", "rp", "rq", "rt", "times",
    };

    // FASM struct-type names that initialise a data table inline
    // (`label: WNDCLASSEX ...`). These are win64a.inc struct macros, not code:
    // a column-0 label whose first body token is one of them is a DATA table,
    // not a routine, so it carries no register contract. The tree's struct-init
    // defs use the colon-less `name TYPE ...` form today (which never matches a
    // `name:` label at all), so this guards only a FUTURE colon+struct table -
    // e.g. `mytable: WNDCLASSEX ...` - from being mis-scanned as a headerless
    // routine. Kept case-sensitive: FASM struct macros are case-sensitive, so
    // only the exact spelling ever names a real data table (unlike the
    // case-insensitive db/dd directives above).
    private static readonly HashSet<string> DataStructTypes = new(StringComparer.Ordinal)
    {
        "TCHAR", "WNDCLASSEX", "RECT", "BITMAPINFOHEADER", "MSG",
    };

    // A register-contract field line inside a banner block: `;   in:`, `; out:`,
    // `; in/out:`, or `; clobbers:` (the routine-contract header convention).
    // Used by the shell scan, where thin seam wrappers legitimately declare
    // `in:` only, so "at least one contract field" is the right bar.
    private static readonly Regex ContractField =
        new(@"^\s*;\s*(clobbers|in/out|in|out)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The `; clobbers:` field specifically. Every kernel routine must document
    // what it clobbers (a wrong/absent clobber list is a bug even when nothing
    // crashes today - directive 4), so the kernel scan requires exactly this
    // field rather than merely "some contract field".
    private static readonly Regex ClobbersField =
        new(@"^\s*;\s*clobbers\s*:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AlignDirective =
        new(@"^align\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Documented limitation (issue #34, item 3) - PRESENCE, not TRUTHFULNESS.
    // The two contract-header scans below (RegisterContractHeaderPresent for the
    // kernel, ShellRoutineContractHeaderPresent for the shells) verify that a
    // routine's banner CONTAINS the required contract field(s): a `clobbers:`
    // line for the kernel, at least one in:/out:/in/out:/clobbers: field for the
    // shells. They do NOT verify the listed registers are ACCURATE - a stale or
    // wrong `clobbers:` list passes as long as the line exists. Machine-checking
    // truthfulness would mean parsing every routine body and modelling the
    // register effects of every macro and callee it reaches, a disproportionate
    // effort against a hand-audited kernel. This is a conscious accepted gap, not
    // an oversight: contract accuracy is enforced by the adversarial-review gate
    // (simd-reviewer reads each routine against its stated contract) and by
    // CodeRabbit, under prime directive 4 that makes a wrong clobber list a bug
    // even when nothing crashes today. If that ever proves insufficient, the
    // follow-up is a body-scanning truthfulness check, tracked as its own issue.

    /// <summary>
    /// Every routine in src/kernel/*.inc carries a register-contract header with
    /// a `clobbers:` field.
    ///
    /// Heuristic: enumerate column-0 `name:` labels. A label is a ROUTINE unless
    /// its first body token is a data directive (then it is a data table). For
    /// each routine, the contiguous comment block immediately above it - blank
    /// and `align` lines are transparent, the first real code/data line ends the
    /// walk - must contain a `clobbers:` field. This is zero-false-positive on
    /// the current tree (data tables kr_table/kr_matrix/swarm_palette and every
    /// .local label are excluded; every kernel routine, including the
    /// tail-dispatch stub pass_core, now documents its clobbers) yet fails if a
    /// routine is added as a bare label without a truthful clobbers line.
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

                if (!HasHeaderField(lines, i, ClobbersField))
                {
                    offenders.Add($"{name}:{i + 1}: routine '{m.Groups[1].Value}' has no `clobbers:` contract line");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "every routine in src/kernel must carry a register-contract header " +
            "with a truthful `clobbers:` field:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every routine in the shell sources (src/swarm.asm, src/swarm_dll.asm)
    /// carries a register-contract header. The shells ARE the OS seam - they use
    /// `invoke`/imports legitimately, so the purity scan does not cover them -
    /// but the platform routines and DLL export bodies still carry register
    /// contracts that nothing else header-checks.
    ///
    /// Heuristic: routines are column-0 `name:` labels (excluding data tables
    /// like sim_params) plus `proc NAME` definitions (WindowProc). A thin seam
    /// wrapper legitimately documents `in:` only, so the bar here is "at least
    /// one contract field" (in:/out:/in/out:/clobbers:), not clobbers
    /// specifically. Zero-false-positive on the current tree.
    /// </summary>
    [Fact]
    public void ShellRoutineContractHeaderPresent()
    {
        var offenders = new List<string>();
        foreach (var path in ShellAsmFiles())
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string routine;
                var label = LabelRegex.Match(lines[i]);
                if (label.Success)
                {
                    if (IsDataLabel(label.Groups[2].Value, lines, i))
                    {
                        continue; // data table (sim_params), not a routine
                    }

                    routine = label.Groups[1].Value;
                }
                else
                {
                    var proc = ProcRegex.Match(lines[i]);
                    if (!proc.Success)
                    {
                        continue;
                    }

                    routine = proc.Groups[1].Value;
                }

                if (!HasHeaderField(lines, i, ContractField))
                {
                    offenders.Add($"{name}:{i + 1}: routine '{routine}' has no register-contract header");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "every routine in the shell sources (src/*.asm) must carry a register-contract " +
            "header (a `; ----` banner with an in:/out:/clobbers: field):\n  " +
            string.Join("\n  ", offenders));
    }

    // A column-0 label is a data table when its first non-blank body token is a
    // FASM data directive (db/dd/...) or a struct-type name (WNDCLASSEX/...).
    // The token may sit inline after the colon or on a following line (comments
    // and blanks are skipped).
    private static bool IsDataLabel(string inlineBody, string[] lines, int labelIndex)
    {
        var first = StripComment(inlineBody).Trim();
        for (int j = labelIndex + 1; first.Length == 0 && j < lines.Length; j++)
        {
            first = StripComment(lines[j]).Trim();
        }

        var mnemonic = Regex.Match(first, @"^([A-Za-z][A-Za-z0-9]*)");
        if (!mnemonic.Success)
        {
            return false;
        }

        var token = mnemonic.Groups[1].Value;
        return DataDirectives.Contains(token) || DataStructTypes.Contains(token);
    }

    // Walk upward from the label over the contiguous banner block; blank and
    // `align` lines are transparent, the first real code/data line ends it. The
    // block satisfies the contract when a line matches the requested field regex.
    private static bool HasHeaderField(string[] lines, int labelIndex, Regex field)
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
                if (field.IsMatch(lines[k]))
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
