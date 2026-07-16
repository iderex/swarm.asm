; swarm.kernel.dll — the simulation kernel packaged for the C# test harness.
;
; Test artifact only, never shipped. Both build targets include the same
; kernel sources, so the tested kernel is the shipped kernel by construction
; (docs/MASTERPLAN.md, decision 5). Exports cross the seam under the full
; Win64 ABI.

; The subsystem field is loader-inert on a DLL (only checked on the
; process image) — the GUI 6.0 here merely mirrors the exe's format line.
format PE64 GUI 6.0 DLL
entry DllEntryPoint

include 'win64a.inc'
include 'kernel/abi.inc'

section '.text' code readable executable

; ------------------------------------------------------------------
; DllEntryPoint — load/unload notification.
;   in:       rcx hinstDLL, edx fdwReason, r8 lpvReserved (all ignored —
;             the DLL holds no per-process state; all state lives in the
;             caller-owned arena)
;   out:      eax = TRUE
;   clobbers: rax
;   MXCSR:    untouched
; ------------------------------------------------------------------
DllEntryPoint:
        mov     eax, TRUE
        ret

; ------------------------------------------------------------------
; swarm_version — the ABI version of the export surface.
;   in:       nothing
;   out:      eax = SWARM_ABI_VERSION
;   clobbers: rax
;   MXCSR:    untouched — this export moves no FP; the save/pin/restore
;             seam (decision 2, pinned 0x9FC0) is MANDATORY in every
;             export prologue that does, starting with the next slice
; ------------------------------------------------------------------
swarm_version:
        mov     eax, SWARM_ABI_VERSION
        ret

; rng_fill_core is exported directly as swarm_rng_fill: its kernel-tier
; contract (rcx seed, rdx out, r8d count) already matches the Win64 argument
; registers, so no adapter is needed. It is integer-only and touches no
; nonvolatile register or MXCSR, so it wears no seam — unlike the FP exports
; that follow, where the MXCSR pin (decision 2) is mandatory. Caller-owned
; bounds, kernel tier: out must hold count u64 slots — the kernel never
; bounds-checks, so a short buffer from the harness is a silent OOB write.
include 'kernel/rng.inc'

section '.edata' export data readable

  export 'swarm.kernel.dll',\
         swarm_version,  'swarm_version',\
         rng_fill_core,  'swarm_rng_fill'

section '.reloc' fixups data readable discardable

  ; The image currently needs no fixups; keep the directory valid with a
  ; dummy entry so the loader can still rebase the DLL.
  if $=$$
    dd 0,8
  end if
