; state.inc — the id-ordered copy-out (docs/MASTERPLAN.md, decision 5).
;
; The arena is opaque to consumers: state crosses the seam by copy-out in
; original-id order, so the internal SoA/bank layout stays private. Every sort
; (M2) permutes the internal order but carries id[] along; scattering by id[i]
; restores the caller's 0..n-1 indexing. Right after init id[i] = i, so the
; copy is the identity — but the id indirection is what keeps the oracle test
; runnable once real sorts land.
;
; read_state reads bank OUT, which always holds the current state: init seeds
; OUT, and each step's fused pass writes OUT (build sorts OUT -> IN, pass reads
; IN and writes OUT).

; ------------------------------------------------------------------
; copy_scatter — dst[id[i]] = src[i] for i in 0..n-1 (one dword component).
;   in:       rcx = src (dword array), rdx = dst (dword array), r8 = id array
;             (u32), r9d = n
;   out:      dst scattered by id; an id outside [0, n) stores nothing
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, flags
;   note:     dword copy — bit-identical for f32 or u32 components.
;   precond:  id[0..n-1] should be a permutation of 0..n-1, but the scatter no
;             longer trusts it. dst is the CALLER's size-n array, reached across
;             the DLL seam, so an id >= n writes outside every bound the caller
;             can assert; the pads at id[n..padded_n) really do hold ids >= n
;             (init seeds them n, n+1, ...), so one off-by-one in a future sort
;             is all that separates the identity case from caller heap
;             corruption. The store is therefore bound-checked and skipped when
;             the check fails — fail closed, never perform an unproven write.
;             The compare is unsigned, so a stale or negative-as-unsigned word
;             is rejected by the same branch. The invariant itself stays pinned
;             by the harness (StateTests.IdOutStaysAPermutation), which is what
;             keeps this branch unreachable on every path the kernel produces.
; ------------------------------------------------------------------
copy_scatter:
        test    r9d, r9d
        jz      .done
        mov     r11d, r9d               ; n stays live as the id bound
  .loop:
        mov     eax, [r8]               ; oid = id[i]
        cmp     eax, r11d
        jae     .skip                   ; oid >= n: reject, never store past dst
        mov     r10d, [rcx]             ; value = src[i]
        mov     [rdx+rax*4], r10d       ; dst[oid] = value
  .skip:
        add     rcx, 4
        add     r8, 4
        dec     r9d
        jnz     .loop
  .done:
        ret

; scatter_component — copy OUT array `idx` (0 x, 1 y, 2 vx, 3 vy, 4 species)
; to the destination pointer `dst`, scattered by id_out. Assumes rbx = arena
; and clobbers only volatiles (copy_scatter preserves the nonvolatile dst/base
; registers). id_out is component 5 (AR_ID_OUT).
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
}
