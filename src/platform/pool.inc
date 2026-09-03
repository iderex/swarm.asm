; SPDX-License-Identifier: MIT
; pool.inc - the M3 persistent worker pool (platform layer; docs/MASTERPLAN.md
; decision 6, issue #68). Parallelises the force+integrate pass across cores,
; and - since #243 - the counting-sort build's two O(n) phases as well, over the
; same threads and the same static split.
;
; Why this is platform, not kernel: the pool is pure OS orchestration
; (CreateThread / auto-reset events) and holds mutable state (handles, ranges).
; The KERNEL never sees a thread. For the pass a worker crosses the thread-entry
; seam and then calls the pure per-range `swarm_pass(arena, first, last)`
; semantics (here `pool_pass`); for the two build jobs it calls the pure
; per-range kernel routines directly, because those are integer-only and wear no
; FP seam - grid.inc's headers say MXCSR untouched, and the debug entry check
; below exists for the FP path and has nothing to prove about them. Kernel purity
; (no API calls, no writable state in src/kernel/) is preserved either way: this
; file lives in src/platform/ and is excluded from the kernel source scan.
;
; Determinism (the load-bearing invariant). The pass is a pure map
; OUT[i] = f(i, IN, cell_start, params): IN and cell_start are frozen by the
; build before any worker is signalled for the pass, ranges are disjoint and cover
; [0,n) exactly, so the OUT bank is bit-identical to the serial pass for ANY
; thread count, partition, and completion order - the machine-checked form of
; GridPassSplitInvariance. The one landmine is MXCSR: it is PER THREAD, so a
; worker running with the default control word would keep the denormals the
; pinned main thread flushes (FTZ/DAZ) and diverge. `pool_pass` is the seam
; wrapper over pass_core, so every worker pins its own MXCSR to 0x9FC0 and
; vzerouppers exactly as the exports do - the per-thread determinism seam.
;
; Distribution: STATIC even split, each range a multiple of 16 (16 consecutive
; f32 = one 64-byte line, so 16-aligned boundaries keep the seven disjoint OUT
; arrays off shared cache lines - no false sharing). Self-scheduling over a
; shared counter is the noted follow-up (the reference 5950X is homogeneous, so
; static-even has no imbalance to absorb and no lock xadd contention).
;
; Sync primitives are all genuinely kernel32 (CreateThread, CreateEventW,
; SetEvent, WaitForSingleObject, WaitForMultipleObjects, CloseHandle,
; GetLogicalProcessorInformation, GetSystemInfo). WaitOnAddress/WakeByAddress
; are deliberately NOT used - they forward through API-MS-Win-Core-Synch-* and
; would break the exe import allowlist.
;
; Reloc-safe by construction: every label reference is RIP-relative - code/data
; addresses passed as values use `lea reg, [label]` and array indexing loads the
; base with `lea` first (an absolute `[label + idx*s]` disp32 cannot hold the
; DLL image base and would need a base relocation this image does not carry).

POOL_MAX = 64                          ; worker cap (WaitForMultipleObjects
                                       ;   takes <= 64 handles: T-1 <= 63)
POOL_JOB_PASS    = 0                   ; the fan-out's three work kinds
POOL_JOB_HIST    = 1
POOL_JOB_SCATTER = 2
MEM_COMMIT_RESERVE = 0x3000            ; MEM_COMMIT | MEM_RESERVE
MEM_RELEASE        = 0x8000
PAGE_READWRITE     = 4
RELATION_PROCESSOR_CORE = 0            ; LOGICAL_PROCESSOR_RELATIONSHIP value
LPI_REL    = 8                         ; SYSTEM_LOGICAL_PROCESSOR_INFORMATION
                                       ;   .Relationship offset (record stride 32)
SI_NPROC   = 0x20                      ; SYSTEM_INFO.dwNumberOfProcessors offset

; ------------------------------------------------------------------
; pool_pass - the thread-entry seam over pass_core (per-thread MXCSR pin +
; Win64 nonvolatiles + 0-mod-32 kernel entry + vzeroupper). Both the main
; thread (as worker 0) and every worker thread run their sub-range through this
; single gate, so the per-thread FTZ/DAZ pin is impossible to miss.
;   in:       rcx arena, edx first, r8d last
;   out:      OUT[first..last) integrated from the frozen IN bank
;   clobbers: Win64 volatiles only; the seam saves/restores every nonvolatile
;             and MXCSR (identical contract to the swarm_pass export)
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return
;   debug:    every worker's entry into an FP core is checked here, and only
;             here. This is a seam_wrap, so it carries seam_assert_pinned like
;             every export; pool_worker_thread reaches pass_core through no other
;             route, so a second check at the thread entry would read the parked
;             thread's own word before the pin and prove nothing. The build jobs
;             enter no FP core and are checked nowhere, which is why grid.inc's
;             MXCSR-untouched line has to stay true by inspection
; ------------------------------------------------------------------
seam_wrap pool_pass, pass_core

; ------------------------------------------------------------------
; pool_worker_thread - CreateThread entry for worker w (1..T-1).
;   in:       ecx = worker index w (the LPVOID thread parameter; low 32 bits)
;   out:      eax = 0 at exit
;   clobbers: Win64 volatiles; preserves rbx/rsi/rbp (the nonvolatiles it uses)
;   MXCSR:    a pass job pins it via pool_pass; the park loop and both build jobs
;             are integer and leave it alone
; ------------------------------------------------------------------
; Parks on go[w]; on wake either exits (shutdown) or runs the job [pool_job]
; names over [first[w], last[w]) and signals done[w]. Three jobs: the frozen-IN
; pass, and the build's histogram and scatter over this worker's own scratch
; block. The job word is published before any wake, so a woken worker reads the
; one it was woken for. Auto-reset events: the wait consumes the signal, so no
; explicit ResetEvent.
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
        mov     esi, [pool_job]
        test    esi, esi
        jnz     .build_job
        mov     rcx, [pool_arena]      ; pool_pass(arena, first[w], last[w])
        lea     rax, [pool_first]
        mov     edx, [rax+rbx*4]
        lea     rax, [pool_last]
        mov     r8d, [rax+rbx*4]
        call    pool_pass
        jmp     .signal
  .build_job:                          ; grid_*_range(arena, &scratch[w],
        mov     rcx, [pool_arena]      ;   first[w], last[w])
        imul    rdx, rbx, GRID_CELLS_MAX*4
        add     rdx, [pool_scratch]
        lea     rax, [pool_first]
        mov     r8d, [rax+rbx*4]
        lea     rax, [pool_last]
        mov     r9d, [rax+rbx*4]
        cmp     esi, POOL_JOB_HIST
        jne     .scatter_job
        call    grid_hist_range
        jmp     .signal
  .scatter_job:
        call    grid_scatter_range
  .signal:
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
; pool_partition - static even 16-aligned split of [0,n) across W workers.
;   in:       ecx = n, edx = W (>= 1, <= pool_nthreads)
;   out:      pool_first[w], pool_last[w] for w in 0..W-1: disjoint, covering
;             [0,n) exactly, interior boundaries multiples of 16, last[W-1] = n.
;             Slots at or above W are left as they were and must not be read
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, flags; preserves rbx
;   MXCSR:    untouched (integer only)
; ------------------------------------------------------------------
; q = round_up_16(ceil(n/W)); first[w] = min(w*q, n); last[w] = min((w+1)*q, n).
; W <= 64 and n <= 2^20, so w*q < 2^21 - the 32-bit multiply cannot overflow.
pool_partition:
        push    rbx
        mov     r8d, ecx               ; n
        mov     r9d, edx               ; W
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
; pool_run_job - one fan-out of an already-partitioned range: wake workers, run
; worker 0's share on this thread, join. The single place a work kind is turned
; into W running copies of itself.
;   in:       rcx = arena, edx = POOL_JOB_*, r8d = W, the number of workers this
;             job is partitioned across (1 <= W <= pool_nthreads).
;             pool_first/pool_last[0..W) are filled and pool_arena is published
;             by the caller. Workers at or above W stay parked - they are never
;             woken, so their done events are never signalled and the join must
;             not wait on them
;   out:      every worker has finished its share of that job
;   clobbers: Win64 volatiles; preserves rbx/rsi/rdi/r12/r13 (and rbp)
;   MXCSR:    the pass pins it per thread via pool_pass; the two build jobs are
;             integer and pin nothing (grid.inc: MXCSR untouched)
; ------------------------------------------------------------------
pool_run_job:
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        push    rdi
        push    r12
        push    r13
        sub     rsp, 40                ; shadow space (all API calls <= 4 args)
        mov     rbx, rcx               ; arena
        mov     r13d, edx              ; job
        mov     [pool_job], r13d       ; publish before any worker is woken
        mov     r12d, r8d              ; W
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
        test    r13d, r13d             ; worker 0 runs on this thread
        jnz     .main_build
        mov     rcx, rbx               ; pool_pass(arena, first[0], last[0])
        lea     rax, [pool_first]
        mov     edx, [rax]
        lea     rax, [pool_last]
        mov     r8d, [rax]
        call    pool_pass
        jmp     .join
  .main_build:                         ; grid_*_range(arena, &scratch[0],
        mov     rcx, rbx               ;   first[0], last[0])
        mov     rdx, [pool_scratch]
        lea     rax, [pool_first]
        mov     r8d, [rax]
        lea     rax, [pool_last]
        mov     r9d, [rax]
        cmp     r13d, POOL_JOB_HIST
        jne     .main_scatter
        call    grid_hist_range
        jmp     .join
  .main_scatter:
        call    grid_scatter_range
  .join:
        cmp     r12d, 1                ; one worker: nothing to join
        jbe     .done
        mov     ecx, r12d              ; WaitForMultipleObjects(T-1,
        dec     ecx                    ;   &done[1], TRUE, INFINITE)
        lea     rdx, [pool_done]
        add     rdx, 8                 ; &done[1]: done[1..T-1] are contiguous
        mov     r8d, 1                 ; bWaitAll = TRUE
        mov     r9d, -1                ; INFINITE
        call    [WaitForMultipleObjects]
  .done:
        add     rsp, 40
        pop     r13
        pop     r12
        pop     rdi
        pop     rsi
        pop     rbx
        pop     rbp
        ret

; ------------------------------------------------------------------
; pool_fanout - one parallel pass over [0,n): partition, then fan the pass out.
; No build, no frame advance (the caller owns those). Assumes IN and cell_start
; are already frozen by the build.
;   in:       rcx = arena
;   out:      OUT[0..n) integrated
;   clobbers: Win64 volatiles only. rbx/rbp are saved here; rsi/rdi/r12/r13 by
;             pool_run_job; r14/r15 are never touched - so the whole nonvolatile
;             set survives, which swarm_pass_mt tail-jumps to this for
;   MXCSR:    the pass pins it per thread via pool_pass; fanout is integer
; ------------------------------------------------------------------
pool_fanout:
        push    rbp
        mov     rbp, rsp
        push    rbx
        sub     rsp, 40
        mov     rbx, rcx               ; arena
        mov     [pool_arena], rbx      ; publish for the workers
        mov     ecx, [rbx+AH_PARAMS+SP_N]
        mov     edx, [pool_nthreads]
        call    pool_partition         ; fill first/last[0..T-1]
        mov     rcx, rbx
        mov     edx, POOL_JOB_PASS
        mov     r8d, [pool_nthreads]   ; the pass scales with the whole pool
        call    pool_run_job
        add     rsp, 40
        pop     rbx
        pop     rbp
        ret

; ------------------------------------------------------------------
; pool_build - the grid build with its scatter across the worker pool
; (docs/MASTERPLAN.md, decision 6; risk 2's contingency).
;
; Four phases, the same four the serial sort runs, re-cut so the O(n) two are
; parallel: the pads are carried, every worker counts its own slice into its own
; histogram, one sweep turns the T histograms into T per-cell write cursors and
; publishes cell_start, and every worker scatters its own slice from its own
; cursors. Bit-identical to grid_sort for any T - the cursor split is what keeps
; equal-cell particles in ascending-i order (grid.inc says why).
;
; Falls back to the serial build, not to a wrong answer, whenever the parallel
; shape does not apply: brute mode has no sort at all, and a pool with no scratch
; - because the allocation failed at pool_init - has nowhere to put the
; histograms. T = 1 is NOT a fallback case -
; the one-worker fan-out runs the same three phases inline, which is what lets
; the harness sweep T = 1 as a real arm of BuildParallelMatchesSerial rather
; than as a comparison of the serial build against itself.
;
; THE BUILD DOES NOT USE THE WHOLE POOL, and the pass does. The two scale in
; opposite directions: adding a worker divides the O(n) histogram and scatter,
; and multiplies the O(g*g) cursor sweep, because every worker needs a count for
; every cell whether or not it holds a particle there. So the bound is the point
; where a worker's slice stops being bigger than its own bucket array:
;
;       W = clamp(n / (g*g), 1, T)
;
; A worker below that line spends more time walking buckets than particles, and
; past it the fan-out costs more than it saves. This is derived rather than
; tuned, and docs/BENCHMARKS.md records that it lands on the measured optimum at
; both dimensions the 1M scenes use - W = 4 at g = 512 and W = 16 at g = 256 -
; where the whole-pool fan-out was at or below break-even at g = 512.
;
;   in:       rcx = arena
;   out:      as build_core: IN pads carried, IN[0..n) cell-sorted, cell_start
;             filled
;   clobbers: Win64 volatiles only. rbx/rbp/r12 are saved here; rsi/rdi/r13 by
;             pool_run_job; r14/r15 are never touched - the same whole-set
;             promise build_core makes, since swarm_build_mt exports this
;   MXCSR:    untouched (the whole build is integer)
; ------------------------------------------------------------------
pool_build:
        mov     eax, [rcx+AH_PARAMS+SP_FLAGS]
        test    eax, FLAG_GRID
        jz      build_core             ; brute: nothing to parallelise
        cmp     dword [pool_nthreads], 1
        jb      build_core             ; no pool at all: nowhere to fan out to
        cmp     qword [pool_scratch], 0
        je      build_core             ; fail-closed: no scratch, no parallel sort
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    r12
        sub     rsp, 32                ; shadow space (all calls <= 4 args)
        mov     rbx, rcx               ; arena
        mov     [pool_arena], rbx      ; publish for the workers
        call    build_pads_core        ; the six pad tails OUT -> IN
        mov     ecx, [rbx+AH_G]        ; W = clamp(n / cells, 1, T)
        imul    ecx, ecx               ; g*g >= 16, so the divisor is never zero
        shl     ecx, GRID_COL_SHIFT    ; cells = g*gx (#334 shape)
        mov     eax, [rbx+AH_PARAMS+SP_N]
        xor     edx, edx
        div     ecx
        mov     r12d, eax
        test    r12d, r12d
        jnz     .w_lo_ok
        mov     r12d, 1
  .w_lo_ok:
        cmp     r12d, [pool_nthreads]
        jbe     .w_hi_ok
        mov     r12d, [pool_nthreads]
  .w_hi_ok:
        mov     ecx, [rbx+AH_PARAMS+SP_N]
        mov     edx, r12d
        call    pool_partition         ; one partition for BOTH build jobs: the
                                       ;   scatter must replay the histogram's
                                       ;   ranges exactly or the cursors lie
        mov     rcx, rbx
        mov     edx, POOL_JOB_HIST
        mov     r8d, r12d
        call    pool_run_job
        mov     rcx, rbx               ; grid_cursors(arena, scratch, W)
        mov     rdx, [pool_scratch]
        mov     r8d, r12d
        call    grid_cursors
        mov     rcx, rbx
        mov     edx, POOL_JOB_SCATTER
        mov     r8d, r12d
        call    pool_run_job
        add     rsp, 32
        pop     r12
        pop     rbx
        pop     rbp
        ret

; ------------------------------------------------------------------
; pool_pass_all - one parallel pass over [0,n), no build/frame advance. The
; benchmark seam: repeatable over a frozen IN bank, mirroring swarm_pass.
;   in:       rcx = arena
;   out:      OUT[0..n) integrated
;   clobbers: as pool_fanout (tail call)
;   MXCSR:    per-thread pin via pool_pass
; ------------------------------------------------------------------
pool_pass_all:
        jmp     pool_fanout

; ------------------------------------------------------------------
; pool_step - the threaded frame loop: parallel build, parallel pass, advance.
;   in:       rcx = arena, edx = n_steps
;   out:      the world advanced n_steps; AH_FRAME += n_steps. Bit-identical to
;             swarm_step for any T - the pass is split-invariant and the build's
;             per-worker cursors reproduce the serial sort's order exactly
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
        call    pool_build             ; OUT -> IN (+ grid sort, scatter fanned out)
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
; pool_detect_cores - physical core count, falling back to the logical count.
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
; pool_init - create the persistent worker pool once (main is worker 0).
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
        lea     eax, [r14+1]           ; T+1 blocks: one histogram per worker
        imul    rdx, rax, GRID_CELLS_MAX*4  ;   plus grid_cursors' workspace
        xor     ecx, ecx               ; VirtualAlloc(NULL, bytes,
        mov     r8d, MEM_COMMIT_RESERVE ;   MEM_COMMIT|MEM_RESERVE, PAGE_READWRITE)
        mov     r9d, PAGE_READWRITE
        call    [VirtualAlloc]         ; a failure here is NOT fatal: pool_scratch
        mov     [pool_scratch], rax    ;   stays 0, pool_build falls back to the
                                       ;   serial sort, and the pass is untouched.
                                       ;   Refusing the pool would turn a missing
                                       ;   optimisation into an exe that does not
                                       ;   start (swarm.asm exits on pool_init < 1)
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
; pool_shutdown - signal all workers to exit, join them, close every handle.
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
        mov     rcx, [pool_scratch]    ; VirtualFree(scratch, 0, MEM_RELEASE)
        test    rcx, rcx
        jz      .no_scratch
        xor     edx, edx
        mov     r8d, MEM_RELEASE
        call    [VirtualFree]
        mov     qword [pool_scratch], 0
  .no_scratch:
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
; pool_storage - the pool's mutable platform state. Emitted by each shell inside
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
  pool_job      dd 0                   ; POOL_JOB_*, published before each wake
  pool_pad0     dd 0                   ; keep the quadwords below 8-aligned
  pool_arena    dq 0                   ; the frame's arena, published to workers
  pool_scratch  dq 0                   ; (T+1) x GRID_CELLS_MAX dwords, or 0 when
                                       ;   the commit failed (the build then runs
                                       ;   serially; MEM_RELEASE needs no length)
  pool_first    rd POOL_MAX            ; per-worker range start
  pool_last     rd POOL_MAX            ; per-worker range end (exclusive)
  pool_go       rq POOL_MAX            ; per-worker wake event (auto-reset)
  pool_done     rq POOL_MAX            ; per-worker completion event (auto-reset)
  pool_threads  rq POOL_MAX            ; per-worker thread handle
        align 16
  pool_sysinfo  rb 64                  ; SYSTEM_INFO scratch (fallback path)
  pool_cpubuf   rb 8192                ; SYSTEM_LOGICAL_PROCESSOR_INFORMATION[]
}
