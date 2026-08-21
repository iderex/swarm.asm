; SPDX-License-Identifier: MIT
; swarm.kernel.dll - the simulation kernel packaged for the C# test harness.
;
; Test artifact only, never shipped. Both build targets include the same
; kernel sources, so the tested kernel is the shipped kernel by construction
; (docs/MASTERPLAN.md, decision 5). Exports cross the seam under the full
; Win64 ABI.

; The subsystem field is loader-inert on a DLL (only checked on the
; process image) - the GUI 6.0 here merely mirrors the exe's format line.
format PE64 GUI 6.0 DLL
entry DllEntryPoint

include 'win64a.inc'
include 'kernel/abi.inc'
include 'platform/seam.inc'

section '.text' code readable executable

; ------------------------------------------------------------------
; DllEntryPoint - load/unload notification.
;   in:       rcx hinstDLL, edx fdwReason, r8 lpvReserved (all ignored -
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
; swarm_version - the ABI version of the export surface.
;   in:       nothing
;   out:      eax = SWARM_ABI_VERSION
;   clobbers: rax
;   MXCSR:    untouched - this export moves no FP; the save/pin/restore
;             seam (decision 2, pinned 0x9FC0) is MANDATORY in every
;             export prologue that does, starting with the next slice
; ------------------------------------------------------------------
swarm_version:
        mov     eax, SWARM_ABI_VERSION
        ret

; rng_fill_core is exported directly as swarm_rng_fill: its kernel-tier
; contract (rcx seed, rdx out, r8d count) already matches the Win64 argument
; registers, so no adapter is needed. It is integer-only and touches no
; nonvolatile register or MXCSR, so it wears no seam - unlike the FP exports
; that follow, where the MXCSR pin (decision 2) is mandatory. Caller-owned
; bounds, kernel tier: out must hold count u64 slots - the kernel never
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
; DLL's only kernel32 imports (all genuinely kernel32 - the import allowlist the
; conformance test pins targets swarm.exe, and CreateThread/CreateEventW/etc. are
; kernel32, so the exe allowlist holds regardless).
include 'platform/pool.inc'

; The capture reduction (platform layer, pure integer, no imports of its own).
; It is included here so the harness can call the very routine swarm.exe
; reports its frame times with. Left in the exe alone it would sit behind a
; process nothing can P/Invoke, and the one thing it can get wrong - which
; sorted element a percentile names - would be asserted by nothing.
include 'platform/frametime.inc'

; ------------------------------------------------------------------
; swarm_plot - seam wrapper over plot_core (FP: x*w needs the pinned MXCSR).
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
; swarm_pass - seam wrapper over pass_core (heavy FP: the MXCSR pin matters).
;   in:       rcx arena, edx first, r8d last
;   out:      nothing (void); the OUT bank in the arena is advanced one pass
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap swarm_pass, pass_core

; ------------------------------------------------------------------
; swarm_step - seam wrapper over step_core (n_steps x build+pass).
;   in:       rcx arena, edx n_steps
;   out:      nothing (void); the arena is advanced n_steps
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap swarm_step, step_core

