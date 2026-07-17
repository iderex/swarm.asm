; pool.inc — the M3 persistent worker pool (platform layer; docs/MASTERPLAN.md
; decision 6, issue #68). Parallelises the force+integrate pass across cores;
; the build (counting sort) stays serial in v1.
;
; Why this is platform, not kernel: the pool is pure OS orchestration
; (CreateThread / auto-reset events) and holds mutable state (handles, ranges).
; The KERNEL never sees a thread — each worker crosses the thread-entry seam and
; then calls the pure per-range pass `swarm_pass(arena, first, last)` semantics
; (here `pool_pass`). Kernel purity (no API calls, no writable state in
; src/kernel/) is preserved: this file lives in src/platform/ and is excluded
; from the kernel source scan.
;
; Determinism (the load-bearing invariant). The pass is a pure map
; OUT[i] = f(i, IN, cell_start, params): IN and cell_start are frozen by the
; serial build before any worker is signalled, ranges are disjoint and cover
; [0,n) exactly, so the OUT bank is bit-identical to the serial pass for ANY
; thread count, partition, and completion order — the machine-checked form of
; GridPassSplitInvariance. The one landmine is MXCSR: it is PER THREAD, so a
; worker running with the default control word would keep the denormals the
; pinned main thread flushes (FTZ/DAZ) and diverge. `pool_pass` is the seam
; wrapper over pass_core, so every worker pins its own MXCSR to 0x9FC0 and
; vzerouppers exactly as the exports do — the per-thread determinism seam.
;
; Distribution: STATIC even split, each range a multiple of 16 (16 consecutive
; f32 = one 64-byte line, so 16-aligned boundaries keep the seven disjoint OUT
; arrays off shared cache lines — no false sharing). Self-scheduling over a
; shared counter is the noted follow-up (the reference 5950X is homogeneous, so
; static-even has no imbalance to absorb and no lock xadd contention).
;
; Sync primitives are all genuinely kernel32 (CreateThread, CreateEventW,
; SetEvent, WaitForSingleObject, WaitForMultipleObjects, CloseHandle,
; GetLogicalProcessorInformation, GetSystemInfo). WaitOnAddress/WakeByAddress
; are deliberately NOT used — they forward through API-MS-Win-Core-Synch-* and
; would break the exe import allowlist.
;
; Reloc-safe by construction: every label reference is RIP-relative — code/data
; addresses passed as values use `lea reg, [label]` and array indexing loads the
; base with `lea` first (an absolute `[label + idx*s]` disp32 cannot hold the
; DLL image base and would need a base relocation this image does not carry).

POOL_MAX = 64                          ; worker cap (WaitForMultipleObjects
                                       ;   takes <= 64 handles: T-1 <= 63)
RELATION_PROCESSOR_CORE = 0            ; LOGICAL_PROCESSOR_RELATIONSHIP value
LPI_REL    = 8                         ; SYSTEM_LOGICAL_PROCESSOR_INFORMATION
                                       ;   .Relationship offset (record stride 32)
SI_NPROC   = 0x20                      ; SYSTEM_INFO.dwNumberOfProcessors offset

; ------------------------------------------------------------------
; pool_pass — the thread-entry seam over pass_core (per-thread MXCSR pin +
; Win64 nonvolatiles + 0-mod-32 kernel entry + vzeroupper). Both the main
; thread (as worker 0) and every worker thread run their sub-range through this
; single gate, so the per-thread FTZ/DAZ pin is impossible to miss.
;   in:       rcx arena, edx first, r8d last
;   out:      OUT[first..last) integrated from the frozen IN bank
;   clobbers: Win64 volatiles only; the seam saves/restores every nonvolatile
;             and MXCSR (identical contract to the swarm_pass export)
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return
; ------------------------------------------------------------------
seam_wrap pool_pass, pass_core

; ------------------------------------------------------------------
; pool_worker_thread — CreateThread entry for worker w (1..T-1).
;   in:       ecx = worker index w (the LPVOID thread parameter; low 32 bits)
;   out:      eax = 0 at exit
;   clobbers: Win64 volatiles; preserves rbx/rsi/rbp (the nonvolatiles it uses)
;   MXCSR:    the per-range pass pins it via pool_pass; the park loop is integer
; ------------------------------------------------------------------
; Parks on go[w]; on wake either exits (shutdown) or runs the frozen-IN pass
; over [first[w], last[w]) and signals done[w]. Auto-reset events: the wait
; consumes the signal, so no explicit ResetEvent.
pool_worker_thread:
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        sub     rsp, 32                ; shadow space for the leaf API calls
        mov     ebx, ecx               ; w survives every call (nonvolatile)
  .park:
        lea     rax, [pool_go]         ; WaitForSingleObject(go[w], INFINITE)
        mov     rcx, [rax+rbx*8]
        mov     edx, -1
        call    [WaitForSingleObject]
        cmp     dword [pool_stopflag], 0
        jne     .exit
        mov     rcx, [pool_arena]      ; pool_pass(arena, first[w], last[w])
        lea     rax, [pool_first]
        mov     edx, [rax+rbx*4]
        lea     rax, [pool_last]
        mov     r8d, [rax+rbx*4]
        call    pool_pass
        lea     rax, [pool_done]       ; SetEvent(done[w])
        mov     rcx, [rax+rbx*8]
        call    [SetEvent]
        jmp     .park
  .exit:
        xor     eax, eax
        add     rsp, 32
        pop     rsi
        pop     rbx
        pop     rbp
        ret

; ------------------------------------------------------------------
; pool_partition — static even 16-aligned split of [0,n) across T workers.
;   in:       ecx = n; reads [pool_nthreads] = T
;   out:      pool_first[w], pool_last[w] for w in 0..T-1: disjoint, covering
;             [0,n) exactly, interior boundaries multiples of 16, last[T-1] = n
;   clobbers: rax, rcx, rdx, r8, r9, r10, flags; preserves rbx
;   MXCSR:    untouched (integer only)
; ------------------------------------------------------------------
; q = round_up_16(ceil(n/T)); first[w] = min(w*q, n); last[w] = min((w+1)*q, n).
; T <= 64 and n <= 2^20, so w*q < 2^21 — the 32-bit multiply cannot overflow.
pool_partition:
        push    rbx
        mov     r8d, ecx               ; n
        mov     r9d, [pool_nthreads]   ; T
        mov     eax, r8d
        add     eax, r9d
        dec     eax                    ; n + T - 1
        xor     edx, edx
        div     r9d                    ; eax = ceil(n / T)
        add     eax, 15
        and     eax, -16               ; q (multiple of 16, >= 16)
        mov     r10d, eax              ; q
        lea     r11, [pool_first]
        lea     rbx, [pool_last]
        xor     ecx, ecx               ; w = 0
  .loop:
        cmp     ecx, r9d
        jae     .done
        mov     eax, ecx               ; first = min(w*q, n)
        imul    eax, r10d
        cmp     eax, r8d
        jbe     .f_ok
        mov     eax, r8d
  .f_ok:
        mov     [r11+rcx*4], eax
        lea     eax, [ecx+1]           ; last = min((w+1)*q, n)
        imul    eax, r10d
        cmp     eax, r8d
        jbe     .l_ok
        mov     eax, r8d
  .l_ok:
        mov     [rbx+rcx*4], eax
        inc     ecx
        jmp     .loop
  .done:
        pop     rbx
        ret

; ------------------------------------------------------------------
; pool_fanout — one parallel pass over [0,n): wake workers, run worker 0's
; range on this thread, join. No build, no frame advance (the caller owns
; those). Assumes IN and cell_start are already frozen by the serial build.
;   in:       rcx = arena
;   out:      OUT[0..n) integrated
;   clobbers: Win64 volatiles; preserves rbx/rsi/r12 (and rbp)
;   MXCSR:    the pass pins it per thread via pool_pass; fanout is integer
; ------------------------------------------------------------------
pool_fanout:
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        push    rdi
        push    r12
        sub     rsp, 32                ; shadow space (all API calls <= 4 args)
        mov     rbx, rcx               ; arena
        mov     [pool_arena], rbx      ; publish for the workers
        mov     ecx, [rbx+AH_PARAMS+SP_N]
        call    pool_partition         ; fill first/last[0..T-1]
        mov     r12d, [pool_nthreads]  ; T
        mov     esi, 1                 ; wake workers 1..T-1
  .wake:
        cmp     esi, r12d
        jae     .main_work
        lea     rax, [pool_go]         ; SetEvent(go[w]) (auto-reset)
        mov     rcx, [rax+rsi*8]
        call    [SetEvent]
        inc     esi
        jmp     .wake
  .main_work:
        mov     rcx, rbx               ; worker 0 runs on this thread:
        lea     rax, [pool_first]      ;   pool_pass(arena, first[0], last[0])
        mov     edx, [rax]
        lea     rax, [pool_last]
        mov     r8d, [rax]
        call    pool_pass
        cmp     r12d, 1                ; single worker: nothing to join
        jbe     .done
        mov     ecx, r12d              ; WaitForMultipleObjects(T-1,
        dec     ecx                    ;   &done[1], TRUE, INFINITE)
        lea     rdx, [pool_done]
        add     rdx, 8                 ; &done[1]: done[1..T-1] are contiguous
        mov     r8d, 1                 ; bWaitAll = TRUE
        mov     r9d, -1                ; INFINITE
        call    [WaitForMultipleObjects]
  .done:
        add     rsp, 32
        pop     r12
        pop     rdi
        pop     rsi
        pop     rbx
        pop     rbp
        ret

; ------------------------------------------------------------------
; pool_pass_all — one parallel pass over [0,n), no build/frame advance. The
; benchmark seam: repeatable over a frozen IN bank, mirroring swarm_pass.
;   in:       rcx = arena
;   out:      OUT[0..n) integrated
;   clobbers: as pool_fanout (tail call)
;   MXCSR:    per-thread pin via pool_pass
; ------------------------------------------------------------------
pool_pass_all:
        jmp     pool_fanout

; ------------------------------------------------------------------
; pool_step — the threaded frame loop: serial build, parallel pass, advance.
;   in:       rcx = arena, edx = n_steps
;   out:      the world advanced n_steps; AH_FRAME += n_steps. Bit-identical to
;             swarm_step for any T (build is serial; the pass is split-invariant)
;   clobbers: Win64 volatiles; preserves rbx/rsi (and rbp)
;   MXCSR:    build is integer; the pass pins it per thread via pool_pass
; ------------------------------------------------------------------
pool_step:
        test    edx, edx
        jz      .done
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        sub     rsp, 32
        mov     rbx, rcx               ; arena
        mov     esi, edx               ; remaining steps
  .loop:
        mov     rcx, rbx
        call    build_core             ; serial: OUT -> IN (+ grid sort)
        mov     rcx, rbx
        call    pool_fanout            ; parallel: pass over [0,n)
        inc     qword [rbx+AH_FRAME]
        dec     esi
        jnz     .loop
        add     rsp, 32
        pop     rsi
        pop     rbx
        pop     rbp
  .done:
        ret

; ------------------------------------------------------------------
; pool_detect_cores — physical core count, falling back to the logical count.
;   in:       nothing
;   out:      eax = core count clamped to [1, POOL_MAX]
;   clobbers: Win64 volatiles; preserves rbx/rsi/rdi (and rbp)
;   MXCSR:    untouched (integer only)
; ------------------------------------------------------------------
; Physical, not logical: the pass is vsqrtps/vdivps-bound (BENCHMARKS.md), and
; two SMT siblings share one divide/sqrt port, so a second hyperthread adds
; scheduling noise for ~no throughput. GetLogicalProcessorInformation counts
; PROCESSOR_CORE records = physical cores; GetSystemInfo (logical) is the
; fallback if that call fails. Determinism is independent of T regardless.
pool_detect_cores:
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        push    rdi
        sub     rsp, 40
        mov     dword [pool_cpulen], 8192   ; in/out: buffer capacity
        lea     rcx, [pool_cpubuf]
        lea     rdx, [pool_cpulen]
        call    [GetLogicalProcessorInformation]
        test    eax, eax
        jz      .fallback
        mov     ecx, [pool_cpulen]     ; record count = bytes / 32
        shr     ecx, 5
        lea     rsi, [pool_cpubuf]
        xor     ebx, ebx               ; physical-core tally
        xor     edx, edx               ; record index
  .scan:
        cmp     edx, ecx
        jae     .have
        mov     eax, edx
        shl     eax, 5                 ; rec = base + idx*32
        cmp     dword [rsi+rax+LPI_REL], RELATION_PROCESSOR_CORE
        jne     .next
        inc     ebx
  .next:
        inc     edx
        jmp     .scan
  .have:
        test    ebx, ebx
        jz      .fallback
        mov     eax, ebx
        jmp     .clamp
  .fallback:
        lea     rcx, [pool_sysinfo]
        call    [GetSystemInfo]
        mov     eax, dword [pool_sysinfo+SI_NPROC]
        test    eax, eax
        jnz     .clamp
        mov     eax, 1                 ; last resort
  .clamp:
        cmp     eax, POOL_MAX
        jbe     .lo
        mov     eax, POOL_MAX
  .lo:
        test    eax, eax
        jnz     .out
        mov     eax, 1
  .out:
        add     rsp, 40
        pop     rdi
        pop     rsi
        pop     rbx
        pop     rbp
        ret

; ------------------------------------------------------------------
; pool_init — create the persistent worker pool once (main is worker 0).
;   in:       ecx = requested worker count (0 = auto: physical cores)
;   out:      eax = actual worker count T (>= 1), or -1 on failure
;   clobbers: Win64 volatiles; preserves rbx/rsi/rdi/r12-r14 (and rbp)
;   MXCSR:    untouched (integer only)
; ------------------------------------------------------------------
; Creates T-1 auto-reset {go,done} event pairs and T-1 worker threads parked on
; their go events. Re-init safe: an existing pool is torn down first, so the
; harness can rebuild at a different T. On any failure the partial pool is torn
; down and -1 returned (fail-closed).
pool_init:
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        push    rdi
        push    r12
        push    r13
        push    r14
        sub     rsp, 48                ; shadow (32) + CreateThread stack args (16)
        mov     r14d, ecx              ; requested (save)
        cmp     dword [pool_nthreads], 0
        je      .fresh
        call    pool_shutdown          ; tear an existing pool down first
  .fresh:
        mov     dword [pool_stopflag], 0
        test    r14d, r14d
        jnz     .have_t
        call    pool_detect_cores      ; 0 => auto
        mov     r14d, eax
  .have_t:
        cmp     r14d, POOL_MAX         ; clamp T to [1, POOL_MAX]
        jbe     .t_hi_ok
        mov     r14d, POOL_MAX
  .t_hi_ok:
        test    r14d, r14d
        jnz     .t_lo_ok
        mov     r14d, 1
  .t_lo_ok:
        mov     [pool_nthreads], r14d
        mov     eax, r14d
        dec     eax
        mov     [pool_nworkers], eax   ; T-1
        mov     esi, 1                 ; spawn workers 1..T-1
  .spawn:
        cmp     esi, r14d
        jae     .ok
        xor     ecx, ecx               ; go[w] = CreateEventW(0, FALSE, FALSE, 0)
        xor     edx, edx               ;   bManualReset = FALSE => auto-reset
        xor     r8d, r8d
        xor     r9d, r9d
        call    [CreateEventW]
        test    rax, rax
        jz      .fail
        lea     rcx, [pool_go]
        mov     [rcx+rsi*8], rax
        xor     ecx, ecx               ; done[w] = CreateEventW(0, FALSE, FALSE, 0)
        xor     edx, edx
        xor     r8d, r8d
        xor     r9d, r9d
        call    [CreateEventW]
        test    rax, rax
        jz      .fail
        lea     rcx, [pool_done]
        mov     [rcx+rsi*8], rax
        xor     ecx, ecx               ; thread[w] = CreateThread(0, 0,
        xor     edx, edx               ;   pool_worker_thread, w, 0, 0)
        lea     r8, [pool_worker_thread]
        mov     r9d, esi               ; lpParameter = w
        mov     qword [rsp+32], 0      ; dwCreationFlags
        mov     qword [rsp+40], 0      ; lpThreadId = NULL
        call    [CreateThread]
        test    rax, rax
        jz      .fail
        lea     rcx, [pool_threads]
        mov     [rcx+rsi*8], rax
        inc     esi
        jmp     .spawn
  .ok:
        mov     eax, [pool_nthreads]
        jmp     .ret
  .fail:
        call    pool_shutdown          ; teardown whatever was created
        mov     eax, -1
  .ret:
        add     rsp, 48
        pop     r14
        pop     r13
        pop     r12
        pop     rdi
        pop     rsi
        pop     rbx
        pop     rbp
        ret

; ------------------------------------------------------------------
; pool_shutdown — signal all workers to exit, join them, close every handle.
;   in:       nothing
;   out:      the pool is empty (pool_nthreads = 0). No-op when none exists;
;             safe on a partially-created pool (null handle slots are skipped)
;   clobbers: Win64 volatiles; preserves rbx/rsi (and rbp)
;   MXCSR:    untouched (integer only)
; ------------------------------------------------------------------
pool_shutdown:
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        sub     rsp, 32
        mov     esi, [pool_nthreads]
        test    esi, esi
        jz      .done0
        mov     dword [pool_stopflag], 1
        mov     ebx, 1                 ; wake every parked worker
  .wake:
        cmp     ebx, esi
        jae     .join
        lea     rax, [pool_go]
        mov     rcx, [rax+rbx*8]
        test    rcx, rcx
        jz      .wake_next
        call    [SetEvent]
  .wake_next:
        inc     ebx
        jmp     .wake
  .join:
        mov     ebx, 1                 ; join each created thread
  .jloop:
        cmp     ebx, esi
        jae     .close
        lea     rax, [pool_threads]
        mov     rcx, [rax+rbx*8]
        test    rcx, rcx
        jz      .jnext
        mov     edx, -1
        call    [WaitForSingleObject]
  .jnext:
        inc     ebx
        jmp     .jloop
  .close:
        mov     ebx, 1                 ; close + null every handle
  .cloop:
        cmp     ebx, esi
        jae     .reset
        lea     rax, [pool_threads]
        mov     rcx, [rax+rbx*8]
        test    rcx, rcx
        jz      .c_go
        call    [CloseHandle]
        lea     rax, [pool_threads]
        mov     qword [rax+rbx*8], 0
  .c_go:
        lea     rax, [pool_go]
        mov     rcx, [rax+rbx*8]
        test    rcx, rcx
        jz      .c_done
        call    [CloseHandle]
        lea     rax, [pool_go]
        mov     qword [rax+rbx*8], 0
  .c_done:
        lea     rax, [pool_done]
        mov     rcx, [rax+rbx*8]
        test    rcx, rcx
        jz      .c_next
        call    [CloseHandle]
        lea     rax, [pool_done]
        mov     qword [rax+rbx*8], 0
  .c_next:
        inc     ebx
        jmp     .cloop
  .reset:
        mov     dword [pool_nthreads], 0
        mov     dword [pool_nworkers], 0
        mov     dword [pool_stopflag], 0
  .done0:
        add     rsp, 32
        pop     rsi
        pop     rbx
        pop     rbp
        ret

; ------------------------------------------------------------------
; pool_storage — the pool's mutable platform state. Emitted by each shell inside
; its own writable data section (kernel purity is untouched: this is platform
; state, never the arena and never in src/kernel/). Handle/range arrays are
; indexed by worker id w (slot 0 is the main thread and is unused for events).
; The reserved (rq/rd/rb) tail is zero at load, so the null-handle checks in
; pool_shutdown are valid before the first pool_init.
; ------------------------------------------------------------------
macro pool_storage {
        align 8
  pool_nthreads dd 0                   ; T (workers incl. main worker 0)
  pool_nworkers dd 0                   ; T-1 (created threads)
  pool_stopflag dd 0                   ; workers exit their park loop when set
  pool_cpulen   dd 0                   ; GetLogicalProcessorInformation length
  pool_arena    dq 0                   ; the frame's arena, published to workers
  pool_first    rd POOL_MAX            ; per-worker range start
  pool_last     rd POOL_MAX            ; per-worker range end (exclusive)
  pool_go       rq POOL_MAX            ; per-worker wake event (auto-reset)
  pool_done     rq POOL_MAX            ; per-worker completion event (auto-reset)
  pool_threads  rq POOL_MAX            ; per-worker thread handle
        align 16
  pool_sysinfo  rb 64                  ; SYSTEM_INFO scratch (fallback path)
  pool_cpubuf   rb 8192                ; SYSTEM_LOGICAL_PROCESSOR_INFORMATION[]
}
