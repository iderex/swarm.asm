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
///   <item>every workflow whose job runs <c>./tools/get-fasm.ps1</c> restores
///         <c>tools/fasm-archive</c> before that step, under one key derived
///         from the script's bytes, and every copy is identical,</item>
///   <item>except <c>release.yml</c>, which restores nothing - the job that
///         attests what it builds must not take bytes out of a cache a pull
///         request could have written, which <c>ReleaseGateTests</c> refuses
///         from its own side.</item>
/// </list>
///
/// WHAT THIS DOES NOT COVER. It reads workflow text, not a run: whether a
/// scheduled job actually ends green and saves, and which scope a run reads,
/// are facts about GitHub's cache service that only its logs establish.
/// </summary>
public sealed class FasmArchiveCacheTests
{
    private const string Bootstrap = "run: ./tools/get-fasm.ps1";
    private const string CachePath = "path: tools/fasm-archive";
    private const string CacheKey = "key: fasm-archive-${{ hashFiles('tools/get-fasm.ps1') }}";
    private static readonly Regex CacheUses = new(@"^\s*uses:\s*actions/cache@[0-9a-f]{40}\b", RegexOptions.Compiled);

    [Fact]
    public void EveryBootstrappingWorkflowRestoresTheArchiveUnderOneKeyExceptTheRelease()
    {
        var dir = Path.Combine(Build.RepoRoot, ".github", "workflows");
        Assert.True(Directory.Exists(dir), "expected .github/workflows to exist");

        var offenders = new List<string>();
        var covered = 0;
        foreach (var path in Directory.GetFiles(dir, "*.yml").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);
            int bootstrap = Array.FindIndex(lines, l => l.Trim() == Bootstrap);
            if (bootstrap < 0)
            {
                continue;
            }

            int cache = Array.FindIndex(lines, l => CacheUses.IsMatch(l));
            if (name == "release.yml")
            {
                if (cache >= 0)
                {
                    offenders.Add($"{name}:{cache + 1}: the release gate restores a cache");
                }
                continue;
            }

            covered++;
            if (cache < 0 || cache > bootstrap)
            {
                offenders.Add($"{name}: no SHA-pinned actions/cache step before the bootstrap at line {bootstrap + 1}");
                continue;
            }

            // The step's `with:` block sits between the uses: line and the
            // bootstrap step; both scalars must be there, verbatim.
            var block = lines[(cache + 1)..bootstrap].Select(l => l.Trim()).ToArray();
            if (!block.Contains(CachePath))
            {
                offenders.Add($"{name}:{cache + 1}: the cache step does not restore `{CachePath}`");
            }
            if (!block.Contains(CacheKey))
            {
                offenders.Add($"{name}:{cache + 1}: the cache step's key is not `{CacheKey}`");
            }
        }

        Assert.True(covered >= 2, "expected at least the pull-request gate and a scheduled job to bootstrap the assembler; found " + covered);
        Assert.True(
            offenders.Count == 0,
            "the assembler-archive cache is keyed or pathed differently across workflows, or reaches the release gate (issue #344):\n  "
                + string.Join("\n  ", offenders));
    }
}