; ------------------------------------------------------------------
; swarm_read_state - id-ordered copy-out of the current state.
;   in:       rcx arena, rdx x*, r8 y*, r9 vx*, [stack] vy*, [stack] species*
;             (each caller array holds n elements)
;   out:      eax = 0 and x[id]..species[id] = the OUT-bank values for i in
;             0..n-1. eax = 1 means at least one id_out[i] was outside [0, n):
;             that element's store was DROPPED rather than written past the end
;             of the caller's array (copy_scatter's bound, issue #86), so the
;             matching caller slot keeps its prior contents and the copy-out as
;             a whole must be treated as untrustworthy. No path in the kernel
;             produces such an id today; the status exists so a future one
;             cannot degrade to a silently partial copy.
;   clobbers: volatile GPRs (rax, rcx, rdx, r8-r11) and flags; no XMM/FP; every
;             nonvolatile it touches (rbx, rsi, rdi, r12-r15) is saved/restored
;   MXCSR:    untouched (pure integer copy, no FP)
;   ABI:      6 args, so NOT the FP seam (which assumes <=4 args); a plain
;             Win64 prologue over an rbp frame. Pure memory copy, no FP, so
;             no MXCSR pin is needed. rbx and the dst regs are callee-saved.
;             The i32 return matches docs/MASTERPLAN.md's export table; every
;             stack-arg offset below is rbp-relative, so the extra push does
;             not move arg5/arg6.
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
        push    r15
        mov     rbx, rcx                ; arena
        mov     rsi, rdx                ; x dst
        mov     rdi, r8                 ; y dst
        mov     r12, r9                 ; vx dst
        mov     r13, [rbp+48]           ; vy dst  (arg5: +8 ret, +32 shadow)
        mov     r14, [rbp+56]           ; species dst (arg6)
        xor     r15d, r15d              ; no component has rejected an id yet
        ; Two deliberate deviations from the Win64 call sequence, both safe
        ; only because copy_scatter is a private leaf (the internal ABI, not
        ; the Win64 seam). First: it never homes its args, so no 32-byte shadow
        ; space is reserved. Second: rbp plus seven nonvolatiles are pushed
        ; above, so these calls land with rsp = 8 (mod 16) rather than 0 - it
        ; issues no onward call and touches no vector or aligned-spill
        ; instruction, so nothing depends on the 16-byte guarantee. Both are
        ; restated in copy_scatter's own contract header; a callee that ever
        ; needs either must realign and reserve here.
        scatter_component 0, rsi
        scatter_component 1, rdi
        scatter_component 2, r12
        scatter_component 3, r13
        scatter_component 4, r14
        mov     eax, r15d               ; 0 = every id was in range
        pop     r15
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
; touch), so it is Win64-clean and integer-only - no seam.

; ------------------------------------------------------------------
; swarm_init - seam wrapper over init_core (FP: the u01 convert needs the pin).
;   in:       rcx arena, rdx arena_bytes, r8 SwarmParams*
;   out:      eax = 0 on success, else IERR_* (arena untouched on failure)
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap swarm_init, init_core

; ------------------------------------------------------------------
; swarm_parse_preset - seam wrapper over parse_preset_core.
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
; swarm_layout_bytes - seam wrapper over layout_bytes_core.
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

; ------------------------------------------------------------------
; mxcsr_read_core - report the control word in force where it runs.
;
; Platform tier on purpose. The kernel cannot report its own control word:
; stmxcsr is on the forbidden-mnemonic list for src/kernel/ (decision 2 puts
; every MXCSR transition at the seam), and this routine closes that
; observability hole without touching the ban. It carries no seam of its own -
; it is the thing a seam is wrapped around.
;
;   in:       nothing
;   out:      eax = MXCSR as it stands on entry, zero-extended into rax
;   clobbers: rax; no nonvolatile, no FP state, no memory outside its own frame
;   MXCSR:    read, never written. The 8 bytes are a private scratch slot:
;             Win64 has no red zone, so the store needs allocated stack
; ------------------------------------------------------------------
mxcsr_read_core:
        sub     rsp, 8
        stmxcsr [rsp]
        mov     eax, [rsp]
        add     rsp, 8
        ret

; ------------------------------------------------------------------
; swarm_mxcsr - seam wrapper over mxcsr_read_core: the control word an FP core
; actually runs under, read from inside the pinned region.
;
; Diagnostic, and only sound because it wears the same frame the FP exports
; wear. A bare export would report the CALLER's control word and read as if it
; had measured the engine's.
;
;   in:       nothing
;   out:      eax = the control word in force inside the seam (SEAM_MXCSR)
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam) -
;             so a hostile caller word is neither seen by the read nor lost
; ------------------------------------------------------------------
seam_wrap swarm_mxcsr, mxcsr_read_core

