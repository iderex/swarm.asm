# Mutation-tests the reference oracle (issue #150). Opt-in: nothing on the
# pull-request gate runs this, and nothing should. A mutation run drives the
# whole harness once per surviving mutant, which is minutes rather than the
# seconds a gate may spend.
#
# WHAT IS MUTATED AND WHY IT IS ITS OWN PROJECT. Stryker mutates a source
# project that the test project REFERENCES; it never mutates the test assembly.
# tests/Swarm.Oracle exists for that reason: pointed at the test project
# instead, the tool would mutate the assertions alongside the reference, and a
# mutated assertion cannot be killed by the suite that contains it, so those
# mutants survive by construction and the score says nothing about the oracle.
#
# WHY MTP AND NOT VSTEST. The harness runs on Microsoft.Testing.Platform
# deliberately: VSTest spawns a freshly built testhost.exe, which Device Guard
# and Smart App Control block on the development machine. Stryker's default
# runner is VSTest, and its MTP runner is what makes this run possible here at
# all. That runner is a preview in 4.16.0 and prints so on every run; read the
# warning rather than filtering it out.
#
# The score is not recorded in this tree. It is a property of the oracle on the
# day it ran, so it lives in the run's report and in the issue that asked for
# it, not in a document that would drift away from it.
$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent $PSScriptRoot
Push-Location $Repo
try {
    # The kernel has to load, or the tests that carry the oracle skip and the
    # mutants they would have killed survive for the wrong reason.
    $env:SWARM_REQUIRE_NATIVE = '1'

    # Both of these are PowerShell scripts, not native executables: they set
    # their own $ErrorActionPreference to Stop and throw, and a $LASTEXITCODE
    # test against them reads whatever native command ran last, which on a
    # fresh session is nothing at all.
    Write-Host '== bootstrapping the pinned assembler =='
    & (Join-Path $PSScriptRoot 'get-fasm.ps1')

    Write-Host '== assembling =='
    & (Join-Path $Repo 'build.ps1')

    Write-Host '== restoring the pinned mutation tool =='
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed (exit $LASTEXITCODE)" }

    $out = Join-Path $Repo 'build/mutation'
    Write-Host '== mutating tests/Swarm.Oracle =='
    Push-Location (Join-Path $Repo 'tests/Swarm.Tests')
    try {
        dotnet dotnet-stryker --output $out
        if ($LASTEXITCODE -ne 0) { throw "dotnet-stryker failed (exit $LASTEXITCODE)" }
    } finally {
        Pop-Location
    }

    Write-Host ''
    Write-Host "report: $out/reports/mutation-report.html"
    Write-Host "        $out/reports/mutation-report.json"
} finally {
    Pop-Location
}
