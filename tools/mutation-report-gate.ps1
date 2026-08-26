# Accounts for the mutation run's report before anything tries to publish it
# (issue #295). Called by .github/workflows/mutation.yml; runnable by hand
# against a directory, which is how its three branches were proved to bite.
#
# WHAT WENT WRONG. The publish step in that workflow carried `if: always()`
# with the reason that "a red run's partial report is the thing a reader
# needs", and `if-no-files-found: error`. Stryker writes its report when a run
# ENDS, so a run the job's timeout killed mid-mutation leaves the output
# directory empty, and the promise cannot be kept for that shape of failure:
# run 32805302524 was cancelled at 44m18s in the mutate step and its publish
# step then failed on the empty directory, leaving nought artifacts. The
# absence is inevitable there, so the workflow says so instead of failing an
# upload over it.
#
# WHAT STAYS A FAILURE. Mutating reporting success and writing no report
# anyway is a real defect, and this script throws on it. That is the case the
# `if-no-files-found: error` setting was protecting, and it keeps protecting
# it: the difference is that the reason for the absence is now read rather
# than assumed.
#
# FAIL CLOSED ON ITS OWN INPUT. An absent report with no conclusion to explain
# it is a throw, not a pass. A gate that reports "nothing to publish, and that
# is fine" because it could not see why the directory is empty is the
# false-clean this exists to remove.
[CmdletBinding()]
param(
    # The `conclusion` of the step that ran Stryker, as GitHub reports it:
    # success, failure, cancelled or skipped. Empty is refused.
    [string] $MutateConclusion,

    # The directory Stryker was told to write its reports into.
    [Parameter(Mandatory = $true)]
    [string] $ReportDir
)

$ErrorActionPreference = 'Stop'

$files = @()
if (Test-Path -LiteralPath $ReportDir) {
    $files = @(Get-ChildItem -LiteralPath $ReportDir -File -Recurse -ErrorAction SilentlyContinue)
}

if ($files.Count -gt 0) {
    Write-Host "report present: $($files.Count) file(s) under $ReportDir"
    $files | ForEach-Object { Write-Host "  $($_.Name) ($($_.Length) bytes)" }
    if ($env:GITHUB_OUTPUT) { "have=true" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding ascii }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($MutateConclusion)) {
    throw "no report under $ReportDir and no conclusion for the mutate step to explain it - refusing to report an unexplained absence as nothing to publish"
}

if ($MutateConclusion -eq 'success') {
    throw "the mutation run reported success and wrote no report under $ReportDir - the run is unreadable and this is a tool or harness failure, not an empty result"
}

Write-Host "no report under $ReportDir, and the mutate step did not succeed (conclusion: $MutateConclusion)."
Write-Host 'Stryker writes its report when a run ends, so a run killed mid-mutation leaves nothing behind: there is no partial report for this job to publish, and this run measured nothing about the tests around the oracle.'
Write-Host 'Re-dispatch the workflow to get a report. If the run was killed by the job timeout, that bound is the thing to read, not this step.'
if ($env:GITHUB_OUTPUT) { "have=false" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding ascii }
exit 0
