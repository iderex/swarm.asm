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
include 'kernel/grid.inc'
include 'kernel/plot.inc'

; The M3 worker pool (platform layer). Included AFTER the kernel so pass_core /
; build_core are defined; the pool crosses the thread-entry seam (pool_pass) and
; calls only the pure per-range pass, so kernel purity is unchanged. It adds the
; DLL's only kernel32 imports (all genuinely kernel32 — the import allowlist the
; conformance test pins targets swarm.exe, and CreateThread/CreateEventW/etc. are
; kernel32, so the exe allowlist holds regardless).
include 'platform/pool.inc'

; ------------------------------------------------------------------
; swarm_plot — seam wrapper over plot_core (FP: x*w needs the pinned MXCSR).
;   in:       rcx arena, rdx bgra, r8d w, r9d h
;   out:      nothing (void); the framebuffer at rdx is written
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap swarm_plot, plot_core

; build_core is exported directly as swarm_build: 1 arg, integer copy only, it
; saves rsi/rdi itself, so it is Win64-clean without the FP seam.

; ------------------------------------------------------------------
; swarm_pass — seam wrapper over pass_core (heavy FP: the MXCSR pin matters).
;   in:       rcx arena, edx first, r8d last
;   out:      nothing (void); the OUT bank in the arena is advanced one pass
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap swarm_pass, pass_core

; ------------------------------------------------------------------
; swarm_step — seam wrapper over step_core (n_steps x build+pass).
;   in:       rcx arena, edx n_steps
;   out:      nothing (void); the arena is advanced n_steps
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap swarm_step, step_core

; ------------------------------------------------------------------
; swarm_read_state — id-ordered copy-out of the current state.
;   in:       rcx arena, rdx x*, r8 y*, r9 vx*, [stack] vy*, [stack] species*
;             (each caller array holds n elements)
;   out:      x[id]..species[id] = the OUT-bank values for i in 0..n-1
;   clobbers: volatile GPRs (rax, rcx, rdx, r8-r10) and flags; no XMM/FP; every
;             nonvolatile it touches (rbx, rsi, rdi, r12-r14) is saved/restored
;   MXCSR:    untouched (pure integer copy, no FP)
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
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap swarm_init, init_core

; ------------------------------------------------------------------
; swarm_parse_preset — seam wrapper over parse_preset_core.
;   in:       rcx text (may be unterminated), edx len, r8 SwarmParams* out
;   out:      eax = 0 and *out written, or the packed negative error with
;             *out untouched (fail-closed two-phase commit)
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return
;             (seam: nonvolatiles + MXCSR saved, vzeroupper before return)
; ------------------------------------------------------------------
seam_wrap swarm_parse_preset, parse_preset_core

; ------------------------------------------------------------------
; swarm_layout_bytes — seam wrapper over layout_bytes_core.
;   in:       rcx = SwarmParams*
;   out:      rax = arena bytes (multiple of 64), or 0 when params are
;             invalid (fail-closed)
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam;
;             validation compares under the pin)
; ------------------------------------------------------------------
seam_wrap swarm_layout_bytes, layout_bytes_core

section '.edata' export data readable

  ; The M3 pool exports (swarm_pool_init / swarm_step_mt / swarm_pass_mt /
  ; swarm_pool_shutdown) drive the real worker pool from the harness so the
  ; PassParallelMatchesSerial determinism gate can compare several thread counts
  ; against the serial path. The serial swarm_step / swarm_pass stay intact — no
  ; threading crosses the P/Invoke boundary; the pool is an in-DLL concern.
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
         swarm_step,         'swarm_step',\
         swarm_plot,         'swarm_plot',\
         pool_init,          'swarm_pool_init',\
         pool_step,          'swarm_step_mt',\
         pool_pass_all,      'swarm_pass_mt',\
         pool_shutdown,      'swarm_pool_shutdown'

section '.data' data readable writeable

  ; The worker pool's mutable state (handles, ranges, publish slot). Platform
  ; state, never the arena — kernel purity is untouched.
  pool_storage

section '.idata' import data readable writeable

  ; The DLL's only imports — all genuinely kernel32 (WaitOnAddress/WakeByAddress
  ; are deliberately excluded: they forward through API-MS-Win-Core-Synch-*).
  library kernel32, 'KERNEL32.DLL'
  import kernel32,\
         CreateThread,'CreateThread',\
         CreateEventW,'CreateEventW',\
         SetEvent,'SetEvent',\
         WaitForSingleObject,'WaitForSingleObject',\
         WaitForMultipleObjects,'WaitForMultipleObjects',\
         CloseHandle,'CloseHandle',\
         GetLogicalProcessorInformation,'GetLogicalProcessorInformation',\
         GetSystemInfo,'GetSystemInfo'

section '.reloc' fixups data readable discardable

  ; The image is position-independent (every label reference is RIP-relative),
  ; so it needs no fixups; keep the directory valid with a dummy entry so the
  ; loader can still rebase the DLL.
  if $=$$
    dd 0,8
  end if
