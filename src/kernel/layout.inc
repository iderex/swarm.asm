; layout.inc — the arena size function (docs/MASTERPLAN.md, decisions 1 & 3).
;
; The layout is part of the ABI contract (abi.inc): any change to the result
; for the same params is an ABI version bump. The arena, in order:
;
;   header        256 B  (magic, version, params, RNG state, frame, path id)
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
; layout_bytes_core — arena bytes for validated params, else 0.
;   in:       rcx = SwarmParams* (caller-owned, 304 bytes)
;   out:      rax = total arena bytes (a multiple of 64), or 0 when the
;             params fail validation (fail-closed)
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, xmm0, xmm1, flags
;             (kernel tier: every register caller-owned)
;   MXCSR:    assumes the pinned seam MXCSR (validation compares under DAZ)
;   align:    rsp = 0 mod 32 at entry (seam contract)
; ------------------------------------------------------------------
layout_bytes_core:
        push    rcx
        call    pp_validate_params
        pop     rcx
        test    eax, eax
        jnz     .invalid

        mov     r8d, [rcx+SP_N]         ; padded_n = ((n+15) & ~15) + 16
        add     r8d, 15
        and     r8d, -16
        add     r8d, 16

        mov     eax, [rcx+SP_RMAX]      ; rmax bits: validated normal positive,
        mov     r9d, 4                  ;   so integer order = float order
        mov     r10d, 0x3E000000        ; f32 bits of 1/(2*4)
  .grow:
        cmp     r9d, 512
        jae     .gdone
        cmp     r10d, eax               ; while 1/(2g) >= rmax: g *= 2
        jb      .gdone
        add     r9d, r9d
        sub     r10d, 0x00800000        ; exponent decrement: exact halving
        jmp     .grow
  .gdone:

        imul    r9d, r9d                ; g*g <= 262144: no overflow
        mov     rax, r8                 ; padded_n (zero-extended)
        imul    rax, 52                 ; 13 arrays (12 particle + cellid) * 4
        lea     rax, [rax+r9*4+256+4]   ; + cell_start (g*g+1)*4 + header
        add     rax, 63                 ; round the total to the 64-B unit the
        and     rax, -64                ;   allocator promises
        ret

  .invalid:
        xor     eax, eax
        ret
