; layout.inc — the arena size function (docs/MASTERPLAN.md, decisions 1 & 3).
;
; The layout is part of the ABI contract (abi.inc): any change to the result
; for the same params is an ABI version bump. The arena, in order:
;
;   header        512 B  (magic, version, path id, frame, RNG state, cached
;                         padded_n/g, and the 304-B validated params copy;
;                         offsets in abi.inc AH_*)
;   12 particle arrays   x,y,vx,vy f32 + species,id u32, banks IN and OUT,
;                        each padded_n * 4 B, 64-aligned by construction
;   cellid        padded_n * 4 B  (per-particle cell id, M2)
;   cell_start    (g*g + 1) * 4 B (exclusive prefix ends, M2)
;
; padded_n = ((n+15) & ~15) + 16 — the explicit +16 tail is load-bearing:
; without it, n = 0 mod 16 (including the headline 2^20) would get zero pad
; and unmasked tail loads would read out of bounds.
;
; g = the largest power of two with 1/g >= rmax, clamped to [4, 512]
; (decision 3). rmax <= 0.25 guarantees g = 4 qualifies; the doubling
; compares positive-f32 bit patterns as integers (order-identical for
; positive floats) and halves 1/(2g) by an exponent decrement — both exact,
; no FP instruction needed.

; ------------------------------------------------------------------
; arena_dims_core — padded_n and grid g for VALIDATED params (no revalidation).
;   in:       rcx = SwarmParams* (already validated by the caller)
;   out:      eax = padded_n = ((n+15) & ~15) + 16
;             edx = g (power of two in [4, 512])
;   clobbers: rax, rdx, r8, r9, flags
;   MXCSR:    untouched (integer bit comparisons only — see the g note above)
;   align:    none required
; ------------------------------------------------------------------
arena_dims_core:
        mov     eax, [rcx+SP_N]         ; padded_n = ((n+15) & ~15) + 16
        add     eax, 15
        and     eax, -16
        add     eax, 16

        mov     r8d, [rcx+SP_RMAX]      ; rmax bits: validated normal positive,
        mov     edx, 4                  ;   so integer order = float order
        mov     r9d, 0x3E000000         ; f32 bits of 1/(2*4)
  .grow:
        cmp     edx, 512
        jae     .done
        cmp     r9d, r8d                ; while 1/(2g) >= rmax: g *= 2
        jb      .done
        add     edx, edx
        sub     r9d, 0x00800000         ; exponent decrement: exact halving
        jmp     .grow
  .done:
        ret

; ------------------------------------------------------------------
; layout_bytes_core — arena bytes for validated params, else 0.
;   in:       rcx = SwarmParams* (caller-owned, 304 bytes)
;   out:      rax = total arena bytes (a multiple of 64), or 0 when the
;             params fail validation (fail-closed)
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, xmm0, xmm1, flags
;             (kernel tier: every register caller-owned)
;   MXCSR:    assumes the pinned seam MXCSR (validation compares under DAZ)
;   align:    none required — scalar FP only (movss/comiss), so it tolerates
;             both the seam's 0-mod-32 entry and init_core's 8-mod-32 nested call
; ------------------------------------------------------------------
layout_bytes_core:
        push    rcx
        call    pp_validate_params
        pop     rcx
        test    eax, eax
        jnz     .invalid

        call    arena_dims_core         ; eax = padded_n, edx = g
        mov     r8d, eax                ; padded_n (zero-extended)
        imul    edx, edx                ; g*g <= 262144: no overflow
        mov     eax, edx                ; g*g, zero-extended to rax
        mov     rdx, r8                 ; padded_n
        imul    rdx, AR_ARRAYS*4        ; 13 arrays (12 particle + cellid) * 4
        lea     rax, [rdx+rax*4+AH_SIZE+4] ; + cell_start (g*g+1)*4 + header
        add     rax, 63                 ; round the total to the 64-B unit the
        and     rax, -64                ;   allocator promises
        ret

  .invalid:
        xor     eax, eax
        ret
