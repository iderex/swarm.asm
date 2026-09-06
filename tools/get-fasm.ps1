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

if (Test-Path -LiteralPath $Archive) {
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
    Move-Item -LiteralPath $zip -Destination $Archive -Force
}

# SHA-256 through .NET rather than Get-FileHash. That cmdlet is a function
# Windows PowerShell resolves through its module path, and a Windows
# PowerShell started from a pwsh-launched process inherits pwsh's module
# path and does not find it - measured on the hosted runner and locally
# (#344). The .NET call needs no module under either host.
$sha = [Security.Cryptography.SHA256]::Create()
$stream = [IO.File]::OpenRead($Archive)
try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
finally { $stream.Dispose(); $sha.Dispose() }
if ($actual -ne $Sha256) {
    # The verdict is the message; whether the refused file could be removed
    # is reported beside it rather than allowed to replace it.
    try { Remove-Item -LiteralPath $Archive -Force; $fate = 'The archive was deleted.' }
    catch { $fate = "The archive could not be deleted and is still at $Archive." }
    throw "fasm archive hash mismatch: expected $Sha256, got $actual - refusing to unpack. $fate A downloaded archive is fetched again on the next run; one restored from a CI cache comes back identical until that cache entry is deleted or this script changes."
}

Expand-Archive -LiteralPath $Archive -DestinationPath $Dest -Force
# The archive stays: it is what the CI cache saves, and only a verified one gets here.

if (-not (Test-Path $Exe)) {
    throw "unexpected archive layout: $Exe not found after extraction."
}
Write-Host "fasm $Version bootstrapped (SHA-256 verified)."
