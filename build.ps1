# Assembles the engine and the test DLL:
#   src/swarm.asm     -> build/swarm.exe        (the product)
#   src/swarm_dll.asm -> build/swarm.kernel.dll (test artifact for the harness)
#
# With -Debug the same two sources assemble a second time into build/debug/,
# with SWARM_DEBUG defined. That build carries the platform layer's debug-only
# assertions (src/platform/seam.inc); the release build above emits none of
# them, so the shipped instruction stream is the one that was measured. The
# release output does not move: -Debug adds a directory, it does not change a
# byte of build/swarm.exe or build/swarm.kernel.dll.
#
# fasm emits the PE64 directly; there is no separate link step. The pinned
# assembler is bootstrapped on first run (SHA-256-verified download).
param([switch]$Debug)

$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Fasm = Join-Path $Root 'tools\fasm\FASM.EXE'
if (-not (Test-Path $Fasm)) {
    & (Join-Path $Root 'tools\get-fasm.ps1')
}

$BuildDir = Join-Path $Root 'build'
New-Item -ItemType Directory -Force $BuildDir | Out-Null

# The bundled Win64 include macros (win64a.inc et al.) resolve via INCLUDE.
$env:INCLUDE = Join-Path $Root 'tools\fasm\INCLUDE'

function Invoke-Fasm {
    param([string]$Source, [string]$Output, [string[]]$Define = @())
    & $Fasm @Define (Join-Path $Root "src\$Source") $Output
    if ($LASTEXITCODE -ne 0) {
        throw "fasm failed on $Source with exit code $LASTEXITCODE"
    }
}

Invoke-Fasm 'swarm.asm' (Join-Path $BuildDir 'swarm.exe')
Invoke-Fasm 'swarm_dll.asm' (Join-Path $BuildDir 'swarm.kernel.dll')
Write-Host 'build/swarm.exe and build/swarm.kernel.dll assembled.'

if ($Debug) {
    $DebugDir = Join-Path $BuildDir 'debug'
    New-Item -ItemType Directory -Force $DebugDir | Out-Null
    Invoke-Fasm 'swarm.asm' (Join-Path $DebugDir 'swarm.exe') @('-d', 'SWARM_DEBUG=1')
    Invoke-Fasm 'swarm_dll.asm' (Join-Path $DebugDir 'swarm.kernel.dll') @('-d', 'SWARM_DEBUG=1')
    Write-Host 'build/debug/swarm.exe and build/debug/swarm.kernel.dll assembled (SWARM_DEBUG).'
}
