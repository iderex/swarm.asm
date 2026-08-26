# Refuses a mutation report that publishes a kill which cannot have happened
# (issue #294). Reads a Stryker JSON report and exits non-zero naming every
# such verdict; the run that produced the report is the caller.
#
# WHAT CANNOT HAVE HAPPENED. A mutation that no program can distinguish from
# the original cannot fail a test, so a `Killed` status on it is not a weak
# verdict, it is an impossible one. For a `ulong` operand `>>` is already a
# logical shift and `>>>` is the same operation. The report recorded on #150
# (run 32667583739) publishes six such mutants as `Killed`, mutant ids 6, 11,
# 15, 23, 26 and 29, each by the same 274 tests, while mutant 19 - `v1 >>> 40`,
# the same mutation on the same type one line from `v2 >>> 40` - is published
# as `Survived` in the same run.
#
# WHAT THIS REFUSES AND WHERE IT STOPS. Only the case that can be decided
# from the report alone: the mutated span is `IDENT >> K`, the replacement is
# the same span with `>>>`, and IDENT is declared in the report's own `source`
# with an unsigned integral type. The type is resolved by reading that source
# rather than the tree, so the check does not drift against a report from an
# older commit. A left operand that is not a simple identifier is NOT decided
# - `((v3 >> 32) * speciesN) >>> 32` needs the type of an expression, which is
# not read here - and an identifier whose declaration this cannot find is not
# decided either. Undecided is printed, never refused: this guard refuses what
# it can prove impossible and nothing on suspicion.
#
# WHAT `killedBy` MEANS IN THIS SETUP, WHICH IS THE OTHER HALF OF #294 AND IS
# REPORTED RATHER THAN REFUSED. The schema says `killedBy` holds ids of tests
# declared in `testFiles`. Measured on the run above: 49 test files, every one
# of them declaring an empty `tests` array, against 352 distinct ids
# referenced by `coveredBy` and 281 by `killedBy`. So no id in the report
# resolves to a test name, and a reader using `killedBy` to find which
# assertion covers a line is reading something the report does not carry. The
# accounting below is printed on every run so that statement cannot go stale
# against a later report; it is not a refusal, because a report that resolves
# none of its ids is unreadable rather than untrue, and #294 asks for this
# half to be stated rather than gated.
param(
    [Parameter(Mandatory = $true)][string]$ReportPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ReportPath)) {
    throw "no mutation report at $ReportPath"
}

$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json

# C# integral keywords. `>>` and `>>>` differ only on a signed operand, so the
# first list is the one that makes a mutation equivalent by construction.
$unsignedTypes = @('ulong', 'uint', 'ushort', 'byte')
$signedTypes = @('long', 'int', 'short', 'sbyte')

# Finds the declared type of a simple local by reading the source the report
# carries. Handles a multi-declarator statement (`ulong v1 = a, v2 = b;`),
# which is the shape the oracle's draw uses. Returns $null when no single
# declaration is found, and the caller then leaves the mutant undecided.
function Resolve-DeclaredType {
    param([string]$Source, [string]$Name)

    foreach ($type in ($unsignedTypes + $signedTypes)) {
        $pattern = '(?m)^\s*' + $type + '\s+(?:\w+\s*=[^;,]*,\s*)*' + [regex]::Escape($Name) + '\s*='
        if ([regex]::IsMatch($Source, $pattern)) { return $type }
    }
    return $null
}

# Slices the mutated span out of the report's own source. Stryker's line and
# column are both 1-based.
function Get-Span {
    param([string[]]$Lines, $Location)

    $startLine = [int]$Location.start.line
    $endLine = [int]$Location.end.line
    $startCol = [int]$Location.start.column
    $endCol = [int]$Location.end.column

    if ($startLine -lt 1 -or $endLine -gt $Lines.Count) { return $null }

    if ($startLine -eq $endLine) {
        $line = $Lines[$startLine - 1]
        if ($startCol -lt 1 -or $endCol - 1 -gt $line.Length) { return $null }
        return $line.Substring($startCol - 1, $endCol - $startCol)
    }

    # A multi-line span is never the shape this check decides on.
    return $null
}

$impossible = @()
$undecided = 0
$decided = 0
$referenced = New-Object 'System.Collections.Generic.HashSet[string]'
$declared = New-Object 'System.Collections.Generic.HashSet[string]'

foreach ($fileProp in $report.files.PSObject.Properties) {
    $file = $fileProp.Value
    $lines = ($file.source -replace "`r", '') -split "`n"

    foreach ($mutant in $file.mutants) {
        foreach ($id in $mutant.coveredBy) { [void]$referenced.Add([string]$id) }
        foreach ($id in $mutant.killedBy) { [void]$referenced.Add([string]$id) }

        $span = Get-Span -Lines $lines -Location $mutant.location
        if ($null -eq $span) { continue }

        # `IDENT >> K` only. Anything else is a shape this check does not read.
        $match = [regex]::Match($span, '^\s*(?<id>[A-Za-z_][A-Za-z0-9_]*)\s*>>\s*(?<k>[0-9]+)\s*$')
        if (-not $match.Success) { continue }

        # The replacement has to be the same span with the shift widened, and
        # nothing else moved.
        if (($mutant.replacement -replace '>>>', '>>') -ne $span) { continue }
        if ($mutant.replacement -eq $span) { continue }

        $name = $match.Groups['id'].Value
        $type = Resolve-DeclaredType -Source $file.source -Name $name

        if ($null -eq $type) {
            $undecided++
            Write-Host "undecided: $($fileProp.Name) mutant $($mutant.id) `"$span`" to `"$($mutant.replacement)`" - no declaration of ``$name`` found in the report's source"
            continue
        }

        if ($unsignedTypes -notcontains $type) { continue }

        $decided++
        if ($mutant.status -eq 'Killed') {
            $impossible += "  $($fileProp.Name) line $($mutant.location.start.line) mutant $($mutant.id): `"$span`" to `"$($mutant.replacement)`" reported $($mutant.status) by $(@($mutant.killedBy).Count) tests, and ``$name`` is a $type so both operators are the same logical shift"
        }
    }
}

if ($report.testFiles) {
    foreach ($testProp in $report.testFiles.PSObject.Properties) {
        foreach ($test in $testProp.Value.tests) { [void]$declared.Add([string]$test.id) }
    }
}

$resolvable = 0
foreach ($id in $referenced) { if ($declared.Contains($id)) { $resolvable++ } }

Write-Host "attribution: $resolvable of $($referenced.Count) test ids referenced by coveredBy/killedBy resolve to a test declared in this report"
Write-Host "equivalent-by-construction shift mutants decided: $decided, undecided: $undecided"

if ($impossible.Count -gt 0) {
    Write-Host ''
    Write-Host "REFUSED: $($impossible.Count) verdict(s) that cannot be true"
    foreach ($line in $impossible) { Write-Host $line }
    Write-Host ''
    Write-Host 'A mutation no program can distinguish from the original cannot fail a test, so a kill reported for one is an artefact of the run rather than a property of the oracle. Do not publish this report as a measurement of the tests (#294).'
    exit 1
}

Write-Host 'no impossible verdict in this report'
exit 0
