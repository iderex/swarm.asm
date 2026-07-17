; cpuid.inc — SIMD path detection (docs/MASTERPLAN.md, decision 7).
;
; Detection happens once (at swarm_init) and the result is cached in the arena
; header; nothing ever falls through to an unsupported instruction mid-run.
; AVX needs both the CPU feature bits AND OS support for the YMM/ZMM state
; (OSXSAVE + XCR0), so the OS-enable check via xgetbv is mandatory — the CPU
; feature flag alone is not sufficient.

; ------------------------------------------------------------------
; cpu_paths_core — which SIMD code paths this CPU + OS support.
;   in:       nothing
;   out:      eax = CPU_AVX2 (bit0) set when AVX2+FMA usable;
;             | CPU_AVX512 (bit1) set when AVX-512 F+DQ+VL usable
;   clobbers: rax, rbx, rcx, rdx, r9, r10, r11, flags (kernel tier; rbx is
;             saved+restored here so a direct seam-less export is Win64-safe).
;             r9 accumulates the result, r10 stashes leaf-1 ecx (FMA), r11
;             stashes XCR0.
;   MXCSR:    untouched (integer only)
;   align:    none required
; ------------------------------------------------------------------
cpu_paths_core:
        push    rbx                     ; cpuid clobbers rbx; keep the leaf
        xor     r9d, r9d                ;   self-contained (seam saves it too)

        ; --- CPUID leaf 1: OSXSAVE (ecx bit 27), FMA (ecx bit 12) ---
        mov     eax, 1
        xor     ecx, ecx
        cpuid
        test    ecx, 1 shl 27           ; OSXSAVE: XGETBV usable + OS uses XSAVE
        jz      .done                   ; no OS AVX state -> no vector path
        mov     r10d, ecx               ; stash leaf-1 ecx (FMA bit)

        ; --- XCR0: XMM (bit1) + YMM (bit2) enabled by the OS? ---
        xor     ecx, ecx
        xgetbv                          ; -> edx:eax = XCR0
        mov     r11d, eax
        and     eax, 6                  ; bits 1 (SSE) and 2 (AVX)
        cmp     eax, 6
        jne     .done                   ; OS has not enabled YMM state

        ; --- CPUID leaf 7.0: AVX2 (ebx bit5), AVX-512 F/DQ/VL ---
        mov     eax, 7
        xor     ecx, ecx
        cpuid
        test    ebx, 1 shl 5            ; AVX2
        jz      .done                   ; FMA present on every AVX2 part we target
        test    r10d, 1 shl 12          ; require FMA explicitly (fused kernel)
        jz      .done
        or      r9d, CPU_AVX2

        ; AVX-512 also needs the OS to have enabled the OPMASK/ZMM state:
        ; XCR0 bits 5 (opmask), 6 (ZMM_Hi256), 7 (Hi16_ZMM) = 0xE0.
        mov     eax, r11d
        and     eax, 0xE0
        cmp     eax, 0xE0
        jne     .done
        mov     eax, 1 shl 16           ; AVX-512F (ebx bit16)
        or      eax, 1 shl 17           ; AVX-512DQ (bit17)
        or      eax, 1 shl 31           ; AVX-512VL (bit31)
        and     ebx, eax
        cmp     ebx, eax
        jne     .done
        or      r9d, CPU_AVX512

  .done:
        mov     eax, r9d
        pop     rbx
        ret
