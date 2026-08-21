; SPDX-License-Identifier: MIT
; frametime.inc - reduce a capture run's raw work-window ticks to the four
; figures docs/BENCHMARKS.md publishes.
;
; Platform tier, and pure: no API call, no import, no simulation state. It is
; not in src/kernel/ because the kernel is the physics and a measurement
; reduction is no part of that. Both build targets include this file, so the
; routine swarm.exe reports with is the routine the harness tests - decision
; 5's shape applied to the instrument rather than to the engine. Without that,
; the reduction would live only inside an executable nothing can call into, and
; the one thing it can get wrong - which element a percentile names - would be
; asserted by nothing.
;
; THE PERCENTILE DEFINITION IS NOT CHOSEN HERE. docs/BENCHMARKS.md carries the
; snippet every recorded figure in this repository was recomputed with, and
; this is that definition in integer arithmetic over the raw ticks:
;
;     p50  = sorted[floor(0.50 * (count - 1))]
;     p99  = sorted[floor(0.99 * (count - 1))]
;     max  = sorted[count - 1]
;     mean = sum / count
;
; A second definition would put the exe and the document at odds about the same
; dump, which is the failure this file exists to make impossible. The index is
; taken as floor(P * (count - 1) / 100) in integers - the same real number, and
; without the binary-fraction error the double form carries. The two forms can
; part by one index where 100 divides P * (count - 1) exactly and the double
; product lands just under the integer; at CAPTURE_FRAMES = 3600 neither
; product divides (50 * 3599 = 179950, 99 * 3599 = 356301), so they agree on
; every dump this instrument produces.
;
; Sorting ticks rather than milliseconds moves no element: ticks to
; milliseconds is multiplication by a positive constant, so the order is the
; same one and the conversion is left until after the index is chosen.

; ------------------------------------------------------------------
; frame_sift_down - restore the max-heap property from one root downwards.
;   in:       rcx = u64* base, edx = root index, r8d = heap size (only
;             [base, base+size) participates; anything above it is placed)
;   out:      nothing; the subtree at root satisfies the max-heap property
;   clobbers: rax, rdx, r10, r11, flags. rcx, r8 and r9 are PRESERVED, which is
;             what lets frame_sort_u64 keep both loop variables across the call
;   MXCSR:    untouched (integer only)
;   ABI:      internal, not the Win64 seam. It is a leaf - it issues no call -
;             so it reserves no shadow space and establishes no 16-byte
;             alignment. A callee added here would owe both.
;   bound:    size < 2^31, so 2*root+2 cannot wrap. frame_stats_core refuses a
;             count at or above that before any of this runs.
; ------------------------------------------------------------------
frame_sift_down:
  .down:
        mov     r10d, edx               ; largest = root
        mov     r11d, edx
        add     r11d, r11d
        inc     r11d                    ; left = 2*root + 1
        cmp     r11d, r8d
        jae     .done                   ; no left child: root is a leaf
        mov     rax, [rcx+r11*8]
        cmp     rax, [rcx+r10*8]
        jbe     .right
        mov     r10d, r11d
  .right:
        inc     r11d                    ; right = 2*root + 2
        cmp     r11d, r8d
        jae     .swap
        mov     rax, [rcx+r11*8]
        cmp     rax, [rcx+r10*8]
        jbe     .swap
        mov     r10d, r11d
  .swap:
        cmp     r10d, edx
        je      .done                   ; the root already dominates both
        mov     rax, [rcx+rdx*8]
        mov     r11, [rcx+r10*8]
        mov     [rcx+rdx*8], r11
        mov     [rcx+r10*8], rax
        mov     edx, r10d               ; follow the element down
        jmp     .down
  .done:
        ret

; ------------------------------------------------------------------
; frame_sort_u64 - ascending in-place heapsort over unsigned 64-bit samples.
;   in:       rcx = u64* base, edx = count
;   out:      [base, base+count) is ascending; it is a permutation of what it
;             held, since every step is a swap
;   clobbers: rax, rdx, r8, r9, r10, r11, flags. rcx is PRESERVED
;   MXCSR:    untouched (integer only)
;   ABI:      internal, not the Win64 seam. Its callee is a leaf that homes
;             nothing, so no shadow space is reserved and no alignment is
;             established here either.
;   why heap: the reduction runs once, at the end of a capture, so the constant
;             factor buys nothing and only the growth matters. An insertion
;             sort is shorter and quadratic, which turns a raised
;             CAPTURE_FRAMES into a stall nobody would attribute to the sort.
;             n log n, no recursion and no scratch buffer costs about twenty
;             instructions more and removes that trap.
; ------------------------------------------------------------------
frame_sort_u64:
        cmp     edx, 2
        jb      .done                   ; 0 or 1 element is already sorted
        mov     r8d, edx                ; heap size = count
        mov     r9d, edx
        shr     r9d, 1                  ; one past the last internal node
  .build:
        dec     r9d
        mov     edx, r9d
        call    frame_sift_down
        test    r9d, r9d
        jnz     .build
        mov     r9d, r8d                ; end = count
  .extract:
        dec     r9d                     ; the largest goes to the end, which
        jz      .done                   ;   leaves the heap one shorter
        mov     rax, [rcx]
        mov     r10, [rcx+r9*8]
        mov     [rcx], r10
        mov     [rcx+r9*8], rax
        mov     r8d, r9d
        xor     edx, edx
        call    frame_sift_down
        jmp     .extract
  .done:
        ret

