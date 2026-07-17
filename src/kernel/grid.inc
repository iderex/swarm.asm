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

; neighbour_runs stack locals (the column window is loop-invariant across the
; three rows, so it is derived once).
NR_LO1    = 0                            ; column low index of run 1
NR_HI1    = 4                            ; column high index (exclusive) of run 1
NR_HI2    = 8                            ; column high index of run 2 (0 = no run 2)
NR_RY     = 12                           ; current row (0..g-1)
NR_ROWCNT = 16                           ; remaining rows to emit
NR_LOCALS = 32                           ; 20 used, rounded up

; ------------------------------------------------------------------
; neighbour_runs — enumerate the 3x3-neighbourhood runs of cell (cx, cy) into
; buf (docs/MASTERPLAN.md, decision 3). For each of the three rows
; ry = (cy-1, cy, cy+1) & (g-1) it appends the contiguous IN run(s) spanning
; columns (cx-1, cx, cx+1) & (g-1). Cells inside a row are consecutive cell ids
; and IN is cell-sorted, so a non-wrapping span is ONE run: base =
; cell_start[ry*g + cx-1], count = cell_start[ry*g + cx+2] - base. The torus
; column seam (cx == 0 or cx == g-1) splits that row into TWO runs so the
; wrapped column becomes its own contiguous span. Disjointness (no neighbour
; summed twice) needs the 3x3 block to name each cell once, which fails only if
; the window folds a cell onto itself: the three columns (cx-1, cx, cx+1) & (g-1)
; must be pairwise distinct AND so must the three rows (cy-1, cy, cy+1) & (g-1) --
; the floor is g >= 3 on BOTH axes (at g = 2 either window folds -1 onto +1).
; arena_dims_core clamps g to [4, 512] (layout.inc), so g >= 4 clears both floors
; and no neighbour is double-listed. Empty runs (count 0, sparse cells) are
; appended too -- the caller's tail logic contributes zero for a zero-count run.
; This is a pure
; function of (cx, cy, g, cell_start), which is what makes the neighbourhood
; force deterministic and pass-split invariant.
;   in:       ecx = cx (in [0, g)), edx = cy (in [0, g)), r8d = g,
;             r9d = g-1 (cell wrap mask), r10 = &cell_start[0],
;             r11 = &buf (space for 6 entries of {dd base, dd count})
;   out:      eax = number of runs written (3..6); buf filled
;   clobbers: rax, rcx, rdx, r11 (buf end), flags; saves+restores rbx; reads
;             r8, r9, r10 read-only (does not touch rsi, rdi, r12-r15, rbp, ymm)
;   MXCSR:    untouched (integer only)
; ------------------------------------------------------------------
neighbour_runs:
        push    rbx
        sub     rsp, NR_LOCALS
        xor     ebx, ebx                ; run count

        ; --- column window (same for all three rows) ---------------------
        lea     eax, [rcx-1]
        mov     [rsp+NR_LO1], eax       ; NOWRAP default: lo1 = cx-1
        lea     eax, [rcx+2]
        mov     [rsp+NR_HI1], eax       ;                 hi1 = cx+2
        mov     dword [rsp+NR_HI2], 0   ;                 no second run
        test    ecx, ecx
        jnz     .chk_high
        lea     eax, [r8-1]             ; cx == 0: run1 = col g-1, run2 = cols 0,1
        mov     [rsp+NR_LO1], eax       ; lo1 = g-1
        mov     [rsp+NR_HI1], r8d       ; hi1 = g
        mov     dword [rsp+NR_HI2], 2   ; hi2 = 2
        jmp     .rows
  .chk_high:
        cmp     ecx, r9d                ; cx == g-1 ?
        jne     .rows
        lea     eax, [r8-2]             ; run1 = cols g-2,g-1, run2 = col 0
        mov     [rsp+NR_LO1], eax       ; lo1 = g-2
        mov     [rsp+NR_HI1], r8d       ; hi1 = g
        mov     dword [rsp+NR_HI2], 1   ; hi2 = 1
  .rows:
        lea     eax, [rdx-1]            ; ry = (cy - 1) & (g-1); three rows up
        and     eax, r9d
        mov     [rsp+NR_RY], eax
        mov     dword [rsp+NR_ROWCNT], 3
  .row_loop:
        mov     eax, [rsp+NR_RY]
        imul    eax, r8d                ; rowbase = ry * g
        mov     ecx, eax                ; ecx = rowbase (cx no longer needed)

        mov     eax, ecx                ; run1: cells [rowbase+lo1, rowbase+hi1)
        add     eax, [rsp+NR_LO1]       ; c_lo
        mov     edx, ecx
        add     edx, [rsp+NR_HI1]       ; c_hi (exclusive)
        mov     eax, [r10+rax*4]        ; base = cell_start[c_lo]
        mov     edx, [r10+rdx*4]        ; end  = cell_start[c_hi]
        sub     edx, eax                ; count = end - base
        mov     [r11], eax
        mov     [r11+4], edx
        add     r11, 8
        inc     ebx

        mov     edx, [rsp+NR_HI2]       ; run2 (seam split) if hi2 > 0
        test    edx, edx
        jz      .row_next
        add     edx, ecx                ; c_hi = rowbase + hi2
        mov     eax, [r10+rcx*4]        ; base = cell_start[rowbase] (col 0)
        mov     edx, [r10+rdx*4]        ; end
        sub     edx, eax
        mov     [r11], eax
        mov     [r11+4], edx
        add     r11, 8
        inc     ebx
  .row_next:
        mov     eax, [rsp+NR_RY]
        inc     eax
        and     eax, r9d                ; ry = (ry + 1) & (g-1)
        mov     [rsp+NR_RY], eax
        dec     dword [rsp+NR_ROWCNT]
        jnz     .row_loop

        mov     eax, ebx                ; run count
        add     rsp, NR_LOCALS
        pop     rbx
        ret