; ------------------------------------------------------------------
; swarm_call_under_mxcsr - call an export with a chosen control word installed,
; and report the word the export left behind.
;
; The harness cannot do this itself. Installing a hostile word from managed
; code would leave every instruction between the install and the call running
; under it, and a word with the exception masks cleared faults on the first
; inexact result - which nearly every FP operation produces. Here the hostile
; word covers the call and nothing else: the instructions on either side of it
; are integer, and a seam-wrapped target re-pins before its first FP op.
;
; What it measures is the seam's restore half. The word this routine hands
; back is whatever the target left in MXCSR, so a target that restores its
; caller's word hands back the hostile word unchanged, and one that leaks its
; own pin hands back that instead.
;
;   in:       ecx  = the control word to install across the call
;             rdx  = the target routine
;             r8   = u64* receiving the target's return value in rax. For a
;                    void target that value is undefined and the caller is the
;                    one who knows which it called
;             r9   = the target's 1st argument
;             [rbp+48], [rbp+56], [rbp+64] = its 2nd, 3rd, 4th. Four is every
;                    current seam export's maximum; a 5th would need the frame
;                    re-derived, not another mov
;   out:      eax = MXCSR as the target left it
;   clobbers: volatile (caller-saved) registers per the Win64 ABI, plus
;             whatever the target clobbers among them; rbx, rsi and rbp are
;             saved and restored here and every other nonvolatile is the
;             target's own responsibility
;   MXCSR:    installed, read back, and put back to the word this routine was
;             entered with - so the harness thread never returns holding it
;   stack:    entry rsp = 8 mod 16; the three pushes and the 48-byte frame land
;             the call's own push at 8 mod 16 again, which is where a Win64
;             callee expects to start. [rsp+0..31] is the target's shadow
;             space, [rsp+32] and [rsp+36] the two control words
; ------------------------------------------------------------------
swarm_call_under_mxcsr:
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        sub     rsp, 48
        mov     ebx, ecx                ; the word to install
        mov     rsi, r8                 ; where the return value goes
        mov     rax, rdx                ; the target
        mov     rcx, r9                 ; shuffle the target's own arguments
        mov     rdx, [rbp+48]           ;   down into rcx/rdx/r8/r9
        mov     r8,  [rbp+56]
        mov     r9,  [rbp+64]
        stmxcsr [rsp+32]                ; the word we were entered with
        mov     [rsp+36], ebx
        ldmxcsr [rsp+36]
        call    rax
        stmxcsr [rsp+36]                ; the word the target left behind
        ldmxcsr [rsp+32]                ; entry word back, before anything else
        mov     [rsi], rax
        mov     eax, [rsp+36]
        add     rsp, 48
        pop     rsi
        pop     rbx
        pop     rbp
        ret

section '.edata' export data readable

  ; The M3 pool exports (swarm_pool_init / swarm_step_mt / swarm_pass_mt /
  ; swarm_build_mt / swarm_pool_shutdown) drive the real worker pool from the
  ; harness, so the PassParallelMatchesSerial and BuildParallelMatchesSerial
  ; determinism gates can compare several thread counts against the serial
  ; path. The serial swarm_step / swarm_pass stay intact - no
  ; threading crosses the P/Invoke boundary; the pool is an in-DLL concern.
  ;
  ; swarm_frame_stats exports the capture reduction. It is integer-only and
  ; preserves every nonvolatile it touches, so it wears no seam. It is here for
  ; the reason swarm_mxcsr is: the shipped exe is its only other caller, and a
  ; figure that exe publishes has to be checkable against the definition
  ; docs/BENCHMARKS.md states rather than against itself.
  export 'swarm.kernel.dll',\
         swarm_version,      'swarm_version',\
         rng_fill_core,      'swarm_rng_fill',\
         swarm_parse_preset, 'swarm_parse_preset',\
         swarm_layout_bytes, 'swarm_layout_bytes',\
         cpu_paths_core,     'swarm_cpu_paths',\
         swarm_mxcsr,        'swarm_mxcsr',\
         swarm_call_under_mxcsr, 'swarm_call_under_mxcsr',\
         swarm_init,         'swarm_init',\
         swarm_read_state,   'swarm_read_state',\
         build_core,         'swarm_build',\
         swarm_pass,         'swarm_pass',\
         swarm_step,         'swarm_step',\
         swarm_plot,         'swarm_plot',\
         pool_init,          'swarm_pool_init',\
         pool_step,          'swarm_step_mt',\
         pool_pass_all,      'swarm_pass_mt',\
         pool_build,         'swarm_build_mt',\
         pool_shutdown,      'swarm_pool_shutdown',\
         frame_stats_core,   'swarm_frame_stats'

section '.data' data readable writeable

  ; The worker pool's mutable state (handles, ranges, publish slot). Platform
  ; state, never the arena - kernel purity is untouched.
  pool_storage

section '.idata' import data readable writeable

  ; The DLL's only imports - all genuinely kernel32 (WaitOnAddress/WakeByAddress
  ; are deliberately excluded: they forward through API-MS-Win-Core-Synch-*).
  ; VirtualAlloc/VirtualFree hold the pool's per-worker histogram scratch. That
  ; scratch is platform-owned precisely so the arena's size stays a function of
  ; SwarmParams alone, which has no thread count in it (grid.inc says why).
  library kernel32, 'KERNEL32.DLL'
  import kernel32,\
         VirtualAlloc,'VirtualAlloc',\
         VirtualFree,'VirtualFree',\
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
