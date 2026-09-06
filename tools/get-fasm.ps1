# Bootstraps the pinned FASM toolchain into tools/fasm (idempotent).
#
# fasm produces PE64 directly - no linker, no SDK: this ~1 MB archive is the
# entire build toolchain. Fail-closed: the archive must match the pinned
# SHA-256 exactly or nothing is unpacked, wherever it came from.
#
# The verified archive is kept in tools/fasm-archive/ after unpacking. CI
# caches that directory on a key derived from this script, so a hit restores
# the archive and the origin server is never contacted (#344). A restored
# archive goes through the same hash comparison as a downloaded one: a cache
# entry is never trusted on its key alone.
$ErrorActionPreference = 'Stop'
# PS 5.1 renders a progress bar per buffer, inflating a ~1 MB download from
# seconds to tens of seconds.
$ProgressPreference = 'SilentlyContinue'

$Version = '1.73.35'
$Url     = 'https://flatassembler.net/fasmw17335.zip'
$Sha256  = '8ef871b369638f63d2df475a64e9f574da06b601db5a3fcb8c12654b7bcf5e81'

$Dest    = Join-Path $PSScriptRoot 'fasm'
$Exe     = Join-Path $Dest 'FASM.EXE'
$Archive = Join-Path (Join-Path $PSScriptRoot 'fasm-archive') "fasmw-$Version.zip"

if (Test-Path $Exe) {
    Write-Host "fasm $Version already bootstrapped at $Exe"
    exit 0
}

if (Test-Path $Archive) {
    Write-Host "Using the archive at $Archive"
} else {
    $zip = Join-Path ([IO.Path]::GetTempPath()) "fasmw-$Version.zip"
    Write-Host "Downloading fasm $Version from $Url"
    # -TimeoutSec bounds the connect/first-response wait (a mid-body stall is
    # bounded separately by the stream read timeout); either way a stalled
    # origin fails hard instead of hanging every caller, e.g. the test harness.
    try {
        Invoke-WebRequest -Uri $Url -OutFile $zip -TimeoutSec 120
    } catch {
        # The origin server's TLS configuration is legacy and current CI
        # runners refuse the handshake. Integrity does not depend on the
        # channel - the pinned SHA-256 below is the gate - so plain HTTP is a
        # sound fallback.
        $fallback = $Url -replace '^https:', 'http:'
        Write-Host "https failed ($($_.Exception.Message)); retrying via $fallback"
        Invoke-WebRequest -Uri $fallback -OutFile $zip -TimeoutSec 120
    }
    # Only a completed download reaches the archive directory; a failed one
    # leaves nothing behind for the next run to refuse.
    New-Item -ItemType Directory -Force (Split-Path $Archive) | Out-Null
    Move-Item -Path $zip -Destination $Archive -Force
}

$actual = (Get-FileHash $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $Sha256) {
    Remove-Item $Archive -Force
    throw "fasm archive hash mismatch: expected $Sha256, got $actual - refusing to unpack. The archive was deleted. A downloaded archive is fetched again on the next run; one restored from a CI cache comes back identical until that cache entry is deleted or this script changes."
}

Expand-Archive -Path $Archive -DestinationPath $Dest -Force
# The archive stays: it is what the CI cache saves, and only a verified one gets here.

if (-not (Test-Path $Exe)) {
    throw "unexpected archive layout: $Exe not found after extraction."
}
Write-Host "fasm $Version bootstrapped (SHA-256 verified)."
