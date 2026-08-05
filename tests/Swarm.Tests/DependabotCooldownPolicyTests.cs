using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the Dependabot cooldown policy (#142, #186).
///
/// THREAT MODEL, stated to match exactly what the code below holds. For the
/// shipped `.github/dependabot.yml` this asserts three things and nothing
/// else: the set of `(package-ecosystem, directory)` entries equals the policy
/// table; each entry carries a `cooldown:` block whose key set equals the
/// policy table's key set for that entry; and each of those keys carries
/// exactly the policy value. So a weakened hold, a deleted tier, a repeated
/// key, a duplicated entry, an undecided carve-out (`include`/`exclude`), an
/// extra tier and an ecosystem with no policy row all fail here.
///
/// What it does not do: it asserts nothing about any key outside a `cooldown:`
/// block, so schedules, groups and open-pull-requests limits are not held by
/// this test. It also fails a hold that is made *longer*, because it compares
/// values rather than a floor. That is deliberate: the tier values are a
/// recorded decision, and moving one should be an edit to this table and not a
/// silent drift past it.
/// </summary>
public sealed class DependabotCooldownPolicyTests
{
    // The policy, in one place. Adding an ecosystem to dependabot.yml means
    // adding its row here, which is the point: an entry with no row fails.
    private static readonly (string Ecosystem, string Directory, (string Key, int Days)[] Cooldown)[] Policy =
    [
        ("github-actions", "/", [("default-days", 7)]),
        ("nuget", "/tests/Swarm.Tests", [("default-days", 7), ("semver-major-days", 14)]),
        ("nuget", "/tests/Swarm.Bench", [("default-days", 7), ("semver-major-days", 14)]),
    ];

    [Fact]
    public void DependabotCooldownPolicyIsHeld()
    {
        var path = Path.Combine(Build.RepoRoot, ".github", "dependabot.yml");
        Assert.True(File.Exists(path), $"expected {path} to exist");

        var violations = Violations(File.ReadAllText(path));

        Assert.True(
            violations.Count == 0,
            "the shipped .github/dependabot.yml no longer holds the recorded cooldown "
                + "policy. Restore the values below in that file, or - if the policy itself "
                + "is meant to change - edit the Policy table in this test in the same "
                + "commit, so the recorded decision and the config move together:\n  "
                + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Non-vacuity control for the reader itself: an absent key must come back
    /// absent, not answer with the value the updater would have defaulted to.
    /// Without this, a reader that reports 7 for everything would satisfy every
    /// green case above while holding nothing.
    /// </summary>
    [Fact]
    public void ReaderReportsAnAbsentKeyAsAbsent()
    {
        var config = DependabotConfig.TryParse(
            Entries(nugetTests: "    cooldown:\n      default-days: 7\n"),
            out var error);

        Assert.NotNull(config);
        Assert.Equal("", error);

        var entry = config!.Updates.Single(e => e.Directory == "/tests/Swarm.Tests");
        Assert.NotNull(entry.Cooldown);
        Assert.False(entry.Cooldown!.ContainsKey("semver-major-days"));
        Assert.Equal("7", entry.Cooldown["default-days"]);
    }

    // Every valid spelling of the CORRECT policy. All of these must pass, or
    // the guard is refusing work it has no business refusing - which is what
    // the previous line scanner did.
    public static TheoryData<string, string> GreenSpellings() =>
        new()
        {
            { "quoted values", Entries(nugetTests: "    cooldown:\n      default-days: \"7\"\n      semver-major-days: \"14\"\n") },
            { "inline comments", Entries(nugetTests: "    cooldown:\n      default-days: 7 # the floor\n      semver-major-days: 14 # twice, widest blast radius\n") },
            { "quoted ecosystem name", Entries(nugetTests: "    cooldown:\n      default-days: 7\n      semver-major-days: 14\n", nugetTestsEcosystem: "\"nuget\"") },
            { "reordered keys", Entries(nugetTests: "    cooldown:\n      semver-major-days: 14\n      default-days: 7\n") },
            { "flow mapping", Entries(nugetTests: "    cooldown: { default-days: 7, semver-major-days: 14 }\n") },
            { "anchor and alias", Entries(nugetTests: "    cooldown: &tiers\n      default-days: 7\n      semver-major-days: 14\n", nugetBench: "    cooldown: *tiers\n") },
        };

    [Theory]
    [MemberData(nameof(GreenSpellings))]
    public void ValidSpellingsOfTheCorrectPolicyPass(string spelling, string yaml)
    {
        var violations = Violations(yaml);
        Assert.True(violations.Count == 0, $"'{spelling}' is the correct policy and must pass:\n  " + string.Join("\n  ", violations));
    }

    // Every way the policy can be lost. Each of these must fail, or the guard
    // is decorative.
    public static TheoryData<string, string> RedFixtures() =>
        new()
        {
            { "weakened default-days", Entries(nugetTests: "    cooldown:\n      default-days: 3\n      semver-major-days: 14\n") },
            { "weakened semver-major-days", Entries(nugetTests: "    cooldown:\n      default-days: 7\n      semver-major-days: 7\n") },
            { "deleted semver-major-days", Entries(nugetTests: "    cooldown:\n      default-days: 7\n") },
            { "repeated semver-major-days", Entries(nugetTests: "    cooldown:\n      default-days: 7\n      semver-major-days: 14\n      semver-major-days: 1\n") },
            { "repeated cooldown block", Entries(nugetTests: "    cooldown:\n      default-days: 7\n      semver-major-days: 14\n    cooldown:\n      default-days: 1\n") },
            { "carve-out inside the cooldown", Entries(nugetTests: "    cooldown:\n      default-days: 7\n      semver-major-days: 14\n      exclude:\n        - xunit.v3\n") },
            { "extra tier", Entries(nugetTests: "    cooldown:\n      default-days: 7\n      semver-major-days: 14\n      semver-minor-days: 1\n") },
            { "no cooldown block at all", Entries(nugetTests: "") },
            {
                // The decoy: the right numbers appear in the file, inside a
                // block scalar where they are text. A line scanner reads them
                // as configuration and passes while the real hold is 1 day.
                "block-scalar decoy over a weakened hold",
                Entries(nugetTests:
                    "    commit-message:\n      prefix: |\n        cooldown:\n          default-days: 7\n          semver-major-days: 14\n"
                    + "    cooldown:\n      default-days: 1\n      semver-major-days: 1\n")
            },
            {
                "duplicated ecosystem entry",
                Entries() + "  - package-ecosystem: nuget\n    directory: \"/tests/Swarm.Tests\"\n    cooldown:\n      default-days: 7\n      semver-major-days: 14\n"
            },
            {
                "third ecosystem with no policy row",
                Entries() + "  - package-ecosystem: npm\n    directory: \"/\"\n    cooldown:\n      default-days: 7\n"
            },
        };

    [Theory]
    [MemberData(nameof(RedFixtures))]
    public void EveryWayThePolicyCanBeLostFails(string fixture, string yaml) =>
        Assert.True(Violations(yaml).Count > 0, $"'{fixture}' must not pass the guard, and did");

    /// <summary>
    /// The whole assertion, in one place so the shipped file and every fixture
    /// are judged by identical code. Returns one line per violation; empty
    /// means the policy holds.
    /// </summary>
    private static IReadOnlyList<string> Violations(string yaml)
    {
        var config = DependabotConfig.TryParse(yaml, out var error);
        if (config is null)
        {
            return [$"the config could not be read: {error}"];
        }

        var found = new List<string>();
        var seen = new List<string>();
        foreach (var entry in config.Updates)
        {
            var id = $"{entry.Ecosystem} {entry.Directory}";
            if (seen.Contains(id))
            {
                found.Add($"{id}: a second `updates:` entry for the same ecosystem and directory - delete the duplicate");
                continue;
            }

            seen.Add(id);

            var row = Policy.FirstOrDefault(p => p.Ecosystem == entry.Ecosystem && p.Directory == entry.Directory);
            if (row.Ecosystem is null)
            {
                found.Add($"{id}: no policy row - add one to the Policy table in this test, with the cooldown this ecosystem is meant to carry");
                continue;
            }

            if (entry.Cooldown is null)
            {
                found.Add($"{id}: no `cooldown:` block - add {Expected(row.Cooldown)}");
                continue;
            }

            var extra = entry.Cooldown.Keys.Where(k => !row.Cooldown.Any(c => c.Key == k)).Order(StringComparer.Ordinal).ToArray();
            if (extra.Length > 0)
            {
                found.Add($"{id}: `cooldown:` carries undecided key(s) {string.Join(", ", extra)} - remove them, or decide them and add them to the Policy table here");
            }

            foreach (var (key, days) in row.Cooldown)
            {
                if (!entry.Cooldown.TryGetValue(key, out var raw))
                {
                    found.Add($"{id}: `cooldown:` is missing `{key}: {days}` - restore it");
                }
                else if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var actual))
                {
                    found.Add($"{id}: `{key}: {raw}` is not a whole number of days - write `{key}: {days}`");
                }
                else if (actual != days)
                {
                    found.Add($"{id}: `{key}` is {actual}, the recorded policy is {days} - restore it, or move the policy in this test's table in the same commit");
                }
            }
        }

        found.AddRange(
            Policy.Where(p => !seen.Contains($"{p.Ecosystem} {p.Directory}"))
                .Select(p => $"{p.Ecosystem} {p.Directory}: the policy has a row for it and dependabot.yml has no entry - restore the entry, or drop the row here if the manifest is gone"));

        return found;
    }

    private static string Expected((string Key, int Days)[] cooldown) =>
        string.Join(", ", cooldown.Select(c => $"`{c.Key}: {c.Days}`"));

    // A minimal but complete config carrying the correct policy, with each
    // entry's tail replaceable so a fixture differs from the correct spelling
    // in exactly one place.
    private static string Entries(
        string? githubActions = null,
        string? nugetTests = null,
        string? nugetBench = null,
        string nugetTestsEcosystem = "nuget") =>
        "version: 2\n"
        + "updates:\n"
        + "  - package-ecosystem: github-actions\n"
        + "    directory: \"/\"\n"
        + (githubActions ?? "    cooldown:\n      default-days: 7\n")
        + $"  - package-ecosystem: {nugetTestsEcosystem}\n"
        + "    directory: \"/tests/Swarm.Tests\"\n"
        + (nugetTests ?? "    cooldown:\n      default-days: 7\n      semver-major-days: 14\n")
        + "  - package-ecosystem: nuget\n"
        + "    directory: \"/tests/Swarm.Bench\"\n"
        + (nugetBench ?? "    cooldown:\n      default-days: 7\n      semver-major-days: 14\n");
}
