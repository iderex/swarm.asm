using System.Text.Json;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// Lock-in for the two properties that decide whether the mutation run (#150)
/// measures the oracle or measures nothing. Both failures are silent: they
/// produce a green run, a report and a percentage, and the percentage is an
/// artefact of the setup rather than a statement about the reference.
///
/// FIRST, THE ORACLE LIVES OUTSIDE THE TEST ASSEMBLY. Stryker mutates a source
/// project the test project references and never the test assembly itself.
/// With <see cref="TestOracle"/> compiled into Swarm.Tests the tool refused to
/// analyse at all, which at least fails loudly; pointed at the test project it
/// would mutate the assertions alongside the reference, and a mutated
/// assertion cannot be killed by the suite containing it, so those mutants
/// survive by construction. A score that cannot go up is worse than no score.
///
/// SECOND, THE RUNNER IS MTP. Stryker's default runner is VSTest. Measured on
/// a two-method probe outside this repository, Stryker 4.16.0 driving an
/// xunit.v3 3.2.2 suite through the VSTest adapter reported nine mutants, zero
/// killed and no errors, where the same three tests under xunit v2 killed six.
/// The MTP runner on the same tool reported five of eight killed. So a config
/// that loses `"test-runner": "MTP"` does not fail; it reports every mutant as
/// a survivor, and the sibling that triages survivors (#151) inherits a list
/// on which every entry is an artefact of the runner.
/// </summary>
public sealed class MutationSubjectTests
{
    private const string OracleAssembly = "Swarm.Oracle";

    [Fact]
    public void OracleIsNotCompiledIntoTheTestAssembly()
    {
        var oracle = typeof(TestOracle).Assembly;
        var harness = typeof(MutationSubjectTests).Assembly;

        Assert.True(
            oracle != harness,
            $"TestOracle is compiled into the test assembly ({harness.GetName().Name}). Stryker "
                + "mutates a project the test project references, never the test assembly, so the "
                + "mutation run (#150) has nothing to mutate and any score it reports is about the "
                + "layout rather than about the reference. Keep TestOracle.cs in tests/Swarm.Oracle.");

        Assert.Equal(OracleAssembly, oracle.GetName().Name);
    }

    [Fact]
    public void MutationConfigNamesTheOracleAndTheMtpRunner()
    {
        var path = Path.Combine(Build.RepoRoot, "tests", "Swarm.Tests", "stryker-config.json");
        Assert.True(File.Exists(path), $"expected {path} to exist");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(
            doc.RootElement.TryGetProperty("stryker-config", out var config),
            "stryker-config.json has no `stryker-config` object; Stryker reads nothing else in it");

        Assert.True(config.TryGetProperty("project", out var project), "`project:` is missing");
        Assert.Equal($"{OracleAssembly}.csproj", project.GetString());

        Assert.True(
            config.TryGetProperty("test-runner", out var runner),
            "`test-runner:` is missing, so Stryker falls back to VSTest. Measured: under VSTest "
                + "this harness's framework reports every mutant as a survivor and the run stays "
                + "green while doing it. Set it back to MTP.");
        Assert.Equal("MTP", runner.GetString());
    }
}
