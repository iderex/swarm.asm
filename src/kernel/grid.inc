; grid.inc — the uniform-grid build (docs/MASTERPLAN.md, decision 3).
;
; The integrate pass writes each particle's cell id (grid coordinate) into the
; OUT cell-id array; build_core, in grid mode, calls grid_sort to reorder the
; population cell-sorted (a serial stable counting sort). seed_cellids computes
; the ids for the initial seeded state so the very first build has valid keys
; (the pass maintains them every frame thereafter). Pure integer code except
; the seed binning, which is IEEE convert/multiply under the pinned MXCSR.

; ------------------------------------------------------------------
; seed_cellids — cell id for the seeded reals [0, n), so the first grid build
; sorts by valid keys. cell = cy*g + cx, cx = int(x*g) & (g-1) (decision 3).
;   in:       rcx = arena (OUT seeded, AH_G cached)
;   out:      cellid[0..n) written; cellid pads (>= n) are left uninitialized
;             and MUST NOT be read (the build only histograms/scatters [0,n);
;             a later neighbourhood pass that reads pad cells must fill them)
;   clobbers: rax, rcx, rdx, rsi, rdi, r8, r9, r10, r11, xmm0, xmm1, flags;
;             saves+restores rbx
;   MXCSR:    assumes the pinned seam MXCSR; cvttss2si truncates (RC-independent)
; ------------------------------------------------------------------
seed_cellids:
        push    rbx
        mov     rbx, rcx                ; arena
        mov     r9d, [rbx+AH_PARAMS+SP_N]
        test    r9d, r9d
        jz      .done
        mov     r10d, [rbx+AH_PADDED]
        shl     r10, 2                  ; stride
        lea     rsi, [rbx+AH_SIZE]      ; &x_out[0]
        lea     rdi, [rsi+r10]          ; &y_out[0] = x + stride
        imul    rax, r10, AR_CELLID     ; 12*stride
        lea     r11, [rsi+rax]          ; &cellid[0]
        mov     ecx, [rbx+AH_G]         ; g
        cvtsi2ss xmm0, ecx              ; g as f32
        mov     r8d, ecx
        dec     r8d                     ; g - 1 (cell wrap mask)
  .loop:
        movss   xmm1, [rsi]             ; cx = int(x*g) & (g-1)
        mulss   xmm1, xmm0
        cvttss2si eax, xmm1
        and     eax, r8d
        movss   xmm1, [rdi]             ; cy = int(y*g) & (g-1)
        mulss   xmm1, xmm0
        cvttss2si edx, xmm1
        and     edx, r8d
        imul    edx, ecx                ; cy*g
        add     eax, edx                ; cell = cy*g + cx
        mov     [r11], eax
        add     rsi, 4
        add     rdi, 4
        add     r11, 4
        dec     r9d
        jnz     .loop
  .done:
        pop     rbx
        ret

; ------------------------------------------------------------------
; grid_sort — reorder the population cell-sorted, IN[0..n) = the n reals of
; OUT by ascending cell id (stable). build_core has ALREADY copied OUT -> IN
; (all padded_n, so the pads carry through); grid_sort overwrites only the
; IN[0..n) reals from OUT, using a serial stable counting sort.
;   in:       rcx = arena (IN currently == OUT; cellid[0..n) valid)
;   out:      IN[0..n) = OUT reals in ascending cell order; cell_start[0..g*g]
;             = the exclusive-prefix run starts (cell_start[g*g] = n)
;   clobbers: rax, rcx, r10, r11, flags (grid-tier); saves+restores rbx, rsi,
;             rdi, r12-r15 (grid_sort is reached by tail-jump from the
;             swarm_build export, so it must preserve the Win64 nonvolatiles)
;   MXCSR:    untouched (integer counting sort)
; ------------------------------------------------------------------
grid_sort:
        push    rbx
        push    rsi
        push    rdi
        push    r12
        push    r13
        push    r14
        push    r15
        mov     r12d, [rcx+AH_PARAMS+SP_N]  ; n
        mov     r10d, [rcx+AH_G]
        imul    r10d, r10d              ; g2 = g*g (number of cells)
        mov     r14d, [rcx+AH_PADDED]
        shl     r14, 2                  ; stride (bytes)
        lea     r15, [rcx+AH_SIZE]      ; OUT base
        imul    rax, r14, AR_X_IN
        lea     r11, [r15+rax]          ; IN base = OUT + 6*stride
        imul    rax, r14, AR_CELLID
        lea     r13, [r15+rax]          ; cellid base = OUT + 12*stride
        imul    rax, r14, AR_ARRAYS
        lea     rbx, [r15+rax]          ; cell_start base = OUT + 13*stride

        ; 1. zero cell_start[0 .. g2] (g2 + 1 dwords)
        mov     rdi, rbx
        lea     ecx, [r10+1]
        xor     eax, eax
        cld
        rep     stosd

        ; 2. histogram: for i in [0, n): cell_start[cellid[i]]++
        mov     rsi, r13
        mov     ecx, r12d
        test    ecx, ecx
        jz      .prefix_start
  .hist:
        mov     eax, [rsi]
        inc     dword [rbx+rax*4]
        add     rsi, 4
        dec     ecx
        jnz     .hist

        ; 3. inclusive prefix over [0, g2): cell_start[c] = sum of counts [0..c]
        ;    (= the end index of cell c; cell_start[g2-1] = n)
  .prefix_start:
        mov     rdi, rbx
        mov     ecx, r10d
        xor     eax, eax
  .prefix:
        add     eax, [rdi]
        mov     [rdi], eax
        add     rdi, 4
        dec     ecx
        jnz     .prefix
        mov     [rbx+r10*4], r12d       ; cell_start[g2] = n (last-run end)

        ; 4. stable backward scatter: for i = n-1 downto 0,
        ;    pos = --cell_start[cellid[i]] ; IN[pos] = OUT[i] (six components).
        ;    Descending i with a pre-decrement keeps equal-cell particles in
        ;    ascending-i order -> the sort is stable, so cell_start ends as the
        ;    run starts and the iteration order is a pure function of the state.
  .scatter:
        dec     r12d
        js      .done
        mov     eax, [r13+r12*4]        ; c = cellid[i]
        dec     dword [rbx+rax*4]
        mov     ecx, [rbx+rax*4]        ; pos
        lea     rsi, [r15+r12*4]        ; &OUT.x[i]
        lea     rdi, [r11+rcx*4]        ; &IN.x[pos]
        mov     eax, [rsi]
        mov     [rdi], eax              ; x
        add     rsi, r14
        add     rdi, r14
        mov     eax, [rsi]
        mov     [rdi], eax              ; y
        add     rsi, r14
        add     rdi, r14
        mov     eax, [rsi]
        mov     [rdi], eax              ; vx
        add     rsi, r14
        add     rdi, r14
        mov     eax, [rsi]
        mov     [rdi], eax              ; vy
        add     rsi, r14
        add     rdi, r14
        mov     eax, [rsi]
        mov     [rdi], eax              ; species
        add     rsi, r14
        add     rdi, r14
        mov     eax, [rsi]
        mov     [rdi], eax              ; id
        jmp     .scatter
  .done:
        pop     r15
        pop     r14
        pop     r13
        pop     r12
        pop     rdi
        pop     rsi
        pop     rbx
        ret
