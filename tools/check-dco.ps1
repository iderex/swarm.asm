# check-dco.ps1 - refuse a commit range in which any non-merge commit lacks a
# Signed-off-by trailer matching its author (issue #143).
#
# CONTRIBUTING.md states "Every commit must be signed off". Until this script
# existed the rule was enforced by whoever happened to look, which is enforced by
# nobody: four unsigned commits on PR #140 survived three review rounds. The
# repair after a merge is a force-push, so the check belongs before the merge.
#
# FAIL-CLOSED ON ITS OWN INPUTS. A sign-off checker that reports clean because it
# could not see the commits is worse than no checker: it turns an unreadable
# input into a green tick. So a missing repository, an unresolvable ref, a git
# invocation that fails, a shallow clone (which can hide commits inside the
# range) and an empty range are all refusals, not passes. The empty range is the
# one that looks harmless and is not - a misspelled base resolves to nothing and
# would otherwise read as "no offending commits found".
#
# Exit codes: 0 = every non-merge commit in the range is signed off by its
# author; 1 = a refusal, with the reason on stderr and any offending commits
# listed on stdout.

[CmdletBinding()]
param(
    # The exclusive lower bound of the range, and its inclusive upper bound.
    [Parameter(Mandatory = $true)][string]$Base,
    [Parameter(Mandatory = $true)][string]$Head,
    # The repository to read. Defaults to the one this script lives in.
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

# Identities exempt from the rule. The DCO binds the people and agents who write
# code here; it does not bind GitHub's own apps, which author and sign off under
# two different addresses and so would red every pull request they open.
#
# ONE NAMED IDENTITY, NEVER A PATTERN. An allowlist entry reading `*[bot]` is a
# door: any account whose name ends that way walks through it. A single exact
# address is a documented exception a reader can weigh.
$ExemptAuthorEmails = @(
    '49699333+dependabot[bot]@users.noreply.github.com'
)

# The record separator git writes for %x1f. No git identity or commit message can
# contain it, which is what makes the three fields unambiguous.
$Unit = [string][char]0x1F

function Write-Refusal {
    param([string]$Reason)
    [Console]::Error.WriteLine("DCO check refused: $Reason")
}

# Runs git and returns its output lines. A non-zero exit is a refusal rather than
# an exception, so every failure leaves this script through one door.
function Invoke-Git {
    param([string[]]$GitArgs)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & git -C $RepoRoot @GitArgs 2>&1 | ForEach-Object { $_.ToString() }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($code -ne 0) {
        Write-Refusal "git $($GitArgs -join ' ') exited with $code - $($output -join ' ')"
        exit 1
    }
    return @($output)
}

if (-not (Test-Path -LiteralPath $RepoRoot)) {
    Write-Refusal "the repository root '$RepoRoot' does not exist"
    exit 1
}

# A shallow clone can omit commits that are inside the range, so the walk below
# would report on a subset while looking as though it had covered the whole.
$shallow = ((Invoke-Git @('rev-parse', '--is-shallow-repository')) -join '').Trim()
if ($shallow -eq 'true') {
    Write-Refusal 'the clone is shallow, so commits inside the range may be missing. Check out with fetch-depth: 0.'
    exit 1
}

# Resolve both ends before walking, so a misspelled ref is refused here rather
# than becoming an empty range further down.
$baseSha = ((Invoke-Git @('rev-parse', '--verify', "$Base^{commit}")) -join '').Trim()
$headSha = ((Invoke-Git @('rev-parse', '--verify', "$Head^{commit}")) -join '').Trim()

$commits = @(
    Invoke-Git @('rev-list', '--no-merges', "$baseSha..$headSha") |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -ne '' }
)

if ($commits.Count -eq 0) {
    Write-Refusal "the range $Base..$Head holds no non-merge commits. An empty range is not a clean range - it is an input this check cannot judge."
    exit 1
}

$offenders = @()

foreach ($sha in $commits) {
    $record = (Invoke-Git @('show', '--no-patch', "--format=%an$($Unit)%ae$($Unit)%B", $sha)) -join "`n"
    $parts = $record -split $Unit, 3
    if ($parts.Count -lt 3) {
        Write-Refusal "could not read the author and message of $sha"
        exit 1
    }

    $authorName = $parts[0].Trim()
    $authorEmail = $parts[1].Trim()
    $message = $parts[2]

    if ($ExemptAuthorEmails -contains $authorEmail) {
        continue
    }

    $signedEmails = @(
        [regex]::Matches($message, '(?im)^[ \t]*Signed-off-by:[ \t]*.*<([^>]+)>[ \t]*$') |
        ForEach-Object { $_.Groups[1].Value.Trim() }
    )

    $short = $sha.Substring(0, [Math]::Min(12, $sha.Length))
    if ($signedEmails.Count -eq 0) {
        $offenders += "$short  $authorName <$authorEmail>  no Signed-off-by trailer"
        continue
    }

    $matched = @($signedEmails | Where-Object { $_ -ieq $authorEmail })
    if ($matched.Count -eq 0) {
        $offenders += "$short  $authorName <$authorEmail>  signed off as $($signedEmails -join ', ')"
    }
}

if ($offenders.Count -gt 0) {
    Write-Output "Commits without a Signed-off-by matching their author ($($offenders.Count) of $($commits.Count)):"
    $offenders | ForEach-Object { Write-Output "  $_" }
    Write-Output ''
    Write-Output 'Add the trailer with `git commit -s`, or across a branch with `git rebase --signoff <base>`.'
    Write-Refusal "$($offenders.Count) commit(s) are not signed off by their author"
    exit 1
}

Write-Output "DCO: $($commits.Count) non-merge commit(s) in $Base..$Head are signed off by their authors."
exit 0
