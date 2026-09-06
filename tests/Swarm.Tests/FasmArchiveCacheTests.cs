using System.Text.RegularExpressions;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the shape the assembler-archive cache rests on (issue #344).
///
/// Three workflows carry the cache step as three copies, and the seeding
/// story - the scheduled jobs on <c>main</c> write the entry every pull
/// request restores - holds only while every copy computes the same key
/// against the same path. A renamed path or a reworded key in one copy would
/// split the namespace silently: no run goes red, the cache just stops
/// helping. So the rule is read out of the workflows rather than trusted from
/// their comments:
///
/// <list type="bullet">
///   <item>before every step that runs <c>./tools/get-fasm.ps1</c>, a step
///         pinned to one commit of <c>actions/cache</c> restores
///         <c>tools/fasm-archive</c> under one key derived from the script's
///         bytes, with no <c>restore-keys</c> - the script refuses a restored
///         mismatch rather than downloading over it, so a prefix hit after a
///         pin bump would be a red run - and the same commit and the same two
///         scalars in every copy,</item>
///   <item>except <c>release.yml</c>, which restores nothing through any
///         entry point of the action - the job that attests what it builds
///         must not take bytes out of a cache a pull request could have
///         written. <c>ReleaseGateTests</c> refuses the action's main entry
///         point there; this lock refuses the others too.</item>
/// </list>
///
/// WHAT THIS DOES NOT COVER. It reads workflow text, not a run: whether a
/// scheduled job actually ends green and saves, and which scope a run reads,
/// are facts about GitHub's cache service that only its logs establish. The
/// pairing is by position in the file, the nearest cache step above each
/// bootstrap, so a cache step in one job paired with a bootstrap in a later
/// job of the same file would pass; every bootstrapping workflow is one job
/// today. The rest of a copy's <c>with:</c> block is not compared beyond the
/// two scalars and the absence of <c>restore-keys</c>. A save-only entry point
/// of the action is not a restore and does not pair with a bootstrap. And the
/// bootstrap is matched by its own step line: a job that reached the script
/// only through <c>build.ps1</c>, which runs it when the toolchain is absent,
/// would download uncached and be seen by nothing here; every job that runs
/// <c>build.ps1</c> today carries the explicit bootstrap step before it.
/// </summary>
public sealed class FasmArchiveCacheTests
{
    private const string Bootstrap = "run: ./tools/get-fasm.ps1";
    private const string CachePath = "path: tools/fasm-archive";
    private const string CacheKey = "key: fasm-archive-${{ hashFiles('tools/get-fasm.ps1') }}";
    // Any entry point of the action counts against the release gate; only the
    // ones that restore count as the step paired with a bootstrap.
    private static readonly Regex CacheAny = new(@"^\s*uses:\s*actions/cache(?:/restore|/save)?@[0-9a-f]{40}\b", RegexOptions.Compiled);
    private static readonly Regex CacheRestores = new(@"^\s*uses:\s*(actions/cache(?:/restore)?@[0-9a-f]{40})\b", RegexOptions.Compiled);

    [Fact]
    public void EveryBootstrapRestoresTheArchiveUnderOneKeyExceptTheRelease()
    {
        var dir = Path.Combine(Build.RepoRoot, ".github", "workflows");
        Assert.True(Directory.Exists(dir), "expected .github/workflows to exist");

        var offenders = new List<string>();
        var pins = new SortedSet<string>(StringComparer.Ordinal);
        var covered = 0;
        var files = Directory.GetFiles(dir, "*.yml")
            .Concat(Directory.GetFiles(dir, "*.yaml"))
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);
            var bootstraps = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == Bootstrap).ToArray();
            if (name == "release.yml")
            {
                foreach (var c in Enumerable.Range(0, lines.Length).Where(i => CacheAny.IsMatch(lines[i])))
                {
                    offenders.Add($"{name}:{c + 1}: the release gate restores a cache");
                }
                continue;
            }

            var caches = Enumerable.Range(0, lines.Length).Where(i => CacheRestores.IsMatch(lines[i])).ToArray();
            foreach (var bootstrap in bootstraps)
            {
                covered++;
                var cache = caches.Where(c => c < bootstrap).DefaultIfEmpty(-1).Max();
                if (cache < 0)
                {
                    offenders.Add($"{name}:{bootstrap + 1}: no SHA-pinned restoring actions/cache step before this bootstrap");
                    continue;
                }

                pins.Add(CacheRestores.Match(lines[cache]).Groups[1].Value);

                // The step's `with:` block sits between the uses: line and the
                // bootstrap step; both scalars must be there, verbatim, and no
                // prefix fallback.
                var block = lines[(cache + 1)..bootstrap].Select(l => l.Trim()).ToArray();
                if (!block.Contains(CachePath))
                {
                    offenders.Add($"{name}:{ScalarLine(block, cache, "path:")}: the cache step does not restore `{CachePath}`");
                }
                if (!block.Contains(CacheKey))
                {
                    offenders.Add($"{name}:{ScalarLine(block, cache, "key:")}: the cache step's key is not `{CacheKey}`");
                }
                var fallback = Array.FindIndex(block, l => l.StartsWith("restore-keys", StringComparison.Ordinal));
                if (fallback >= 0)
                {
                    offenders.Add($"{name}:{cache + 1 + fallback + 1}: the cache step carries restore-keys; a prefix hit the script refuses is a red run, not a re-fetch");
                }
            }
        }

        if (pins.Count > 1)
        {
            offenders.Add("the cache steps pin different commits of the action: " + string.Join(", ", pins));
        }

        Assert.True(covered >= 2, "expected at least the pull-request gate and a scheduled job to bootstrap the assembler; found " + covered);
        Assert.True(
            offenders.Count == 0,
            "the assembler-archive cache is keyed, pathed or pinned differently across workflows, carries a prefix fallback, or reaches the release gate (issue #344):\n  "
                + string.Join("\n  ", offenders));
    }

    // The 1-based line of the scalar named by `key` inside the step's block,
    // or the step's `uses:` line when the scalar is absent altogether.
    private static int ScalarLine(string[] block, int cache, string key)
    {
        int i = Array.FindIndex(block, l => l.StartsWith(key, StringComparison.Ordinal));
        return i < 0 ? cache + 1 : cache + 1 + i + 1;
    }
}
