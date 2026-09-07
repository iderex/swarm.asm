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

if (Test-Path -LiteralPath $Exe) {
    Write-Host "fasm $Version already bootstrapped at $Exe"
    exit 0
}

if (Test-Path -LiteralPath $Archive) {
    Write-Host "Using the archive at $Archive"
} else {
    # A per-process temporary name: two cold bootstraps on one machine do
    # not write the same download. They still race for the archive path
    # and the toolchain directory, and that race is NOT closed: the hash
    # below reads the archive by path and Expand-Archive reopens it by
    # path, so a second bootstrap moving its own download over the path
    # between the two would have the first unpack bytes it never hashed.
    # One bootstrap per checkout is the supported mode, which is what CI
    # runs on a fresh checkout and what build.ps1 runs once.
    $zip = Join-Path ([IO.Path]::GetTempPath()) ("fasmw-$Version-" + [IO.Path]::GetRandomFileName() + '.zip')
    Write-Host "Downloading fasm $Version from $Url"
    # -TimeoutSec bounds the connect/first-response wait (a mid-body stall is
    # bounded separately by the stream read timeout); either way a stalled
    # origin fails hard instead of hanging every caller, e.g. the test harness.
    try {
        try {
            Invoke-WebRequest -Uri $Url -OutFile $zip -TimeoutSec 120
        } catch {
            # The origin server's TLS configuration is legacy and current CI
            # runners refuse the handshake. Integrity does not depend on the
            # channel - the pinned SHA-256 below is the gate - so plain HTTP is
            # a sound fallback.
            $fallback = $Url -replace '^https:', 'http:'
            Write-Host "https failed ($($_.Exception.Message)); retrying via $fallback"
            Invoke-WebRequest -Uri $fallback -OutFile $zip -TimeoutSec 120
        }
        # Only a completed download reaches the archive directory; a failed
        # one leaves nothing behind, in the archive directory or in TEMP, for
        # the next run to refuse or to pile up.
        New-Item -ItemType Directory -Force (Split-Path -LiteralPath $Archive) | Out-Null
        Move-Item -LiteralPath $zip -Destination $Archive -Force
    } catch {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        throw
    }
}

# SHA-256 through .NET rather than Get-FileHash. That cmdlet is a function
# Windows PowerShell resolves through its module path, and a Windows
# PowerShell started by a non-PowerShell child of pwsh (the test host under
# a pwsh step, Build.cs under a pwsh terminal) inherits pwsh's module path
# and does not find it, while Expand-Archive below still resolves. The
# failure showed on hosted run 34042336148 as a refusal leg under that
# host printing no verdict; both halves were measured locally through a
# Python child of pwsh (#344). The .NET call needs no module under either
# host. An archive that cannot be read at all - a
# directory at the path, a file held without sharing - is refused too,
# and the message says that rather than pretending to a hash verdict.
try {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Archive)
    try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
} catch {
    throw "fasm archive at $Archive could not be read ($($_.Exception.Message)) - refusing to unpack. A CI cache restores only what this script saved, so what sits there is a leftover or a lock on this machine: remove or release it, then run again."
}
if ($actual -ne $Sha256) {
    # The verdict is the message; whether the refused file could be removed
    # is reported beside it rather than allowed to replace it, and each
    # branch says what the next run will do.
    try {
        Remove-Item -LiteralPath $Archive -Force
        $fate = 'The archive was deleted: a downloaded one is fetched again on the next run, one restored from a CI cache comes back identical until that cache entry is deleted or this script changes.'
    } catch {
        $fate = "The archive could not be deleted and is still at $Archive; every run refuses it again until it is removed."
    }
    throw "fasm archive hash mismatch: expected $Sha256, got $actual - refusing to unpack. $fate"
}

Expand-Archive -LiteralPath $Archive -DestinationPath $Dest -Force
# The archive stays: it is what the CI cache saves, and only a verified one gets here.

if (-not (Test-Path -LiteralPath $Exe)) {
    throw "unexpected archive layout: $Exe not found after extraction."
}
Write-Host "fasm $Version bootstrapped (SHA-256 verified)."
