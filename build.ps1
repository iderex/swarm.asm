# Assembles the engine: src/main.asm -> build/swarm.exe.
#
# fasm emits the PE64 directly; there is no separate link step. The pinned
# assembler is bootstrapped on first run (SHA-256-verified download).
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

& $Fasm (Join-Path $Root 'src\main.asm') (Join-Path $BuildDir 'swarm.exe')
if ($LASTEXITCODE -ne 0) {
    throw "fasm failed with exit code $LASTEXITCODE"
}
Write-Host 'build/swarm.exe assembled.'
