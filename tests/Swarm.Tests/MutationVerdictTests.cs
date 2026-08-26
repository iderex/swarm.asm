using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Proof that <c>tools/check-mutation-verdicts.ps1</c> bites, for the reason
/// it names (issue #294).
///
/// WHAT IT IS FOR. The mutation run publishes kills that cannot have happened.
/// The report recorded on #150 (run 32667583739) reports six
/// <c>&gt;&gt;</c>-to-<c>&gt;&gt;&gt;</c> mutants on <c>ulong</c> operands as
/// Killed, each by the same 274 tests, while one more of exactly that shape a
/// line away is reported Survived in the same run. For an unsigned operand the
/// two operators are the same logical shift, so no program can distinguish
/// them and no test can fail on one. A verdict that cannot be true is refused
/// here rather than published.
///
/// THE FIXTURE IS THE CASE THAT CANNOT BE ARGUED WITH. Every report below is
/// built in this file rather than committed, so the assertion sits next to the
/// bytes it is about, and the one that has to fire is a hand-checkable
/// equivalent mutant reported as Killed.
///
/// THE NEAR-MISSES ARE THE PART WORTH READING. A guard that refused every
/// <c>&gt;&gt;&gt;</c> mutation would pass the first test and be wrong: on a
/// SIGNED operand the widened shift is a real mutation and a kill on it is
/// ordinary. So the same fixture is run with the operand declared
/// <c>long</c>, with the status Survived, and with a replacement that is not a
/// widened shift at all; none of the three may be refused.
///
/// WHAT THIS DOES NOT COVER. It runs the check against fixtures, never against
/// a mutation run, so nothing here says the hosted job invokes it. That
/// invocation is asserted for the local route by
/// <see cref="TheOptInMutationScriptInvokesTheVerdictCheck"/>, and the hosted
/// route's step is not added, because <c>.github/workflows/mutation.yml</c> is
/// carried by an open pull request for a different issue and two edits to one
/// file collide. The type resolution reads the report's own source with a
/// regular expression and decides only a simple identifier; a left operand
/// that is an expression is left undecided by construction, which is why the
/// recorded report's mutant 26 is neither refused nor counted.
/// </summary>
public sealed class MutationVerdictTests
{
    private const string CheckScript = "check-mutation-verdicts.ps1";

