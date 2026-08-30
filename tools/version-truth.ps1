# version-truth.ps1 - refuses a version tag that disagrees with the changelog
# or with the binary that was just built from it (#181).
#
# WHAT IT COMPARES, and the reason each leg exists.
#
#   1. The tag's SHAPE. docs/RELEASE-POLICY.md fixes three-part `X.Y.Z`
#      versions, so `v1.2`, `v1.2.3-rc1` and `v01.2.3` are not versions this
#      project ships and are refused before anything is compared against them.
#
#   2. The tag against CHANGELOG.md. The policy's step 3 makes the changelog
#      bump part of the release ritual: the entries move under a heading
#      carrying the new version and today's date. This leg is what covers Y
#      and Z - both are strings in the tree and both are read here.
#
#   3. The tag's MAJOR against the ABI version the built artifact reports.
#      The policy's digit table maps `X` to a break in the kernel ABI, the
#      P/Invoke seam, or the preset/config format, so a `v2.x.y` tag on a
#      binary still reporting ABI 1 is a disagreement between the tag and the
#      thing being tagged.
#
# WHAT IT DOES NOT COMPARE, stated because the check reads stronger than it is.
#
#   - The image carries no product version. `SWARM_ABI_VERSION` is the only
#     version constant in it (src/kernel/abi.inc), exported as
#     `swarm_version`, so Y and Z have nothing on the binary side to be read
#     against and are checked against the changelog alone.
#   - Leg 3 is one-directional in a way worth naming: the digit table also
#     bumps `X` on a preset/config-format break, and the preset grammar
#     carries its own version constant that this check never reads. A
#     format break that bumped `X` without bumping `SWARM_ABI_VERSION` would
#     be refused here for a reason that is not the real one, and a format
#     break that bumped neither would pass. Widening leg 3 to the grammar
#     version means deciding that the two constants move together, which is a
#     rule about the seam rather than about this script.
#   - Nothing here reads the `Unreleased` section. A tag whose entries were
#     copied under the new heading instead of moved passes.
#   - The ABI version is an input. Reading it out of the artifact is the
#     caller's job (.github/workflows/release.yml does it by calling the
#     built DLL's `swarm_version` export); a caller that reads the wrong
#     thing gets a check over the wrong number.
#
# Exit 0 only when every leg agrees. Any disagreement prints one REFUSED line
# per leg that failed - all of them, not the first - and exits 1.

[CmdletBinding()]
param(
    # The version tag being released, with its leading `v`: `v1.2.3`.
    [Parameter(Mandatory = $true)][string]$Tag,

    # The changelog to read the version heading out of.
    [Parameter(Mandatory = $true)][string]$ChangelogPath,

    # The ABI version the built artifact reports, read by the caller.
    [Parameter(Mandatory = $true)][int]$AbiVersion
)

$ErrorActionPreference = 'Stop'

$refusals = New-Object System.Collections.Generic.List[string]
function Deny([string]$leg, [string]$why) { $refusals.Add("REFUSED ($leg): $why") }

# Leg 1 - the tag's shape. Leading zeros are refused because `v01.2.3` and
# `v1.2.3` would otherwise be two tags for one version.
$shape = [regex]'^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
$m = $shape.Match($Tag)
if (-not $m.Success) {
    Deny 'tag shape' "'$Tag' is not a three-part version tag of the form vX.Y.Z (docs/RELEASE-POLICY.md)"
    # Nothing below can be compared against a version that was never parsed.
    $refusals | ForEach-Object { Write-Host $_ }
    exit 1
}

$major = [int]$m.Groups[1].Value
$version = "$($m.Groups[1].Value).$($m.Groups[2].Value).$($m.Groups[3].Value)"

# Leg 2 - the changelog heading for exactly this version, carrying a date.
if (-not (Test-Path -LiteralPath $ChangelogPath)) {
    Deny 'changelog' "no changelog at '$ChangelogPath'"
}
else {
    $lines = [IO.File]::ReadAllLines($ChangelogPath)
    $escaped = [regex]::Escape($version)
    $headings = @($lines | Where-Object { $_ -match "^##\s+$escaped(\s|$)" })

    if ($headings.Count -eq 0) {
        Deny 'changelog' "tag $Tag has no '## $version' section in $ChangelogPath - the policy's step 3 moves the entries under the new version heading before the tag is pushed"
    }
    elseif ($headings.Count -gt 1) {
        Deny 'changelog' "$ChangelogPath carries $($headings.Count) '## $version' headings - one version, one section"
    }
    else {
        $heading = $headings[0]
        $dated = [regex]::Match($heading, "^##\s+$escaped\s+-\s+(\d{4}-\d{2}-\d{2})\s*$")
        if (-not $dated.Success) {
            Deny 'changelog' "the '## $version' heading reads '$heading' - expected '## $version - YYYY-MM-DD'"
        }
        else {
            $parsed = [datetime]::MinValue
            $ok = [datetime]::TryParseExact(
                $dated.Groups[1].Value, 'yyyy-MM-dd',
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None, [ref]$parsed)
            if (-not $ok) {
                Deny 'changelog' "the '## $version' heading carries '$($dated.Groups[1].Value)', which is not a calendar date"
            }
        }
    }
}

# Leg 3 - the tag's major against the ABI version the artifact reports.
if ($major -ne $AbiVersion) {
    Deny 'binary version' "tag $Tag has major $major and the built artifact reports SWARM_ABI_VERSION = $AbiVersion - docs/RELEASE-POLICY.md maps X to an ABI, seam or preset-format break"
}

if ($refusals.Count -gt 0) {
    $refusals | ForEach-Object { Write-Host $_ }
    Write-Host "version truth: $($refusals.Count) disagreement(s) between $Tag, $ChangelogPath and the built artifact."
    exit 1
}

Write-Host "version truth: $Tag agrees with the '## $version' section of $ChangelogPath and with SWARM_ABI_VERSION = $AbiVersion."
exit 0
