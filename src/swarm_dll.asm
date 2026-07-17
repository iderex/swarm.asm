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
include 'platform/seam.inc'

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
include 'kernel/parse.inc'
include 'kernel/layout.inc'
include 'kernel/cpuid.inc'
include 'kernel/init.inc'
include 'kernel/state.inc'
include 'kernel/step.inc'

; build_core is exported directly as swarm_build: 1 arg, integer copy only, it
; saves rsi/rdi itself, so it is Win64-clean without the FP seam.

; ------------------------------------------------------------------
; swarm_pass — seam wrapper over pass_core (heavy FP: the MXCSR pin matters).
;   in:       rcx arena, edx first, r8d last
; ------------------------------------------------------------------
swarm_pass:
        seam_enter
        call    pass_core
        seam_leave

; ------------------------------------------------------------------
; swarm_step — seam wrapper over step_core (n_steps x build+pass).
;   in:       rcx arena, edx n_steps
; ------------------------------------------------------------------
swarm_step:
        seam_enter
        call    step_core
        seam_leave

; ------------------------------------------------------------------
; swarm_read_state — id-ordered copy-out of the current state.
;   in:       rcx arena, rdx x*, r8 y*, r9 vx*, [stack] vy*, [stack] species*
;             (each caller array holds n elements)
;   out:      x[id]..species[id] = the OUT-bank values for i in 0..n-1
;   ABI:      6 args, so NOT the FP seam (which assumes <=4 args); a plain
;             Win64 prologue over an rbp frame. Pure memory copy, no FP, so
;             no MXCSR pin is needed. rbx and the dst regs are callee-saved.
; ------------------------------------------------------------------
swarm_read_state:
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        push    rdi
        push    r12
        push    r13
        push    r14
        mov     rbx, rcx                ; arena
        mov     rsi, rdx                ; x dst
        mov     rdi, r8                 ; y dst
        mov     r12, r9                 ; vx dst
        mov     r13, [rbp+48]           ; vy dst  (arg5: +8 ret, +32 shadow)
        mov     r14, [rbp+56]           ; species dst (arg6)
        ; copy_scatter is a private leaf that never homes its args, so no
        ; 32-byte shadow space is reserved before these calls (safe by that
        ; contract; the internal ABI, not the Win64 seam).
        scatter_component 0, rsi
        scatter_component 1, rdi
        scatter_component 2, r12
        scatter_component 3, r13
        scatter_component 4, r14
        pop     r14
        pop     r13
        pop     r12
        pop     rdi
        pop     rsi
        pop     rbx
        pop     rbp
        ret

; cpu_paths_core is exported directly as swarm_cpu_paths: it takes no args,
; returns the path bits in eax, and preserves rbx itself (its only nonvolatile
; touch), so it is Win64-clean and integer-only — no seam.

; ------------------------------------------------------------------
; swarm_init — seam wrapper over init_core (FP: the u01 convert needs the pin).
;   in:       rcx arena, rdx arena_bytes, r8 SwarmParams*
;   out:      eax = 0 on success, else IERR_* (arena untouched on failure)
;   ABI:      full Win64 seam
; ------------------------------------------------------------------
swarm_init:
        seam_enter
        call    init_core
        seam_leave

; ------------------------------------------------------------------
; swarm_parse_preset — seam wrapper over parse_preset_core.
;   in:       rcx text (may be unterminated), edx len, r8 SwarmParams* out
;   out:      eax = 0 and *out written, or the packed negative error with
;             *out untouched (fail-closed two-phase commit)
;   ABI:      full Win64 seam — nonvolatiles + MXCSR saved, pin 0x9FC0,
;             vzeroupper before return
; ------------------------------------------------------------------
swarm_parse_preset:
        seam_enter
        call    parse_preset_core
        seam_leave

; ------------------------------------------------------------------
; swarm_layout_bytes — seam wrapper over layout_bytes_core.
;   in:       rcx = SwarmParams*
;   out:      rax = arena bytes (multiple of 64), or 0 when params are
;             invalid (fail-closed)
;   ABI:      full Win64 seam (validation compares under the pinned MXCSR)
; ------------------------------------------------------------------
swarm_layout_bytes:
        seam_enter
        call    layout_bytes_core
        seam_leave

section '.edata' export data readable

  export 'swarm.kernel.dll',\
         swarm_version,      'swarm_version',\
         rng_fill_core,      'swarm_rng_fill',\
         swarm_parse_preset, 'swarm_parse_preset',\
         swarm_layout_bytes, 'swarm_layout_bytes',\
         cpu_paths_core,     'swarm_cpu_paths',\
         swarm_init,         'swarm_init',\
         swarm_read_state,   'swarm_read_state',\
         build_core,         'swarm_build',\
         swarm_pass,         'swarm_pass',\
         swarm_step,         'swarm_step'

section '.reloc' fixups data readable discardable

  ; The image currently needs no fixups; keep the directory valid with a
  ; dummy entry so the loader can still rebase the DLL.
  if $=$$
    dd 0,8
  end if
