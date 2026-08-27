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

# Sweep the aside copies an earlier run could not delete because something
# still held them; by now nothing does.
Get-ChildItem -LiteralPath $BuildDir -Filter '*.inuse-*' -File -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }

# The bundled Win64 include macros (win64a.inc et al.) resolve via INCLUDE.
$env:INCLUDE = Join-Path $Root 'tools\fasm\INCLUDE'

# Publishes a freshly assembled file over the previous one. fasm writes its
# output in place, and Windows refuses a write to an image a live process has
# loaded - a test host that loaded build/swarm.kernel.dll, a running
# build/swarm.exe - so an in-place assembly fails for a reason that has nothing
# to do with the sources. A mapped image cannot be overwritten or deleted, but
# it CAN be renamed, and the holder keeps the image it already mapped, so the
# previous file is moved aside and the new one takes its name. The aside copy
# is deleted when nothing holds it any more and is otherwise left for the next
# run to sweep; build/ is git-ignored in full and nothing enumerates it.
function Publish-Output {
    param([string]$Temp, [string]$Output)
    try {
        Move-Item -LiteralPath $Temp -Destination $Output -Force -ErrorAction Stop
        return
    }
    catch {
        if (-not (Test-Path -LiteralPath $Output)) { throw }
    }
    $aside = "$Output.inuse-" + [guid]::NewGuid().ToString('N').Substring(0, 8)
    Move-Item -LiteralPath $Output -Destination $aside -Force -ErrorAction Stop
    Move-Item -LiteralPath $Temp -Destination $Output -Force -ErrorAction Stop
    Remove-Item -LiteralPath $aside -Force -ErrorAction SilentlyContinue
}

function Invoke-Fasm {
    param([string]$Source, [string]$Output, [string[]]$Define = @())
    $temp = "$Output.new"
    & $Fasm @Define (Join-Path $Root "src\$Source") $temp
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        throw "fasm failed on $Source with exit code $LASTEXITCODE"
    }
    Publish-Output $temp $Output
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
