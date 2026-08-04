; state.inc - the id-ordered copy-out (docs/MASTERPLAN.md, decision 5).
;
; The arena is opaque to consumers: state crosses the seam by copy-out in
; original-id order, so the internal SoA/bank layout stays private. Every sort
; (M2) permutes the internal order but carries id[] along; scattering by id[i]
; restores the caller's 0..n-1 indexing. Right after init id[i] = i, so the
; copy is the identity - but the id indirection is what keeps the oracle test
; runnable once real sorts land.
;
; read_state reads bank OUT, which always holds the current state: init seeds
; OUT, and each step's fused pass writes OUT (build sorts OUT -> IN, pass reads
; IN and writes OUT).

; ------------------------------------------------------------------
; copy_scatter - dst[id[i]] = src[i] for i in 0..n-1 (one dword component).
;   in:       rcx = src (dword array), rdx = dst (dword array), r8 = id array
;             (u32), r9d = n
;   out:      dst scattered by id; eax = 0 when every id was in range, 1 when
;             at least one was rejected and its store dropped
;   clobbers: rax, rcx, r8, r9, r10, r11, flags. The REGISTER rdx is never
;             written: it is the base of `mov [rdx+rax*4], r10d`, so the
;             routine writes through it and not to it. rbx carries the
;             rejection flag across the loop and is saved/restored, so every
;             nonvolatile is preserved
;   note:     dword copy - bit-identical for f32 or u32 components.
;   align:    called with rsp = 8 (mod 16) rather than the Win64-conforming 0,
;             so entered with rsp = 0 (mod 16) rather than 8 - the sole caller
;             (swarm_read_state) pushes rbp plus seven nonvolatiles before the
;             call. That caller also reserves no 32-byte shadow space, which is
;             safe because this routine never homes its register args. Both
;             deviations hold only because this is a bare leaf: it issues no
;             onward call and touches no alignment-sensitive instruction (no
;             movaps/movdqa, no xmm/ymm at all, no aligned spill), and its only
;             stack traffic is push/pop rbx, which leaves rsp = 8 (mod 16)
;             through the loop. Anything added here that calls out or spills a
;             vector register must realign first (sub rsp, 8) - from 8 (mod 16)
;             a call site needs 0 - and needs the caller to reserve shadow
;             space, or the callee's own homing stores and aligned spills land
;             on the caller's frame.
;   bound:    id[0..n-1] should be a permutation of 0..n-1, and the scatter no
;             longer trusts it. dst is the CALLER's size-n array, reached across
;             the DLL seam, so an id >= n writes outside every bound the caller
;             can assert; the pads at id[n..padded_n) really do hold ids >= n
;             (init seeds them n, n+1, ...), so one off-by-one in a future sort
;             is all that separates the identity case from caller heap
;             corruption. The store is therefore bound-checked and dropped when
;             the check fails - fail closed, never perform an unproven write -
;             and the drop is REPORTED, so a caller is never handed a silently
;             partial copy-out. The compare is unsigned, so a stale or
;             negative-as-unsigned word is rejected by the same branch. The
;             invariant itself stays pinned by the harness
;             (StateTests.IdOutStaysAPermutation), which is what keeps this
;             branch unreachable on every path the kernel produces.
; ------------------------------------------------------------------
copy_scatter:
        xor     eax, eax                ; nothing rejected (also the n = 0 answer)
        test    r9d, r9d
        jz      .done
        push    rbx
        xor     ebx, ebx
        mov     r11d, r9d               ; n stays live as the id bound
  .loop:
        mov     eax, [r8]               ; oid = id[i]
        cmp     eax, r11d
        jae     .reject                 ; oid >= n: never store past dst
        mov     r10d, [rcx]             ; value = src[i]
        mov     [rdx+rax*4], r10d       ; dst[oid] = value
  .next:
        add     rcx, 4
        add     r8, 4
        dec     r9d
        jnz     .loop
        mov     eax, ebx                ; report whether any id was rejected
        pop     rbx
  .done:
        ret
  .reject:                              ; out of line: a valid permutation never
        mov     ebx, 1                  ; comes here, so the copy path keeps its
        jmp     .next                   ; straight-line shape

; ------------------------------------------------------------------
; scatter_component - copy OUT array `idx` (0 x, 1 y, 2 vx, 3 vy, 4 species)
; to the destination pointer `dst`, scattered by id_out (component 5,
; AR_ID_OUT). A macro, so the source-scan contract check that covers column-0
; labels never reaches it - the fields below are the contract regardless.
;   in:       rbx = arena, r15d = the rejection flag accumulated so far,
;             `dst` = the operand holding the destination array pointer
;   out:      dst[id_out[i]] = OUT[idx][i] for every in-range id;
;             r15d |= 1 if copy_scatter rejected an id in this component
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, flags (the volatiles) and r15d
;             as the stated accumulator; every other nonvolatile is preserved,
;             so the caller's dst/base registers survive the call
;   align:    expands inline, so it inherits the caller's rsp and adds no frame
;             of its own; see copy_scatter's align note for the 8 (mod 16) call
;             site this reaches it with
; ------------------------------------------------------------------
macro scatter_component idx, dst {
        mov     r10d, [rbx+AH_PADDED]   ; padded_n
        shl     r10d, 2                 ; stride = padded_n * 4
        lea     rcx, [rbx+AH_SIZE]      ; src = bank OUT base + idx*stride
      if idx
        imul    rax, r10, idx
        add     rcx, rax
      end if
        imul    rax, r10, AR_ID_OUT     ; id_out = base + 5*stride
        lea     r8, [rbx+AH_SIZE+rax]
        mov     rdx, dst
        mov     r9d, [rbx+AH_PARAMS+SP_N]
        call    copy_scatter
        or      r15d, eax               ; a rejection anywhere fails the call
}