; ------------------------------------------------------------------
; frame_ticks_to_us - QPC ticks to whole microseconds, rounded to nearest.
;   in:       rax = ticks, r13 = ticks per second (non-zero)
;   out:      CF clear and rax = microseconds; CF set and rax undefined where
;             the quotient would not fit in 64 bits
;   clobbers: rax, rcx, rdx, flags
;   MXCSR:    untouched (integer only)
;   ABI:      internal leaf; no shadow space, no alignment established
;   rounding: half up, because the document's own snippet formats a double to
;             three decimals of a millisecond and therefore rounds rather than
;             truncates. Truncating here would put the exe a microsecond under
;             the document on about half of all samples.
;   note:     the CF path is what keeps a divide fault out of the process. It
;             needs a single figure past 1.8e13 seconds to trigger, which no
;             capture can produce; it costs two instructions, which is the same
;             trade the unreachable bounds guard in capture_frame takes.
; ------------------------------------------------------------------
frame_ticks_to_us:
        mov     rcx, 1000000
        mul     rcx                     ; rdx:rax = ticks * 1e6
        mov     rcx, r13
        shr     rcx, 1
        add     rax, rcx                ; + freq/2: round to nearest
        adc     rdx, 0
        cmp     rdx, r13
        jae     .overflow               ; the quotient would not fit
        div     r13
        clc
        ret
  .overflow:
        stc
        ret

; ------------------------------------------------------------------
; frame_stats_core - the four published figures of a capture run.
;   in:       rcx = u64* samples. SORTED IN PLACE, so a caller that wants the
;                   recorded order on disk must have written it already
;             edx = sample count
;             r8  = QueryPerformanceFrequency, ticks per second
;             r9  = u64* out, four slots: mean, p50, p99, max, each in whole
;                   microseconds
;   out:      eax = 0 and the four slots written; eax = 1 and out UNTOUCHED
;             where count is 0, count is 2^31 or more, the tick rate is 0, or a
;             conversion would overflow. Fail-closed: a caller that publishes
;             on eax = 1 publishes figures it was never handed. The sample
;             buffer may already be sorted when it refuses - only out is
;             promised untouched.
;   clobbers: volatile GPRs (rax, rcx, rdx, r8-r11) and flags; rbx, rsi, r12,
;             r13 and r14 are saved and restored. No FP, no XMM
;   MXCSR:    untouched (integer only), so this export wears no seam
;   ABI:      Win64. Every call it makes is internal and issues nothing onward,
;             so no shadow space is reserved and no 16-byte alignment is
;             established; a Win64 call added below would owe both.
;   bound:    the sum is a u64 over count samples. Overflowing it needs the run
;             to have spent 5.8e10 seconds inside the work window at a 10 MHz
;             tick rate, so it is out of reach rather than guarded.
; ------------------------------------------------------------------
frame_stats_core:
        push    rbx
        push    rsi
        push    r12
        push    r13
        push    r14
        test    edx, edx
        jz      .refuse                 ; no samples: no figures
        js      .refuse                 ; count >= 2^31: the heap index wraps
        test    r8, r8
        jz      .refuse                 ; no tick rate: no conversion
        mov     rsi, rcx                ; samples
        mov     r12d, edx               ; count, zero-extended
        mov     r13, r8                 ; ticks per second
        mov     r14, r9                 ; out
        xor     ebx, ebx                ; the sum, taken before the sort only
        xor     ecx, ecx                ;   to keep that read sequential
  .sum:
        add     rbx, [rsi+rcx*8]
        inc     ecx
        cmp     ecx, r12d
        jb      .sum
        mov     rcx, rsi                ; ascending order is what every index
        mov     edx, r12d               ;   below means
        call    frame_sort_u64
        mov     rax, rbx                ; mean = sum / count, in ticks first so
        xor     edx, edx                ;   the division rounds once
        div     r12
        call    frame_ticks_to_us
        jc      .refuse
        mov     r8, rax
        mov     ebx, r12d
        dec     ebx                     ; count - 1; the sum is spent
        imul    rax, rbx, 50            ; p50 index, floor(50*(count-1)/100)
        mov     ecx, 100
        xor     edx, edx
        div     rcx
        mov     rax, [rsi+rax*8]
        call    frame_ticks_to_us
        jc      .refuse
        mov     r9, rax
        imul    rax, rbx, 99            ; p99 index, floor(99*(count-1)/100)
        mov     ecx, 100
        xor     edx, edx
        div     rcx
        mov     rax, [rsi+rax*8]
        call    frame_ticks_to_us
        jc      .refuse
        mov     r10, rax
        mov     rax, [rsi+rbx*8]        ; max = sorted[count - 1]
        call    frame_ticks_to_us
        jc      .refuse
        mov     r11, rax
        mov     [r14],    r8            ; committed together, so a refusal
        mov     [r14+8],  r9            ;   above leaves the caller's slots as
        mov     [r14+16], r10           ;   they were
        mov     [r14+24], r11
        xor     eax, eax
        jmp     .ret
  .refuse:
        mov     eax, 1
  .ret:
        pop     r14
        pop     r13
        pop     r12
        pop     rsi
        pop     rbx
        ret
