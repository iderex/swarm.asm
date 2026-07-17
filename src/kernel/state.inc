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
;   out:      dst scattered by id
;   clobbers: rax, rcx, rdx, r8, r9, r10, flags
;   note:     dword copy — bit-identical for f32 or u32 components.
;   precond:  id[0..n-1] MUST be a permutation of 0..n-1 (every id < n), so the
;             scatter stays inside the caller's size-n array. init guarantees
;             it (id[i]=i); the M2 sort must preserve it — a leaked pad/stale
;             slot with id >= n would be a silent OOB write into the caller.
; ------------------------------------------------------------------
copy_scatter:
        test    r9d, r9d
        jz      .done
  .loop:
        mov     eax, [r8]               ; oid = id[i]
        mov     r10d, [rcx]             ; value = src[i]
        mov     [rdx+rax*4], r10d       ; dst[oid] = value
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