    private const string TestId = "0f1e2d3c";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);

    /// <summary>A source small enough to read whole, carrying the exact shape
    /// the oracle's <c>SplitMix64</c> carries: an integral local shifted right
    /// by a constant.</summary>
    private static string Source(string type) =>
        string.Join(
            "\n",
            "static " + type + " Mix(" + type + " seed)",
            "{",
            "    " + type + " z = seed;",
            "    z = z ^ (z >> 30);",
            "    return z;",
            "}");

    [Fact]
    public void AnEquivalentShiftReportedKilledIsRefused()
    {
        var (exit, output) = RunCheck(Report("ulong", "z >>> 30", "Killed"));

        Assert.True(
            exit != 0,
            "a `z >>> 30` mutation on a ulong is the same logical shift as `z >> 30`, so no "
                + "test can fail on it and a Killed verdict for it cannot be true. The check "
                + $"published it anyway (exit {exit}):{Environment.NewLine}{output}");

        Assert.Contains("REFUSED", output, StringComparison.Ordinal);
        Assert.Contains("ulong", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same mutant, Survived. That is the verdict the recorded run gives
    /// the twin of every mutant above, and it is the correct one: an
    /// equivalent mutant surviving is what equivalence looks like. A check
    /// that refused on the mutation rather than on the verdict fails here.
    /// </summary>
    [Fact]
    public void TheSameEquivalentMutantReportedSurvivedIsNotRefused()
    {
        var (exit, output) = RunCheck(Report("ulong", "z >>> 30", "Survived"));

        Assert.True(
            exit == 0,
            "an equivalent mutant reported Survived is the run behaving correctly; refusing it "
                + $"would refuse the only honest verdict this mutation has (exit {exit}):"
                + $"{Environment.NewLine}{output}");
    }

    /// <summary>
    /// The one-character mistake somebody will actually make: taking every
    /// <c>&gt;&gt;&gt;</c> mutation for an equivalent one. On a SIGNED operand
    /// <c>&gt;&gt;</c> is an arithmetic shift and <c>&gt;&gt;&gt;</c> is not,
    /// so this mutant is genuinely killable and its kill is ordinary.
    /// </summary>
    [Fact]
    public void TheSameShiftOnASignedOperandIsNotRefused()
    {
        var (exit, output) = RunCheck(Report("long", "z >>> 30", "Killed"));

        Assert.True(
            exit == 0,
            "on a signed operand `>>` is an arithmetic shift and `>>>` is not, so this mutant "
                + $"is killable and its kill is not impossible (exit {exit}):"
                + $"{Environment.NewLine}{output}");
    }

    /// <summary>A mutation that changes the operator rather than widening the
    /// shift is a real mutation on any operand type.</summary>
    [Fact]
    public void AKillOnAMutationThatIsNotAWidenedShiftIsNotRefused()
    {
        var (exit, output) = RunCheck(Report("ulong", "z << 30", "Killed"));

        Assert.True(
            exit == 0,
            $"`z << 30` is a different operation from `z >> 30` on any type (exit {exit}):"
                + $"{Environment.NewLine}{output}");
    }

    /// <summary>
    /// The other half of #294: whether <c>killedBy</c> means anything here. The
    /// schema says its ids name tests declared in <c>testFiles</c>; the
    /// recorded run declares none of them, in 49 test files. The check prints
    /// the share that resolves on every run so that statement cannot go stale
    /// against a later report, and it prints rather than refuses, because a
    /// report that resolves none of its ids is unreadable rather than untrue.
    /// </summary>
    [Fact]
    public void TheAttributionAccountingReportsIdsThatResolveToNoTest()
    {
        var (withoutExit, withoutRegistry) = RunCheck(Report("ulong", "z >>> 30", "Survived"));
        Assert.Equal(0, withoutExit);
        Assert.Contains(
            "attribution: 0 of 1 test ids",
            withoutRegistry,
            StringComparison.Ordinal);

        var (withExit, withRegistry) = RunCheck(
            Report("ulong", "z >>> 30", "Survived", declareTests: true));
        Assert.Equal(0, withExit);
        Assert.Contains(
            "attribution: 1 of 1 test ids",
            withRegistry,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The check has to be reached by the run, or it refuses nothing that is
    /// ever published. Removing the invocation is one deletion and nothing
    /// else in the tree would notice.
    /// </summary>
    [Fact]
    public void TheOptInMutationScriptInvokesTheVerdictCheck()
    {
        var path = Path.Combine(Build.RepoRoot, "tools", "mutation-test.ps1");
        Assert.True(File.Exists(path), $"expected {path} to exist");

        var invoked = File.ReadAllLines(path)
            .Select(l => l.TrimStart())
            .Where(l => !l.StartsWith('#'))
            .Any(l => l.Contains(CheckScript, StringComparison.Ordinal));

        Assert.True(
            invoked,
            $"tools/mutation-test.ps1 does not invoke tools/{CheckScript}, so a local run "
                + "publishes its report without anything reading the verdicts in it (#294)");
    }

    /// <summary>
    /// A Stryker JSON report carrying one file, one mutant and one test id,
    /// built so the mutant's location is computed from the source rather than
    /// written down beside it.
    /// </summary>
    private static string Report(
        string type,
        string replacement,
        string status,
        bool declareTests = false)
    {
        var source = Source(type);
        var lines = source.Split('\n');

        const string original = "z >> 30";
        var lineIndex = Array.FindIndex(lines, l => l.Contains(original, StringComparison.Ordinal));
        Assert.True(lineIndex >= 0, "the fixture source lost the expression the mutant is about");
        var column = lines[lineIndex].IndexOf(original, StringComparison.Ordinal);

        var testFile = new JsonObject
        {
            ["language"] = "cs",
            ["source"] = "// the assertions are not read by the check",
            ["tests"] = declareTests
                ? new JsonArray(new JsonObject { ["id"] = TestId, ["name"] = "SomeTest" })
                : new JsonArray(),
        };

        var mutant = new JsonObject
        {
            ["id"] = "1",
            ["mutatorName"] = "Bitwise mutation",
            ["replacement"] = replacement,
            ["location"] = new JsonObject
            {
                ["start"] = new JsonObject
                {
                    ["line"] = lineIndex + 1,
                    ["column"] = column + 1,
                },
                ["end"] = new JsonObject
                {
                    ["line"] = lineIndex + 1,
                    ["column"] = column + 1 + original.Length,
                },
            },
            ["status"] = status,
            ["static"] = false,
            ["coveredBy"] = new JsonArray(TestId),
            ["killedBy"] = status == "Killed" ? new JsonArray(TestId) : new JsonArray(),
        };

        var report = new JsonObject
        {
            ["schemaVersion"] = "2",
            ["thresholds"] = new JsonObject { ["high"] = 80, ["low"] = 60 },
            ["files"] = new JsonObject
            {
                ["Fixture.cs"] = new JsonObject
                {
                    ["language"] = "cs",
                    ["source"] = source,
                    ["mutants"] = new JsonArray(mutant),
                },
            },
            ["testFiles"] = new JsonObject { ["FixtureTests.cs"] = testFile },
        };

        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static (int Exit, string Output) RunCheck(string reportJson)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "swarm-mutation-verdict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var reportPath = Path.Combine(dir, "mutation-report.json");
            File.WriteAllText(reportPath, reportJson);

            var psi = new ProcessStartInfo("powershell")
            {
                WorkingDirectory = Build.RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(Path.Combine(Build.RepoRoot, "tools", CheckScript));
            psi.ArgumentList.Add("-ReportPath");
            psi.ArgumentList.Add(reportPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException(
                    "could not start powershell to run the verdict check");

            // Both pipes drained asynchronously: a synchronous read on one
            // deadlocks against a child blocked writing the other.
            var text = new StringBuilder();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (text) { text.AppendLine(e.Data); }
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (text) { text.AppendLine(e.Data); }
                }
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit((int)CheckTimeout.TotalMilliseconds))
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit();
                throw new InvalidOperationException(
                    $"{CheckScript} did not finish within {CheckTimeout.TotalMinutes} minutes");
            }
            proc.WaitForExit(); // flush the async readers after the bounded wait

            lock (text)
            {
                return (proc.ExitCode, text.ToString());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
