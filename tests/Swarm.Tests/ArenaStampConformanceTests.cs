using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Locks the statement `src/kernel/abi.inc` now makes about `AH_MAGIC` and
/// `AH_ABI`: init_core writes them and nothing reads them, so neither
/// authenticates an arena (#203).
///
/// The point is not that reading them would be wrong. It is that the header
/// says nothing reads them, and a document asserting a property the tree does
/// not have is the defect this repository treats as its own class. So this
/// test fails the moment a read appears, and its message sends the author to
/// the paragraph that has to move with it. It is a truthfulness lock, not a
/// ban: the repair for a red here can equally be to update `abi.inc`.
/// </summary>
public sealed class ArenaStampConformanceTests
{
    private static readonly string[] DiagnosticFields = ["AH_MAGIC", "AH_ABI"];

    /// <summary>A store: the field appears inside the destination operand,
    /// i.e. `[reg+FIELD], ` - the one form init_core uses.</summary>
    private static readonly Regex Store =
        new(@"\[[^\]]*\b(AH_MAGIC|AH_ABI)\b[^\]]*\]\s*,", RegexOptions.Compiled);

    [Fact]
    public void TheArenaStampIsWrittenAndNeverRead()
    {
        var srcDir = Path.Combine(Build.RepoRoot, "src");
        Assert.True(Directory.Exists(srcDir), "expected src/ to exist");

        var sources = Directory.GetFiles(srcDir, "*.asm", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(srcDir, "*.inc", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(sources); // a moved src/ must fail loudly, not pass vacuously

        var reads = new List<string>();
        int stores = 0;

        foreach (var path in sources)
        {
            var name = Path.GetRelativePath(Build.RepoRoot, path).Replace('\\', '/');
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i].Split(';')[0]; // the comment half is prose, not an access
                if (!DiagnosticFields.Any(f => Regex.IsMatch(code, $@"\b{f}\b")))
                {
                    continue;
                }

                // The definitions themselves (`AH_MAGIC   = 0`) are neither.
                if (Regex.IsMatch(code, @"^\s*(AH_MAGIC|AH_ABI)\s*="))
                {
                    continue;
                }

                var remainder = Store.Replace(code, string.Empty);
                if (Store.IsMatch(code))
                {
                    stores++;
                }

                if (DiagnosticFields.Any(f => Regex.IsMatch(remainder, $@"\b{f}\b")))
                {
                    reads.Add($"{name}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            reads.Count == 0,
            "src/kernel/abi.inc states that AH_MAGIC and AH_ABI are written and never read, and that "
                + "nothing authenticates an arena. These lines make that false:\n  "
                + string.Join("\n  ", reads)
                + "\nIf the stamp is meant to be load bearing, that is a welcome change - but the "
                + "paragraph above AH_MAGIC in src/kernel/abi.inc has to move in the same commit, "
                + "and the entry point doing the check needs a channel to fail closed through, which "
                + "the void-returning exports do not have.");

        // Non-vacuity: if the writes vanish too, the scan above is passing on
        // an empty set and this test would keep a deleted stamp green.
        Assert.True(
            stores == 2,
            $"expected exactly the two init_core stores of AH_MAGIC and AH_ABI, found {stores}. "
                + "If the stamp was removed, remove this test with it; if it moved, this count moves.");
    }
}
