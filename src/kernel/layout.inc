; SPDX-License-Identifier: MIT
; layout.inc - the arena size function (docs/MASTERPLAN.md, decisions 1 & 3).
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
;   cell_start    (g*gx + 1) * 4 B (exclusive prefix ends, M2), where
;                 gx = g << GRID_COL_SHIFT is the column count (#334)
;
; padded_n = ((n+15) & ~15) + 16 - the explicit +16 tail is load-bearing:
; without it, n = 0 mod 16 (including the headline 2^20) would get zero pad
; and unmasked tail loads would read out of bounds.
;
; g = the largest power of two with 1/g >= rmax, clamped to [4, GRID_G_MAX]
; (decision 3): the cell edge 1/g must be at least rmax so the 3x3
; neighbourhood covers the cutoff. rmax <= 0.25 guarantees g = 4 qualifies
; (1/4 = 0.25 >= rmax always); the loop grows g while the NEXT candidate
; still qualifies, so the per-step check compares 1/(2g) (the doubled g's
; edge) to rmax, not the current g's own invariant. The doubling compares
; positive-f32 bit patterns as integers (order-identical for positive
; floats) and halves 1/(2g) by an exponent decrement - both exact, no FP
; instruction needed.

GRID_G_MAX = 512                        ; the cell-dimension ceiling (decision 3).
                                        ; grid.inc asserts its own scratch block
                                        ; is sized from this, so raising it here
                                        ; reddens the build rather than silently
                                        ; overrunning a fixed-size histogram.

; #334 EXPERIMENT BUILD - the sort's columns refined below rmax. Rows stay at
; g (cell height 1/g >= rmax) and columns are binned at gx = g << GRID_COL_SHIFT
; (cell width rmax/C, C = GRID_COL_HALF). The force loop's column window widens
; to [cx-C, cx+C], so the walk still covers the disc, and a row's fine columns
; are one contiguous span of the sorted bank, so the run set stays decision 3's
; three runs. Every g*g in the tree becomes g*gx through these two names;
; GRID_COL_SHIFT = 0 restores the shipped square grid bit for bit.
GRID_COL_SHIFT = 2                      ; gx = g << 2: four fine columns per rmax
GRID_COL_HALF  = 1 shl GRID_COL_SHIFT   ; C: fine columns walked either side of cx

; ------------------------------------------------------------------
; arena_dims_core - padded_n and grid g for VALIDATED params (no revalidation).
;   in:       rcx = SwarmParams* (already validated by the caller)
;   out:      eax = padded_n = ((n+15) & ~15) + 16
;             edx = g (power of two in [4, GRID_G_MAX])
;   clobbers: rax, rdx, r8, r9, flags
;   MXCSR:    untouched (integer bit comparisons only - see the g note above)
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
        cmp     edx, GRID_G_MAX
        jae     .done
        cmp     r9d, r8d                ; while 1/(2g) >= rmax: g *= 2
        jb      .done
        add     edx, edx
        sub     r9d, 0x00800000         ; exponent decrement: exact halving
        jmp     .grow
  .done:
        ret

; ------------------------------------------------------------------
; layout_bytes_core - arena bytes for validated params, else 0.
;   in:       rcx = SwarmParams* (caller-owned, 304 bytes)
;   out:      rax = total arena bytes (a multiple of 64), or 0 when the
;             params fail validation (fail-closed)
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, xmm0, xmm1, flags
;             (kernel tier: every register caller-owned)
;   MXCSR:    assumes the pinned seam MXCSR (validation compares under DAZ)
;   align:    none required - scalar FP only (movss/comiss), so it tolerates
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
        shl     edx, GRID_COL_SHIFT     ; cells = g*gx <= 1048576 (#334)
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
