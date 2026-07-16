# Bootstraps the pinned FASM toolchain into tools/fasm (idempotent).
#
# fasm produces PE64 directly - no linker, no SDK: this ~1 MB archive is the
# entire build toolchain. Fail-closed: the download must match the pinned
# SHA-256 exactly or nothing is unpacked.
$ErrorActionPreference = 'Stop'

$Version = '1.73.35'
$Url     = 'https://flatassembler.net/fasmw17335.zip'
$Sha256  = '8ef871b369638f63d2df475a64e9f574da06b601db5a3fcb8c12654b7bcf5e81'

$Dest = Join-Path $PSScriptRoot 'fasm'
$Exe  = Join-Path $Dest 'FASM.EXE'

if (Test-Path $Exe) {
    Write-Host "fasm $Version already bootstrapped at $Exe"
    exit 0
}

$zip = Join-Path ([IO.Path]::GetTempPath()) "fasmw-$Version.zip"
Write-Host "Downloading fasm $Version from $Url"
try {
    Invoke-WebRequest -Uri $Url -OutFile $zip
} catch {
    # The origin server's TLS configuration is legacy and current CI runners
    # refuse the handshake. Integrity does not depend on the channel - the
    # pinned SHA-256 below is the gate - so plain HTTP is a sound fallback.
    $fallback = $Url -replace '^https:', 'http:'
    Write-Host "https failed ($($_.Exception.Message)); retrying via $fallback"
    Invoke-WebRequest -Uri $fallback -OutFile $zip
}

$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $Sha256) {
    Remove-Item $zip -Force
    throw "fasm archive hash mismatch: expected $Sha256, got $actual - refusing to unpack."
}

Expand-Archive -Path $zip -DestinationPath $Dest -Force
Remove-Item $zip -Force

if (-not (Test-Path $Exe)) {
    throw "unexpected archive layout: $Exe not found after extraction."
}
Write-Host "fasm $Version bootstrapped (SHA-256 verified)."
