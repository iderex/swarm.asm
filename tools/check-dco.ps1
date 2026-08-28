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
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    # The login of the account that OPENED the pull request, as GitHub reports it.
    # See the exemption below: without it there is no exemption at all.
    [string]$PullRequestAuthor = ''
)

# The one identity exempt from the rule, and the login that has to have opened
# the pull request for the exemption to exist at all. The DCO binds the people
# and agents who write code here; it does not bind GitHub's own apps, which
# author and sign off under two different addresses and so would red every pull
# request they open.
#
# BOTH HALVES ARE REQUIRED, AND THE SECOND IS WHY. A commit's author email is a
# field its author types: anybody can write Dependabot's address into their own
# unsigned commit, and an exemption keyed on that field alone hands the gate to
# whoever spells the address. The opening account comes from GitHub's event
# payload rather than from the commit, so a pull request opened by a person
# carries no exemption at all, whatever its commits claim to be.
#
# ONE NAMED IDENTITY, NEVER A PATTERN, AND MATCHED CASE-SENSITIVELY. An
# allowlist entry reading `*[bot]` is a door: any account whose name ends that
# way walks through it. PowerShell's `-contains` is case-INSENSITIVE for
# strings, which would have turned one address into its whole casing family, so
# the comparison below is `-ceq`.
$ExemptAuthorEmail = '49699333+dependabot[bot]@users.noreply.github.com'
$ExemptOpenedBy = 'dependabot[bot]'

# Anything unexpected below is a terminating error, so it leaves through a
# non-zero exit rather than continuing past the failure with a stale variable.
# Invoke-Git relaxes this around the git call alone, where git's stderr would
# otherwise be raised as an error on a run that succeeded.
$ErrorActionPreference = 'Stop'

# A resolved commit id, and nothing else. git's output reaches this script with
# stderr merged into it, so a line git decided to emit for its own reasons would
# otherwise be carried forward as if it were the answer to the question asked.
$Sha1 = '^[0-9a-f]{40}$'

function Write-Refusal {
    param([string]$Reason)
    [Console]::Error.WriteLine("DCO check refused: $Reason")
}

# git is resolved once, up front, and its absence is a refusal.
#
# THIS IS THE FALSE-CLEAN THIS SCRIPT IS MOST EXPOSED TO, and it was measured
# rather than supposed: a call to a command that is not on PATH raises a
# NON-terminating error, and `pwsh -File` on a script that only produced one
# exits 0. So a runner without git would have taken this check green while it
# read nothing at all. Resolving the executable turns that into an exit 1 at the
# first line that needs it.
$GitExe = (Get-Command git -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1)
if (-not $GitExe) {
    Write-Refusal 'git is not on PATH, so no commit in the range can be read'
    exit 1
}

# Runs git and returns its output lines. A non-zero exit is a refusal rather than
# an exception, so every failure leaves this script through one door.
function Invoke-Git {
    param([string[]]$GitArgs)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & $GitExe.Source -C $RepoRoot @GitArgs 2>&1 | ForEach-Object { $_.ToString() }
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
#
# THE TEST IS FOR `false`, NOT AGAINST `true`, and the difference is the whole
# point. A refusal that fires only on an exact literal passes everything else,
# including a probe whose answer arrived with another line of git's output
# joined onto it - which is a real shape here, because stderr is merged into
# what Invoke-Git returns.
$shallow = ((Invoke-Git @('rev-parse', '--is-shallow-repository')) -join '').Trim()
if ($shallow -cne 'false') {
    Write-Refusal "the clone is not known to be complete (git answered '$shallow'), so commits inside the range may be missing. Check out with fetch-depth: 0."
    exit 1
}

# Resolve both ends before walking, so a misspelled ref is refused here rather
# than becoming an empty range further down. The answer has to be a commit id
# and nothing else, for the reason given at $Sha1.
$baseSha = ((Invoke-Git @('rev-parse', '--verify', "$Base^{commit}")) -join '').Trim()
$headSha = ((Invoke-Git @('rev-parse', '--verify', "$Head^{commit}")) -join '').Trim()
foreach ($pair in @(@('base', $baseSha), @('head', $headSha))) {
    if ($pair[1] -notmatch $Sha1) {
        Write-Refusal "resolving the $($pair[0]) did not yield a commit id: '$($pair[1])'"
        exit 1
    }
}

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
    if ($sha -notmatch $Sha1) {
        Write-Refusal "rev-list returned something that is not a commit id: '$sha'"
        exit 1
    }

    # ONE FIELD PER CALL, NEVER ONE FORMAT STRING SPLIT ON A SEPARATOR. The
    # earlier form asked for `%an<US>%ae<US>%B` and split on U+001F, on the claim
    # that no git identity can contain that byte. git stores it verbatim:
    #
    #     git -c user.name=$'Foo\x1fvictim@example.invalid' commit ...
    #
    # shifts every field one place left, so the address the check compares
    # against comes out of the author's own name and any trailer they like
    # matches it. That is a signed-off verdict on an arbitrary unsigned commit,
    # which is the failure this whole script exists to make impossible. A field
    # read on its own cannot be shifted by anything inside another field.
    $authorName = ((Invoke-Git @('show', '--no-patch', '--format=%an', $sha)) -join ' ').Trim()
    $authorEmail = ((Invoke-Git @('show', '--no-patch', '--format=%ae', $sha)) -join ' ').Trim()
    $message = (Invoke-Git @('show', '--no-patch', '--format=%B', $sha)) -join "`n"

    if ($PullRequestAuthor -ceq $ExemptOpenedBy -and $authorEmail -ceq $ExemptAuthorEmail) {
        continue
    }

    # THE TRAILER IS A TRAILER, NOT A LINE THAT LOOKS LIKE ONE. Anchored at
    # column zero, so a `Signed-off-by:` indented inside a fenced block or an
    # example in the body does not certify anything; and the name may not itself
    # contain angle brackets, so a line carrying two addresses cannot have the
    # second one read as the certifying one while the first is what a human sees.
    $signedEmails = @(
        [regex]::Matches($message, '(?m)^Signed-off-by:[ \t]*[^<>]*<([^<>]+)>[ \t]*$') |
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
