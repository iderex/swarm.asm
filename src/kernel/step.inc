; SPDX-License-Identifier: MIT
; step.inc - the force + integrate pass (docs/MASTERPLAN.md force model).
;
; This is the scalar reference path: one particle, one neighbour at a time, in
; the exact operation order of the C# oracle (no FMA). Each op is IEEE-exact,
; so the two agree bit-for-bit EXCEPT where the pinned FTZ/DAZ MXCSR (decision
; 2) flushes a denormal the .NET oracle would keep - a <=~1e-38 divergence the
; oracle test absorbs at its epsilon tier (1e-5 positions / 1e-4 velocities),
; never a bit-exact promise across the two runtimes. The AVX2 gather path is a
; separate code path added later, validated against this anchor within epsilon
; and against its own per-path goldens; the scalar path stays the semantic
; anchor and the readable definition.
;
; Flow (decision 5): swarm_build copies bank OUT -> bank IN (brute mode; the
; M2 counting sort replaces it); swarm_pass reads bank IN (the frozen pre-step
; state) and writes bank OUT (forces read IN, integration writes OUT, disjoint
; banks so fusing per particle is safe); swarm_step = n_steps x (build + pass)
; and advances the frame counter.

; elem_addr - dst = &bank[comp][idxreg], i.e. base + comp*stride + idxreg*4.
; x86 allows only base+index in one operand, so the three-term address is
; folded into a register here. Assumes rbx = bank base, r14 = stride.
macro elem_addr dst, comp, idxreg {
        imul    dst, r14, comp
        add     dst, rbx
        lea     dst, [dst+idxreg*4]
}

; pass_core stack frame (scalar const cache):
KP_RMAX2   = 0
KP_VMAX    = 4
KP_NEGVMAX = 8
KP_INVRMAX = 12
KP_INVBETA = 16
KP_INV1MB  = 20
KP_FS      = 24
KP_DT      = 28
KP_FRIC    = 32
KP_BETA    = 36
KP_LAST    = 40                         ; outer-loop bound (dword)
KP_G       = 44                         ; grid dim g (u32, M2 cell binning)
KP_GM1     = 48                         ; g - 1 (cell wrap mask)
KP_GF      = 52                         ; g as f32
KP_FRAME   = 64                         ; end of the shared const cache

; pass_scalar adds a neighbour-run buffer after the const cache: up to 6 runs
; (three 3x3 rows, each split at most once by the torus seam) of {base, count}.
KN_SCAL_RUNBUF = KP_FRAME               ; 6 * 8 bytes after the const cache
KN_SCAL_CACHE  = KN_SCAL_RUNBUF + 48    ; {cx, cy, count} of the resolved cell
KN_SCAL_FRAME  = 128                    ; 64 + 48 + 12, rounded to a 0-mod-32 frame

; Integrate-tail scalar-FP encoding switch (issue #33). The fused pass runs the
; per-particle integrate tail once, right after the force loop. In the AVX2 pass
; ymm8-15 carry dirty upper halves from that VEX loop, so running the tail in
; legacy-SSE encoding forces a VEX<->SSE transition on every particle. These
; helpers emit the VEX.128 form when TAIL_VEX=1 (bit-identical scalar arithmetic
; -- encoding only, so determinism per code path is preserved) and the legacy
; SSE form otherwise, keeping the AVX2 tail all-VEX while the scalar reference
; path stays SSE. The scalar path's BODY must run without AVX, so it MUST NOT
; emit VEX; its own floor is SSE4.1, set by the roundss the min-image round uses
; (:373, :377) and not by the SSE2 x86-64 baseline. That is a property of the
; body rather than of the shipped image: every route into it today crosses the
; seam, which saves xmm6-xmm15 with VEX vmovaps and so needs AVX1 regardless.
TAIL_VEX = 0

macro t_movss d, s { if TAIL_VEX
                       vmovss d, s
                     else
                       movss d, s
                     end if }
macro t_copy d, s  { if TAIL_VEX
                       vmovaps d, s
                     else
                       movaps d, s
                     end if }
macro t_mul d, s   { if TAIL_VEX
                       vmulss d, d, s
                     else
                       mulss d, s
                     end if }
macro t_add d, s   { if TAIL_VEX
                       vaddss d, d, s
                     else
                       addss d, s
                     end if }
macro t_sub d, s   { if TAIL_VEX
                       vsubss d, d, s
                     else
                       subss d, s
                     end if }
macro t_min d, s   { if TAIL_VEX
                       vminss d, d, s
                     else
                       minss d, s
                     end if }
macro t_max d, s   { if TAIL_VEX
                       vmaxss d, d, s
                     else
                       maxss d, s
                     end if }
macro t_cvtt d, s  { if TAIL_VEX
                       vcvttss2si d, s
                     else
                       cvttss2si d, s
                     end if }
macro t_round d, s, imm { if TAIL_VEX
                       vroundss d, s, s, imm
                     else
                       roundss d, s, imm
                     end if }
macro t_cmplt d, s { if TAIL_VEX
                       vcmpltss d, d, s
                     else
                       cmpltss d, s
                     end if }
macro t_and d, s   { if TAIL_VEX
                       vandps d, d, s
                     else
                       andps d, s
                     end if }
; d = s * m with s left intact. Separate from t_mul because the non-destructive
; form is one instruction under VEX and two without it, and the two-operand
; helper above cannot express that: writing t_copy + t_mul would emit the extra
; vmovaps in the VEX case, in the per-particle prologue of the AVX2 pass.
macro t_mul_from d, s, m { if TAIL_VEX
                       vmulss d, s, m
                     else
                       movaps d, s
                       mulss  d, m
                     end if }

; cell_id_from_coords - decision 3's binning rule, written once:
;   cx = int(x*g) & (g-1), cy = int(y*g) & (g-1), cell = cy*g + cx.
; Power-of-two g makes int(coord*g) exact for coord in [0,1); the & (g-1) mask
; is the belt that keeps cell in [0, g*g) unconditionally, which is what bounds
; the histogram and the scatter.
;
; Every operand is a parameter because the two call sites genuinely differ in
; all of them, and the alternative to parameterising them is what this replaces:
; the same ten lines written twice, in two files, for a rule that must change in
; both or in neither. `movop` is a parameter for the same reason and is the one
; that is easy to miss - seed_cellids brings each coordinate in from memory and
; the integrate tail already holds it in a register, so one site needs a load
; and the other a register copy. Substituting one for the other would change
; the emitted bytes in the hot tail, where `t_copy` is a full overwrite and a
; scalar move would be a merge into the destination's upper lanes.
;
; It sets TAIL_VEX, exactly as integrate_and_store does, so `vex` selects the
; encoding of the two arithmetic ops the same way the t_* helpers do everywhere
; else in this file.
;   in:       vex = 1 to VEX-encode, movop = the mnemonic that brings a
;             coordinate into tmp, xsrc/ysrc = its two sources, gf/gm1/g = the
;             grid triple as f32 / mask / integer, in any operand form movop's
;             site can supply
;   out:      dst = the cell id
;   clobbers: dst, scratch, tmp, flags
macro cell_id_from_coords vex, movop, xsrc, ysrc, gf, gm1, g, dst, scratch, tmp {
        TAIL_VEX = vex
        movop   tmp, xsrc
        t_mul   tmp, gf
        t_cvtt  dst, tmp                ; int(x*g), truncation = floor for x>=0
        and     dst, gm1                ; cx
        movop   tmp, ysrc
        t_mul   tmp, gf
        t_cvtt  scratch, tmp
        and     scratch, gm1            ; cy
        imul    scratch, g              ; cy*g
        add     dst, scratch            ; cell = cy*g + cx
}

; resolve_neighbour_runs - the per-particle prologue both force passes run
; before their j loop: decide which IN particles particle i may interact with.
; In grid mode that is the 3x3 cell neighbourhood of i's own cell, enumerated
; into the caller's frame run buffer by neighbour_runs; in brute mode it is the
; degenerate single run [0, n), which the caller writes itself because the two
; passes address their run buffers differently there.
;
; Written once because the cx/cy it computes must reproduce the cell id
; grid_sort binned by, bit-for-bit, or a particle reads a neighbourhood it is
; not in. That is one rule, and it was previously spelled out twice - the same
; fourteen lines in two routines four hundred lines apart, in two encodings, to
; be changed in both or in neither.
;
; `vex` selects the encoding of the two multiply/convert pairs exactly as the
; t_* helpers do elsewhere in this file: the arithmetic is identical and only
; the emitted form differs, so determinism per code path is unaffected. The
; AVX2 pass wants the whole prologue VEX-encoded because its force loop leaves
; ymm8-15 dirty (issue #33).
;
; The run set is resolved once per CELL, not once per particle (issue #87).
; grid_sort leaves IN cell-sorted and the pass walks i ascending, so
; consecutive particles share a cell; cell_start is fixed for the whole pass,
; so neighbour_runs is a pure function of (cx, cy) there and every particle in
; a cell would get the same answer. A three-dword cache in the caller's frame
; holds the last (cx, cy) and its run count, and a hit skips the call and the
; run buffer refill. The buffer itself is left exactly as the last call wrote
; it, which is what makes the hit legal.
;
; cx and cy are still computed per particle. They are a handful of uops, they
; are what detects the cell change, and computing them from the particle rather
; than trusting a boundary count keeps the resolution a function of the state.
;
; invalidate_run_cache resets the cache at pass entry, which is what keeps this
; invisible to a split pass: pass(0,k);pass(k,n) starts the second call with a
; miss, so it resolves at k exactly as an unsplit pass resolved at 0, and
; GridPassSplitInvariance still holds bit-for-bit.
;
;   in:       vex = 1 to VEX-encode, runbuf = the caller's frame offset for the
;             6-entry {base, count} run buffer, cache = its frame offset for the
;             three-dword {cx, cy, count} cell cache, countreg = the caller's
;             32-bit run-count register (must survive a call: neighbour_runs
;             preserves r12-r15 and this writes countreg after it returns)
;             rbx = bank base, r14 = stride, xmm8/xmm9 = xi/yi in their low
;             lane, and the KP_* const cache at [rsp]
;   out:      grid mode: countreg = run count, [rsp+runbuf] holds the runs of
;             this particle's cell, control falls through to the caller's
;             `.have_runs`
;             brute mode: jumps to the caller's `.single_run`, which owes the
;             degenerate run and countreg = 1
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, countreg, xmm0, flags
;   note:     the caller must define both `.single_run` and `.have_runs`; a
;             missing one is an assemble-time error rather than a silent fall
;             into the wrong branch.
macro resolve_neighbour_runs vex, runbuf, cache, countreg {
        TAIL_VEX = vex
        mov     eax, [rbx-AH_SIZE+AH_PARAMS+SP_FLAGS]  ; arena = bank base - header
        test    eax, FLAG_GRID
        jz      .single_run
        t_mul_from xmm0, xmm8, [rsp+KP_GF]  ; cx = int(xi*g) & (g-1) (reproduces
        t_cvtt  eax, xmm0                   ;   the cell id the sort used)
        and     eax, [rsp+KP_GM1]
        mov     ecx, eax
        t_mul_from xmm0, xmm9, [rsp+KP_GF]  ; cy = int(yi*g) & (g-1)
        t_cvtt  eax, xmm0
        and     eax, [rsp+KP_GM1]
        mov     edx, eax
        cmp     ecx, [rsp+cache]            ; same cell as the previous particle?
        jne     .resolve
        cmp     edx, [rsp+cache+4]
        jne     .resolve
        mov     countreg, [rsp+cache+8]     ; hit: the run buffer already holds it
        jmp     .have_runs
  .resolve:
        mov     [rsp+cache], ecx
        mov     [rsp+cache+4], edx
        mov     r8d, [rsp+KP_G]
        mov     r9d, [rsp+KP_GM1]
        imul    rax, r14, AR_ARRAYS         ; cell_start base = bank base + 13*stride
        lea     r10, [rbx+rax]
        lea     r11, [rsp+runbuf]
        call    neighbour_runs              ; eax = run count, buf filled
        mov     [rsp+cache+8], eax
        mov     countreg, eax
        jmp     .have_runs
}

; invalidate_run_cache - arm the cell cache above for a fresh pass.
;
; cx lives in [0, g) and g is clamped to at most 512, so -1 is a cell
; coordinate no particle can produce and the first comparison always misses.
; This must run once per pass entry rather than once per frame: cell_start is
; rebuilt by every grid_sort, so a cache surviving into the next pass would
; hand a particle the run set of a partition that no longer exists.
;   in:       cache = the caller's frame offset for the three-dword cache
;   out:      the cache cannot match any cell
;   clobbers: nothing but memory
macro invalidate_run_cache cache {
        mov     dword [rsp+cache], -1
}

; integrate_and_store vex - semi-implicit Euler + vmax clamp + torus wrap +
; write OUT[i], shared by both code paths, and the OUT cell id for next frame's
; grid build (M2).
;   in:       xmm6 = fx, xmm7 = fy (the accumulated force for particle i, as
;             SCALARS), xmm8/xmm9 = xi/yi in their low lane, r12 = i,
;             rbx = bank base, r14 = stride, and the KP_* const cache at [rsp]
;             (incl. KP_G/KP_GM1/KP_GF)
;   out:      OUT[i] velocity and position written, plus the OUT cell id
;   clobbers: xmm0-4, xmm10, r9, r10, and (via the wrap routine it calls)
;             xmm1/3/4
;   note:     vex=1 VEX-encodes the tail (issue #33): the emitted arithmetic is
;             identical, and the VEX.128 forms additionally zero the upper lanes
;             of the xmm0-4/xmm10 temporaries - harmless, as none carry live ymm
;             state past the force loop (the persistent ymm6-15 are read but
;             never written here).
macro integrate_and_store vex {
        TAIL_VEX = vex
        elem_addr r10, AR_VX_IN, r12    ; vx = clamp(vx*fric + fx*dt, +/-vmax)
        t_movss xmm0, [r10]
        t_mul   xmm0, [rsp+KP_FRIC]
        t_copy  xmm1, xmm6
        t_mul   xmm1, [rsp+KP_DT]
        t_add   xmm0, xmm1
        t_min   xmm0, [rsp+KP_VMAX]
        t_max   xmm0, [rsp+KP_NEGVMAX]  ; xmm0 = vx'
        elem_addr r10, AR_VY_IN, r12
        t_movss xmm2, [r10]
        t_mul   xmm2, [rsp+KP_FRIC]
        t_copy  xmm3, xmm7
        t_mul   xmm3, [rsp+KP_DT]
        t_add   xmm2, xmm3
        t_min   xmm2, [rsp+KP_VMAX]
        t_max   xmm2, [rsp+KP_NEGVMAX]  ; xmm2 = vy'
        t_copy  xmm4, xmm0              ; x' = wrap(xi + vx'*dt)
        t_mul   xmm4, [rsp+KP_DT]
        t_add   xmm4, xmm8
      if vex
        call    wrap_scalar_avx
      else
        call    wrap_scalar
      end if
        t_copy  xmm10, xmm4             ; stash x' (wrap clobbers xmm1/3/4)
        t_copy  xmm4, xmm2              ; y' = wrap(yi + vy'*dt)
        t_mul   xmm4, [rsp+KP_DT]
        t_add   xmm4, xmm9
      if vex
        call    wrap_scalar_avx
      else
        call    wrap_scalar
      end if
        elem_addr r10, AR_X_OUT, r12
        t_movss [r10], xmm10
        elem_addr r10, AR_Y_OUT, r12
        t_movss [r10], xmm4
        elem_addr r10, AR_VX_OUT, r12
        t_movss [r10], xmm0
        elem_addr r10, AR_VY_OUT, r12
        t_movss [r10], xmm2
        elem_addr r10, AR_SP_IN, r12    ; species, id pass through IN -> OUT
        mov     r9d, [r10]
        elem_addr r10, AR_SP_OUT, r12
        mov     [r10], r9d
        elem_addr r10, AR_ID_IN, r12
        mov     r9d, [r10]
        elem_addr r10, AR_ID_OUT, r12
        mov     [r10], r9d
        ; cell id for next frame's grid build (M2). x' is still live in xmm10
        ; from the store above and y' in xmm4, both in [0,1) from wrap_scalar.
        cell_id_from_coords vex, t_copy, xmm10, xmm4, \
                [rsp+KP_GF], [rsp+KP_GM1], [rsp+KP_G], r9d, r10d, xmm0
        elem_addr r10, AR_CELLID, r12
        mov     [r10], r9d
}

; cache_pass_consts - derive the per-pass scalar constant cache from the params
; block into the KP_* stack slots. Identical prologue for both pass_scalar and
; pass_avx2 (they share the KP_* layout), so it lives once here (decision 5).
;   in:       rcx = arena, rsp = the pass frame (KP_* slots reserved)
;   out:      [rsp+KP_RMAX2 .. KP_GF] filled
;   clobbers: rax (eax), r10, xmm0-6
macro cache_pass_consts {
        lea     r10, [rcx+AH_PARAMS]
        movss   xmm0, [r10+SP_RMAX]
        movaps  xmm1, xmm0
        mulss   xmm1, xmm0
        movss   [rsp+KP_RMAX2], xmm1    ; rmax2
        movss   xmm2, [r10+SP_DT]
        movaps  xmm3, xmm0
        divss   xmm3, xmm2
        movss   [rsp+KP_VMAX], xmm3     ; vmax = rmax/dt
        xorps   xmm4, xmm4
        subss   xmm4, xmm3
        movss   [rsp+KP_NEGVMAX], xmm4  ; -vmax
        movss   xmm5, [f_one]
        divss   xmm5, xmm0
        movss   [rsp+KP_INVRMAX], xmm5  ; 1/rmax
        movss   xmm5, [f_one]
        divss   xmm5, [r10+SP_BETA]
        movss   [rsp+KP_INVBETA], xmm5  ; 1/beta
        movss   xmm5, [f_one]
        movaps  xmm6, xmm5
        subss   xmm6, [r10+SP_BETA]     ; 1 - beta
        divss   xmm5, xmm6
        movss   [rsp+KP_INV1MB], xmm5   ; 1/(1-beta)
        movss   xmm5, [r10+SP_FORCE_SCALE]
        movss   [rsp+KP_FS], xmm5
        movss   [rsp+KP_DT], xmm2       ; dt
        movss   xmm5, [r10+SP_FRICTION]
        movss   [rsp+KP_FRIC], xmm5
        movss   xmm5, [r10+SP_BETA]
        movss   [rsp+KP_BETA], xmm5
        mov     eax, [rcx+AH_G]         ; cache the grid dim for cell binning
        mov     [rsp+KP_G], eax
        dec     eax
        mov     [rsp+KP_GM1], eax       ; g - 1 (cell wrap mask)
        cvtsi2ss xmm5, dword [rcx+AH_G]
        movss   [rsp+KP_GF], xmm5       ; g as f32
}

; ------------------------------------------------------------------
; build_core - produce bank IN for the next pass.
;
; Brute mode copies the whole OUT bank into IN: the pass reads IN as the frozen
; prior state, so next step's IN must equal this step's OUT, element for
; element.
;
; Grid mode does NOT, and copying it there was pure waste. grid_sort overwrites
; IN[0..n) for all six components out of OUT (grid.inc, the backward scatter),
; so every dword the full-bank copy wrote for a real particle was rewritten
; before anything read it. At n = 1M that is 6M dwords written and immediately
; overwritten, roughly 24 MB of avoidable write traffic per step. What grid_sort
; never touches is the pad tail [n, padded_n) of each array, and the pass DOES
; read it: the AVX2 tail over-reads the final group and masks it out, which is
; safe only because those lanes hold the finite values init pinned there. So
; grid mode carries the pads and nothing else. padded_n = ((n+15) & ~15) + 16,
; so that is 16 to 31 dwords per array rather than padded_n.
;
; Bit-exact either way: IN[0..n) ends up byte-identical because grid_sort
; derives it from OUT, and the pads are propagated by both branches.
;
;   in:       rcx = arena
;   out:      brute: IN = OUT (all six arrays, incl. pads). grid: IN pads = OUT
;             pads, and IN[0..n) reordered by ascending cell id, cell_start
;             filled.
;   clobbers: rax, rcx, r10, r11, flags (grid mode additionally clobbers what
;             grid_sort does); saves+restores rbx, rsi, rdi (grid_sort
;             preserves the remaining nonvolatiles it uses)
;   MXCSR:    untouched (integer copy / sort)
; ------------------------------------------------------------------
build_core:
        mov     r11d, [rcx+AH_PARAMS+SP_FLAGS]
        test    r11d, FLAG_GRID
        jnz     .grid
        push    rsi
        push    rdi
        mov     eax, [rcx+AH_PADDED]    ; padded_n
        lea     rsi, [rcx+AH_SIZE]      ; bank OUT base
        imul    r10d, eax, AR_X_IN      ; 6*padded_n dwords = one bank
        lea     rdi, [rsi+r10*4]        ; bank IN base = OUT + 6*stride
        cld
        mov     ecx, r10d               ; brute: copy the whole OUT bank -> IN
        rep     movsd
        pop     rdi
        pop     rsi
        ret
  .grid:
        push    rbx
        mov     rbx, rcx                ; arena survives the pad copy
        call    build_pads_core         ; carry the six pad tails OUT -> IN
        mov     rcx, rbx
        pop     rbx
        jmp     grid_sort               ; overlay IN[0..n); rets to caller

; ------------------------------------------------------------------
; build_pads_core - the grid-mode half of build_core that is not the sort:
; carry each of the six OUT pad tails [n, padded_n) into IN.
;
; Split out because the parallel build (pool_build) needs exactly this and
; nothing else before it fans the sort out; both callers reach the same bytes,
; so there is one description of what the pads are for rather than two.
;
;   in:       rcx = arena
;   out:      IN pads = OUT pads for all six particle arrays; IN[0..n)
;             untouched - the caller's sort overlays it
;   clobbers: rax, rcx, r10, r11, flags; saves+restores rsi, rdi
;   MXCSR:    untouched (integer copy)
; ------------------------------------------------------------------
build_pads_core:
        push    rsi
        push    rdi
        mov     eax, [rcx+AH_PADDED]    ; padded_n
        lea     rsi, [rcx+AH_SIZE]      ; bank OUT base
        imul    r10d, eax, AR_X_IN
        lea     rdi, [rsi+r10*4]        ; bank IN base = OUT + 6*stride
        cld
        mov     r11d, [rcx+AH_PARAMS+SP_N]  ; n (zero-extended: r11 is an index)
        sub     eax, r11d               ; pad = padded_n - n, in [16, 31]
        lea     rsi, [rsi+r11*4]        ; &OUT.x[n], the first pad element
        lea     rdi, [rdi+r11*4]        ; &IN.x[n]
        mov     r10d, AR_X_IN           ; six arrays to carry
  .pad:
        mov     ecx, eax
        rep     movsd                   ; leaves rsi/rdi at element 0 of the next
        lea     rsi, [rsi+r11*4]        ;   array, so +n lands on its pad again
        lea     rdi, [rdi+r11*4]
        dec     r10d
        jnz     .pad
        pop     rdi
        pop     rsi
        ret

; ------------------------------------------------------------------
; pass_core - dispatch the fused force+integrate to the code path the arena
; resolved at init (AH_PATH holds a PATH_* id, never PATH_AUTO). Tail jumps
; preserve the seam's 0-mod-32 entry, which the SIMD path relies on for
; aligned spills.
;
; PATH_AVX512 IS NOT NAMED HERE and that is not an omission (#316). It used to
; be, jumping to the ymm body, so on every host reporting the feature a header
; word saying AVX-512 and the instructions that ran disagreed. init_core now
; refuses the id rather than resolving it, so no arena this engine writes can
; carry it, and a forged word carrying it lands on the reference path with
; everything else - the direction below where being wrong costs speed. Naming
; it again belongs in the change that builds the zmm body (#123/#164), beside
; the init_core arms that admit it.
;
; The vector ids are named and everything else falls to the reference path,
; rather than the reverse (#202). Deciding by exclusion sent PATH_AUTO, and any
; word that never came from a resolution at all, into the vector body: the
; direction that can execute an instruction the CPU may not have. Two
; invariants kept that safe and neither is stated here - the accept gate in
; pp_validate_params bounds the field, and cpuid.inc cannot report CPU_AVX512
; without CPU_AVX2 - so the structure was safe only for as long as code
; elsewhere stayed that way. Downward is the direction where being wrong costs
; speed instead of an invalid opcode.
;   in:       rcx = arena, edx = first, r8d = last
;   clobbers: everything pass_scalar/pass_avx2 do (kernel tier; the seam saved it)
; ------------------------------------------------------------------
pass_core:
        mov     eax, [rcx+AH_PATH]
        cmp     eax, PATH_AVX2
        je      pass_avx2
        jmp     pass_scalar             ; PATH_SCALAR, PATH_AUTO, PATH_AVX512,
                                        ;   anything else

; ------------------------------------------------------------------
; pass_scalar - the reference path: fused force+integrate for i in
; [first, last), one particle and one neighbour at a time (the semantic anchor).
; Each particle's neighbours come from the resolved run set (grid = the 3x3 cell
; runs via neighbour_runs, brute = the single run [0, n)), so the scalar path is
; an independent check of the same neighbourhood the AVX2 path consumes.
;   in:       rcx = arena, edx = first, r8d = last
;   out:      OUT[i].{x,y,vx,vy} integrated; OUT[i].{species,id} = IN[i].*
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, rsi, rdi, rbx, r12-r15,
;             xmm0-10, flags (kernel tier; the seam saved them)
;   MXCSR:    assumes the pinned seam MXCSR (round-nearest, FTZ/DAZ)
;   align:    rsp = 0 mod 32 at entry (seam / dispatcher tail-jump)
; ------------------------------------------------------------------
pass_scalar:
        sub     rsp, KN_SCAL_FRAME
        mov     [rsp+KP_LAST], r8d
        mov     r12d, edx               ; i = first
        lea     rbx, [rcx+AH_SIZE]      ; bank base
        mov     r14d, [rcx+AH_PADDED]
        shl     r14, 2                  ; stride = padded_n*4
        mov     r15d, [rcx+AH_PARAMS+SP_N]
        lea     r13, [rcx+AH_PARAMS+SP_MATRIX]

        cache_pass_consts               ; derive KP_* from the params block
        invalidate_run_cache KN_SCAL_CACHE

  .outer:
        cmp     r12d, [rsp+KP_LAST]
        jae     .done

        elem_addr r10, AR_X_IN, r12     ; xi, yi = IN.x[i], IN.y[i]
        movss   xmm8, [r10]
        elem_addr r10, AR_Y_IN, r12
        movss   xmm9, [r10]
        xorps   xmm6, xmm6              ; fx
        xorps   xmm7, xmm7              ; fy

        ; --- resolve the neighbour run set: grid mode = the 3x3 cell runs,
        ;     brute = the degenerate single run [0, n) (decision 3) ---------
        resolve_neighbour_runs 0, KN_SCAL_RUNBUF, KN_SCAL_CACHE, ecx
  .single_run:
        mov     dword [rsp+KN_SCAL_RUNBUF], 0   ; base = 0
        mov     [rsp+KN_SCAL_RUNBUF+4], r15d    ; count = n
        mov     ecx, 1
  .have_runs:
        lea     r8, [rsp+KN_SCAL_RUNBUF]        ; run-buffer walk pointer (ecx runs left)
  .run_loop:
        mov     edx, [r8]              ; base = particle index of this run
        imul    rax, r14, AR_X_IN      ; IN.x / IN.y / IN.species at base
        lea     rsi, [rbx+rax]
        lea     rsi, [rsi+rdx*4]
        imul    rax, r14, AR_Y_IN
        lea     rdi, [rbx+rax]
        lea     rdi, [rdi+rdx*4]
        imul    rax, r14, AR_SP_IN
        lea     r11, [rbx+rax]
        lea     r11, [r11+rdx*4]       ; ssp
        mov     r9d, [r8+4]            ; j counter = run count
        test    r9d, r9d
        jz      .run_next              ; empty cell run contributes nothing

  .inner:
        movss   xmm0, [rsi]             ; dx = x[j]-xi ; dx -= round(dx)
        subss   xmm0, xmm8
        roundss xmm1, xmm0, 0           ; round to nearest even
        subss   xmm0, xmm1
        movss   xmm2, [rdi]             ; dy = y[j]-yi ; dy -= round(dy)
        subss   xmm2, xmm9
        roundss xmm3, xmm2, 0
        subss   xmm2, xmm3
        movaps  xmm4, xmm0              ; r2 = dx*dx + dy*dy
        mulss   xmm4, xmm0
        movaps  xmm5, xmm2
        mulss   xmm5, xmm2
        addss   xmm4, xmm5              ; xmm4 = r2
        xorps   xmm5, xmm5
        comiss  xmm4, xmm5              ; r2 <= 0 ? (self / coincident)
        jbe     .skip
        comiss  xmm4, [rsp+KP_RMAX2]    ; r2 >= rmax2 ?
        jae     .skip

        sqrtss  xmm4, xmm4              ; r
        movaps  xmm1, xmm4              ; xn = r * (1/rmax)
        mulss   xmm1, [rsp+KP_INVRMAX]
        comiss  xmm1, [rsp+KP_BETA]     ; xn < beta ?
        jb      .repulse

        ; f = a[si][sj] * (1 - abs(2*xn - 1 - beta) * inv1mb)
        movaps  xmm3, xmm1
        addss   xmm3, xmm3              ; 2*xn
        subss   xmm3, [f_one]
        subss   xmm3, [rsp+KP_BETA]     ; 2*xn - 1 - beta
        movd    eax, xmm3               ; abs: clear the sign bit in a GPR
        and     eax, 0x7FFFFFFF
        movd    xmm3, eax
        mulss   xmm3, [rsp+KP_INV1MB]
        movss   xmm5, [f_one]
        subss   xmm5, xmm3              ; 1 - ...
        elem_addr rax, AR_SP_IN, r12    ; si
        mov     eax, [rax]
        shl     eax, 3                  ; si*8
        mov     r10d, [r11]             ; sj
        add     eax, r10d
        mulss   xmm5, [r13+rax*4]       ; f = a * (1 - ...)
        jmp     .havef
  .repulse:
        movaps  xmm5, xmm1              ; f = xn*invBeta - 1
        mulss   xmm5, [rsp+KP_INVBETA]
        subss   xmm5, [f_one]
  .havef:
        mulss   xmm5, [rsp+KP_FS]       ; q = force_scale * f / r
        divss   xmm5, xmm4
        movaps  xmm1, xmm5              ; fx += q*dx
        mulss   xmm1, xmm0
        addss   xmm6, xmm1
        mulss   xmm5, xmm2              ; fy += q*dy
        addss   xmm7, xmm5
  .skip:
        add     rsi, 4
        add     rdi, 4
        add     r11, 4
        dec     r9d
        jnz     .inner
  .run_next:
        add     r8, 8
        dec     ecx
        jnz     .run_loop

        ; --- integrate particle i (semi-implicit Euler + clamp + wrap) ----
        integrate_and_store 0           ; scalar path: SSE-encoded tail (no AVX)

        inc     r12d
        jmp     .outer
  .done:
        add     rsp, KN_SCAL_FRAME
        ret

; FG_LANES - the lane width pass_avx2 instantiates the force pass at. Every
; width-derived number below and in the run loop is written through it rather
; than as a literal 8, so the two spellings of "8" the routine used to carry -
; a lane count and the species-table cap - stop looking alike. The species cap
; is NOT this constant: `and eax, 7` in the matrix-row lookup bounds a species
; index against an 8-row matrix and does not move when the lane width does.
FG_LANES  = 8

; pass_avx2 frame: the scalar const cache (KP_*), the neighbour-run buffer (up
; to 6 runs of {base, count}), then the vector block: seven FG_LANES-wide
; broadcast constants the force expression reads as memory operands (KB_*),
; and four spill slots that carry the compaction overflow across one force
; expression (KA_SPILL; see force_group).
;
; The constants moved out of registers on purpose (#335): the four registers
; they held now hold the pending in-range candidates, and AVX2 has no embedded
; broadcast, so a constant that is a memory operand is a full vector in memory.
; Every slot in the block is read and written with vmovaps or used as an
; m256 operand, so the block and the frame must be 0 mod the vector size. They
; are consequences of KP_FRAME and FG_LANES rather than written numbers, and
; asserted below rather than believed: a literal would go on covering an
; 8-lane layout after the rest had widened, and the failure would be a silent
; write past the frame into the caller's slots rather than an assembler refusal.
KA_RUNBUF = KP_FRAME                    ; 6 * 8 bytes after the const cache
KA_CACHE  = KA_RUNBUF + 48               ; {cx, cy, count} of the resolved cell
KA_VEC    = ((KA_CACHE + 12 + FG_LANES*4 - 1) / (FG_LANES*4)) * (FG_LANES*4)
KB_RMAX2   = KA_VEC                      ; the vector block, one slot each
KB_BETA    = KB_RMAX2 + FG_LANES*4
KB_ABSMASK = KB_BETA + FG_LANES*4
KB_INVRMAX = KB_ABSMASK + FG_LANES*4
KB_INVBETA = KB_INVRMAX + FG_LANES*4
KB_INV1MB  = KB_INVBETA + FG_LANES*4
KB_FS      = KB_INV1MB + FG_LANES*4
KA_SPILL   = KB_FS + FG_LANES*4          ; 4 slots: dx, dy, r2, a of the overflow
KA_FRAME   = KA_SPILL + 4*FG_LANES*4

if (KA_VEC mod (FG_LANES*4)) <> 0 | (KA_SPILL mod (FG_LANES*4)) <> 0
  err       ; a vmovaps slot is not vector-aligned; see the note above
end if
if (KA_FRAME mod 32) <> 0 | KA_FRAME < (KA_CACHE + 12)
  err       ; the frame does not cover the block, or breaks the 0-mod-32 rsp
end if

; The compaction tables (#335), one contiguous block so one register addresses
; all four; the offsets are asserted at the tables themselves.
;   CT_COMPRESS  256 rows of W dwords: row m lists the set bits of m in order,
;                zero-padded - the vpermps index that packs the in-range lanes
;                to the front. AVX2 has no vpcompressps; this is the LUT form.
;   CT_ROTATE    W rows of W dwords: row k is the index vector that rotates a
;                register up by k lanes, so a packed candidate m lands at
;                lane (k + m) mod W.
;   CT_LTMASK    W rows of W dwords: row k is all-ones in lanes < k.
;   CT_POPCNT    256 bytes: the bit count of the mask, so no POPCNT feature
;                bit is spent (cpuid.inc gates AVX2+FMA and nothing else).
CT_COMPRESS = 0
CT_ROTATE   = CT_COMPRESS + 256*FG_LANES*4
CT_LTMASK   = CT_ROTATE + FG_LANES*FG_LANES*4
CT_POPCNT   = CT_LTMASK + FG_LANES*FG_LANES*4
CT_BYTES    = CT_POPCNT + 256

; The force pass as one text, parameterised by lane width W and register file V
; (masterplan decision 7). The 8-lane instantiation below is what ships; the
; 16-lane one is #164 and does not exist yet, so every construct that has no
; width-neutral spelling is dispatched on W with only the 8-lane arm written and
; the other arm refusing at assembly time. A refusal is deliberate: emitting a
; guessed zmm form would assemble into a path nothing on this hardware can run
; and nothing in the harness can falsify.
;
; V is concatenated with a register number (V#0 .. V#15), so it names the file
; and never a single register: pass ymm or zmm, not ymm0.

; force_consts - the pass-wide state the force loop reads without reloading:
; the seven broadcast constants written once into the KB_* frame slots (the
; loop reads them as memory operands), 1.0 in V#15 (the one constant used at
; four sites, kept in a register), the pending-candidate registers V#11..V#14
; zeroed, and r15 pointing at the compaction tables. Width-specific: the abs
; mask is a full-register constant, so it is named and sized per width.
;
; The zeroing is load-bearing and not hygiene: the drain (force_group at
; mode 2) masks the lanes above k to a zero force but still multiplies that
; zero by whatever the pending dx/dy lanes hold, and 0 * NaN is NaN. Zeroed
; here, every later lane is a permute of a real finite dx, dy, r2 or matrix
; entry, so the product is a signed zero and never anything else.
;   in:       rsp = the pass frame, KP_* cached
;   out:      [rsp+KB_RMAX2 .. KB_FS] = the broadcast constants, V#15 = 1.0,
;             V#11-14 = 0, r15 = &f_compress
;   clobbers: V#0
macro force_consts W, V {
        vbroadcastss V#15, [f_one]
        vbroadcastss V#0, [rsp+KP_RMAX2]
        vmovaps [rsp+KB_RMAX2], V#0
        vbroadcastss V#0, [rsp+KP_BETA]
        vmovaps [rsp+KB_BETA], V#0
      if W = 8
        vmovaps V#0, yword [f_absmask256]
      else
        err     ; #164: the 16-lane abs mask has no constant in this tree
      end if
        vmovaps [rsp+KB_ABSMASK], V#0
        vbroadcastss V#0, [rsp+KP_INVRMAX]
        vmovaps [rsp+KB_INVRMAX], V#0
        vbroadcastss V#0, [rsp+KP_INVBETA]
        vmovaps [rsp+KB_INVBETA], V#0
        vbroadcastss V#0, [rsp+KP_INV1MB]
        vmovaps [rsp+KB_INV1MB], V#0
        vbroadcastss V#0, [rsp+KP_FS]
        vmovaps [rsp+KB_FS], V#0
        vxorps  V#11, V#11, V#11        ; pending dx, dy, r2, a: finite from here on
        vxorps  V#12, V#12, V#12
        vxorps  V#13, V#13, V#13
        vxorps  V#14, V#14, V#14
        lea     r15, [f_compress]
}

; force_tail_mask - build the mask that excludes the over-read past a run end.
; Width-specific: at 8 lanes it is a lane mask in V#5, read out of the sliding
; f_tailwin LUT at (W - tail); at 16 it is a k register built with bzhi/kmovw
; and the LUT has no successor.
;   in:       eax = tail lanes (1 .. W-1)
;   out:      V#5 = the tail mask force_group consumes at tail=1
;   clobbers: r8, r10, flags (the `sub` that turns the lane count into the
;             LUT offset writes them; the run loop's next branch is the `cmp`
;             at .g8 in the following iteration, so nothing reads them across)
macro force_tail_mask W, V {
      if W = 8
        mov     r10d, W                 ; tailmask = tailwin[(W-tail) .. +W-1];
        sub     r10d, eax               ;   masks the over-read past the run end,
        lea     r8, [f_tailwin]         ;   which would double-count the next
        vmovups V#5, [r8+r10*4]         ;   cell's particles (a wrapped neighbour)
      else
        err     ; #164: k-register masking, not a lane mask out of a LUT
      end if
}

; force_particle_regs - the per-particle prologue: broadcast xi/yi, load
; particle i's species row as the permute table, and clear the force partials.
; Width-specific: the matrix row is 8 floats wide whatever the lane width is, so
; at 16 lanes what occupies the table's upper half is a decision rather than a
; wider load, and it is the same decision the vpermps in force_group refuses.
;   in:       rbx = bank base, r14 = stride, r12d = i
;   out:      V#8 = xi, V#9 = yi, V#10 = the species row, V#6/V#7 = zeroed
;   clobbers: rax, r10, flags
macro force_particle_regs W, V {
        elem_addr r10, AR_X_IN, r12     ; xi, yi broadcast
        vbroadcastss V#8, [r10]
        elem_addr r10, AR_Y_IN, r12
        vbroadcastss V#9, [r10]
        elem_addr r10, AR_SP_IN, r12    ; matrow = matrix[si*8 .. +7]
        mov     eax, [r10]
        and     eax, 7                  ; species < species_n <= 8; keep in-table
        shl     eax, 5                  ; si*8 floats = si*32 bytes
        lea     r10, [rbx-AH_SIZE+AH_PARAMS+SP_MATRIX]  ; arena = bank base - header
      if W = 8
        vmovups V#10, [r10+rax]
      else
        err     ; #164: an 8-float row does not fill a 16-lane permute table
      end if
        vxorps  V#6, V#6, V#6           ; fx partials
        vxorps  V#7, V#7, V#7           ; fy partials
}

; force_group - one group of W neighbours j at [base+r9], in two halves that
; run at different rates (#335). The STAGE half runs once per group: it forms
; dx, dy and r2 for the W lanes, decides which are in range, and packs those
; lanes onto the pending candidates in V#11-14 (dx, dy, r2, a). The FORCE half
; - sqrt, divide and the whole force expression - runs only when W candidates
; are pending, on a register holding nothing but in-range lanes, so the divide
; unit no longer works on lanes whose force is discarded afterwards. Per group
; that is one mask, two table loads, five permutes and four blends against a
; force expression run on the in-range share of the lanes; whether that share
; pays for the bookkeeping is a measurement (docs/BENCHMARKS.md), not an
; argument.
;
; mode 0 is a full group, mode 1 a run's tail group (V#5 carries the tail
; mask that excludes the over-read past the run end), mode 2 the DRAIN: no
; loads, the k pending candidates run the force half under a lane mask, once
; per particle. The drain is what keeps the pass split-invariant: no candidate
; of particle i is pending when i+1 starts, so where a range split falls
; changes nothing.
;
; HOW THE PACKING WORKS, because the index arithmetic is the part a reader
; cannot reconstruct from the mnemonics. vmovmskps turns `valid` into a byte
; m; row m of the compress table lists the set lanes of m in order, so
; permuting by it packs the in-range lanes to the front. That row is itself
; permuted by row k of the rotate table before it is used, so packed lane j
; lands at lane (k + j) mod W instead: the first W - k of them fall on the
; lanes above the k already pending, and any past that wrap to the bottom.
; A blend by "lane < k" then merges the rotated group onto the pending
; registers. If k + c < W the merge IS the new pending set. If k + c >= W the
; merge holds W in-range candidates and runs the force half, and the ROTATED
; group - whose bottom lanes are exactly the wrapped overflow - becomes the
; new pending set, k - W of it meaningful. Lanes above k are never read as
; candidates: the flush is full by construction and the drain masks them.
;
; Pinned masking survives in the one place a lane can be unwanted: the drain
; blends r2 to 1.0 on the lanes above k BEFORE the sqrt and zeroes their
; force, which is what makes a NaN structurally impossible there; the flush
; has no such lane. The neighbour loads are vmovups: a run starts at
; cell_start[c], which is not W-aligned, so the group base is unaligned
; (bit-identical to vmovaps when it happens to land aligned, e.g. the brute
; single run). The spill slots are vector-aligned frame offsets, asserted
; where they are defined, so those stay vmovaps.
;   in:       rsi/rdi/r11 = &IN.x[0] / &IN.y[0] / &IN.sp[0], r9 = group byte
;             offset (modes 0 and 1), rsp = the pass frame with the KB_* block
;             written, r15 = &f_compress, V#8/V#9 = xi/yi, V#10 = the species
;             row, V#15 = 1.0, V#5 = the tail mask at mode 1
;   in/out:   edx = k, the pending in-range candidates (0 .. W-1 between
;             groups), V#11-14 = their dx, dy, r2, a in lanes < k,
;             V#6/V#7 = the fx/fy partials, accumulated into
;   clobbers: V#0-5, rax, r10, flags, [rsp+KA_SPILL .. +4 slots]
;
; V#8, V#9 and V#10 are inputs, and force_particle_regs is what produces them -
; the per-particle prologue, once per i, where this macro runs once per group of
; W. They are named here through V like everything else, so the two must be
; instantiated at the same width or this macro reads a register whose upper
; lanes the other one never wrote.
;
; SITES BELOW THAT REFUSE AT W <> 8 ARE MARKERS, NOT A COMPLETE LIST, and the
; number of them is deliberately not stated: at 16 lanes the dataflow changes
; shape rather than widening. Every vcmpps here writes a k register there, so
; `valid` stops being a vector and the two vandps that combine it become kandw;
; vroundps has no 512-bit encoding at all (it is vrndscaleps); the packing is
; one vpcompressps there and the tables have no successor. Some of those fail
; closed as assembler refusals and the mask ANDs would not. #164 rewrites this
; body against a golden of its own; it does not fill in else-arms.
;
; FIVE SITES ARE FUSED (#162): the five mul-then-add/sub pairs are one FMA
; each, rounding once instead of twice. That is the ARITHMETIC of PATH_AVX2
; rather than a spelling of it, which is why it is written here once with no
; switch beside it: a path id names an algorithm, not an encoding, so an
; encoding change re-baselines its path rather than forking one (masterplan
; decision 2, amended 2026-08-31). `att = tt*a` stays a lone mul - it has no
; add to fuse with. Fusing is not a move TOWARD the oracle: the reference is
; an unfused f32 op sequence, so single rounding is a DIFFERENT distance from
; it, and the recorded parity envelope is what says how far. The compaction
; moved the ORDER the candidates enter the partial sums and nothing about the
; arithmetic of any one candidate; the envelope was re-measured for it.
macro force_group W, V, mode {
      local ..flush, ..done
      if mode < 2
        vmovups V#0, [rsi+r9]           ; dx = x[j] - xi ; min-image
        vsubps  V#0, V#0, V#8
        vroundps V#4, V#0, 0
        vsubps  V#0, V#0, V#4
        vmovups V#1, [rdi+r9]           ; dy
        vsubps  V#1, V#1, V#9
        vroundps V#4, V#1, 0
        vsubps  V#1, V#1, V#4
        vmulps  V#2, V#0, V#0           ; r2 = dx*dx + dy*dy (one rounding)
        vfmadd231ps V#2, V#1, V#1
        vxorps  V#4, V#4, V#4
        vcmpps  V#3, V#2, V#4, 0x0E     ; r2 > 0
        vcmpps  V#4, V#2, [rsp+KB_RMAX2], 0x11  ; r2 < rmax2
        vandps  V#3, V#3, V#4           ; valid
        if mode = 1
          if W = 8
        vandps  V#3, V#3, V#5           ; & tail mask (exclude j >= n / pads)
          else
        err     ; #164: the tail predicate is a k register at 16 lanes
          end if
        end if
        vmovups V#5, [r11+r9]           ; sj (species indices)
        if W = 8
        vpermps V#5, V#5, V#10          ; a[k] = matrow[sj[k]]
        vmovmskps eax, V#3              ; m = the in-range lanes as a byte
        mov     r10d, edx
        shl     r10d, 5                 ; k * 32: the row of the k-indexed tables
        vmovups V#4, [r15+CT_ROTATE+r10]
        shl     eax, 5                  ; m * 32: the compress row
        vpermps V#4, V#4, [r15+CT_COMPRESS+rax] ; idx = compress[m] rotated by k
        vpermps V#0, V#4, V#0           ; the in-range lanes, packed, at (k+j) mod W
        vpermps V#1, V#4, V#1
        vpermps V#2, V#4, V#2
        vpermps V#5, V#4, V#5
        vmovups V#3, [r15+CT_LTMASK+r10] ; lanes < k: the pending ones
        shr     eax, 5
        movzx   eax, byte [r15+CT_POPCNT+rax] ; c = in-range lanes this group
        else
        err     ; #164: vpcompressps under the k mask, and no tables
        end if
        add     edx, eax                ; k += c
        cmp     edx, W
        jae     ..flush
        if W = 8
        vblendvps V#11, V#0, V#11, V#3  ; pending = lane<k ? pending : packed
        vblendvps V#12, V#1, V#12, V#3
        vblendvps V#13, V#2, V#13, V#3
        vblendvps V#14, V#5, V#14, V#3
        else
        err     ; #164: k-register merge, and not in this operand order
        end if
        jmp     ..done
  ..flush:
        if W = 8
        vblendvps V#11, V#0, V#11, V#3  ; W in-range candidates, no other lane
        vblendvps V#12, V#1, V#12, V#3
        vblendvps V#13, V#2, V#13, V#3
        vblendvps V#14, V#5, V#14, V#3
        else
        err     ; #164: k-register merge, and not in this operand order
        end if
        vmovaps [rsp+KA_SPILL], V#0     ; the packed group outlives the force
        vmovaps [rsp+KA_SPILL+FG_LANES*4], V#1    ; expression: its bottom
        vmovaps [rsp+KA_SPILL+2*FG_LANES*4], V#2  ; lanes are the overflow
        vmovaps [rsp+KA_SPILL+3*FG_LANES*4], V#5
      else
        test    edx, edx                ; the drain: nothing pending, nothing owed
        jz      ..done
        mov     r10d, edx
        shl     r10d, 5
        vmovups V#3, [r15+CT_LTMASK+r10] ; lanes < k hold candidates
        if W = 8
        vblendvps V#13, V#15, V#13, V#3 ; r2' = lane<k ? r2 : 1.0 (pre-sqrt)
        else
        err     ; #164: k-register select, and not in this operand order
        end if
      end if
        vsqrtps V#2, V#13               ; r
        vmulps  V#4, V#2, [rsp+KB_INVRMAX] ; xn = r * invrmax
        vmovaps V#0, [rsp+KB_INVBETA]
        vfmsub213ps V#0, V#4, V#15      ; rep = invbeta*xn - 1 (mul commutes)
        vaddps  V#1, V#4, V#4           ; att: 2xn - 1 - beta
        vsubps  V#1, V#1, V#15
        vsubps  V#1, V#1, [rsp+KB_BETA]
        vandps  V#1, V#1, [rsp+KB_ABSMASK] ; abs
        vmovaps V#5, [rsp+KB_INV1MB]
        vfnmadd213ps V#1, V#5, V#15     ; tt = 1 - |..|*inv1mb
        vmulps  V#1, V#1, V#14          ; att = tt * a
        vcmpps  V#5, V#4, [rsp+KB_BETA], 0x11 ; xn < beta
      if W = 8
        vblendvps V#0, V#1, V#0, V#5    ; f = (xn<beta) ? rep : att
      else
        err     ; #164: k-register select, and not in this operand order
      end if
      if mode = 2
        vandps  V#0, V#0, V#3           ; f &= lane<k (the rest -> 0)
      end if
        vmulps  V#0, V#0, [rsp+KB_FS]   ; q = f*fs / r  (masked: f=0, r=1 -> 0)
        vdivps  V#0, V#0, V#2
        vfmadd231ps V#6, V#0, V#11      ; fx += q*dx ; fy += q*dy
        vfmadd231ps V#7, V#0, V#12
      if mode < 2
        vmovaps V#11, [rsp+KA_SPILL]    ; pending = the overflow, k - W of it
        vmovaps V#12, [rsp+KA_SPILL+FG_LANES*4]
        vmovaps V#13, [rsp+KA_SPILL+2*FG_LANES*4]
        vmovaps V#14, [rsp+KA_SPILL+3*FG_LANES*4]
        sub     edx, W
      end if
  ..done:
}

; force_reduce - collapse the W partial forces in V#6/V#7 to xmm6[0]/xmm7[0]
; with a fixed shuffle order, once per particle.
;
; It stays OUTSIDE force_group and is its own macro instead. force_group runs
; once per group of W neighbours and the reduce runs once per particle, so
; folding it in would run it per group and change the arithmetic - the opposite
; of what this change is allowed to do. Parameterising it separately gives #164
; the one place its 16-lane reduction order is written, which is what the
; per-path golden exists to pin, without the caller holding a width choice the
; macro layer cannot see.
;   in:       V#6 = fx partials, V#7 = fy partials
;   out:      xmm6[0] = fx, xmm7[0] = fy
;   clobbers: xmm0, the upper lanes of V#6/V#7
macro force_reduce W, V {
      if W = 8
        vextractf128 xmm0, V#6, 1       ; fx -> xmm6[0]
        vaddps  xmm6, xmm6, xmm0
        vhaddps xmm6, xmm6, xmm6
        vhaddps xmm6, xmm6, xmm6
        vextractf128 xmm0, V#7, 1       ; fy -> xmm7[0]
        vaddps  xmm7, xmm7, xmm0
        vhaddps xmm7, xmm7, xmm7
        vhaddps xmm7, xmm7, xmm7
      else
        err     ; #164: the 16-lane reduction order is its own golden
      end if
}

; ------------------------------------------------------------------
; avx2_pass_body - the 8-wide gather force+integrate (masterplan decision 3),
; written once and instantiated per force_group arithmetic (#162).
; For each particle i (scalar), broadcast xi/yi and load its matrix row, then
; sweep its neighbour runs eight j at a time: grid mode consumes the 3x3 cell
; runs from neighbour_runs (the O(n*k) path), brute mode the degenerate single
; run [0, n). Every run's final vector applies a count-derived tail mask so the
; unaligned over-read past the run end cannot double-count the next cell. The
; pinned lane-hygiene order (blend r2 to 1.0 on the invalid lanes BEFORE the
; sqrt) makes a NaN structurally impossible. The 8 partial forces reduce with a
; fixed shuffle order across all runs; integration is the shared scalar tail.
; Matches the oracle within the epsilon tier (its lane reduction order differs
; from the scalar path - that is the per-code-path golden's whole reason to exist).
;
; EVERY LABEL IN HERE IS A FASM LOCAL, and it stays a macro with one caller
; for #164's sake rather than for a second AVX2 instantiation: a local binds to
; the global label above it, so a 16-lane body instantiated beside this one
; gets its own .outer. resolve_neighbour_runs jumps to .single_run and
; .have_runs and is inside this macro for the same reason.
;   in:       rcx = arena, edx = first, r8d = last
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, rsi, rdi, rbx, r12-r15,
;             ymm0-15, flags (kernel tier; the seam saved the nonvolatiles)
;   MXCSR:    assumes the pinned seam MXCSR
;   align:    rsp = 0 mod 32 at entry (aligned ymm loads/spills)
; ------------------------------------------------------------------
macro avx2_pass_body {
        sub     rsp, KA_FRAME
        mov     [rsp+KP_LAST], r8d
        mov     r12d, edx               ; i = first
        lea     rbx, [rcx+AH_SIZE]      ; bank base
        mov     r14d, [rcx+AH_PADDED]
        shl     r14, 2                  ; stride

        cache_pass_consts               ; same KP_* layout as pass_scalar
        invalidate_run_cache KA_CACHE

        ; --- the pass-wide state: KB_* constants, 1.0, the pending set, r15 --
        force_consts FG_LANES, ymm

  .outer:
        cmp     r12d, [rsp+KP_LAST]
        jae     .done

        force_particle_regs FG_LANES, ymm

        ; --- resolve the neighbour run set (decision 3): grid = the 3x3 cell
        ;     runs, brute = the degenerate single run [0, n) ------------------
        resolve_neighbour_runs 1, KA_RUNBUF, KA_CACHE, r13d
  .single_run:
        lea     r11, [rsp+KA_RUNBUF]
        mov     dword [r11], 0           ; base = 0
        mov     eax, [rbx-AH_SIZE+AH_PARAMS+SP_N] ; count = n (arena = bank base - header)
        mov     [r11+4], eax
        mov     r13d, 1
  .have_runs:
        xor     edx, edx                 ; k = 0 pending in-range candidates (after
                                         ;   the call above, which clobbers rdx)
        imul    rax, r14, AR_X_IN        ; group bases (recomputed after the call)
        lea     rsi, [rbx+rax]           ; &IN.x[0]
        imul    rax, r14, AR_Y_IN
        lea     rdi, [rbx+rax]           ; &IN.y[0]
        imul    rax, r14, AR_SP_IN
        lea     r11, [rbx+rax]           ; &IN.sp[0]
        lea     rcx, [rsp+KA_RUNBUF]     ; run-buffer walk pointer (r13 runs left)
  .run_loop:
        mov     r9d, [rcx]               ; base = particle index of this run
        shl     r9, 2                    ; base byte offset = first group offset
        mov     r10d, [rcx+4]            ; count (edx is the pending count now)
        and     r10d, -FG_LANES          ; nfull = count & ~(W-1) (full groups)
        lea     r8, [r9+r10*4]           ; tail group byte offset = base + nfull*4
  .g8:
        cmp     r9, r8
        jae     .g8done
        force_group FG_LANES, ymm, 0
        add     r9, FG_LANES*4
        jmp     .g8
  .g8done:
        mov     eax, [rcx+4]             ; count again: force_group clobbers rax
        and     eax, FG_LANES-1          ; tail lanes = count & (W-1)
        jz      .run_next                ; run length a lane multiple -> no tail
        force_tail_mask FG_LANES, ymm
        force_group FG_LANES, ymm, 1 ; r9 at the tail group offset
  .run_next:
        add     rcx, 8
        dec     r13d
        jnz     .run_loop
  .reduce:
        force_group FG_LANES, ymm, 2    ; drain: the k still pending, masked
        force_reduce FG_LANES, ymm      ; fixed shuffle-reduce, once per particle
        integrate_and_store 1           ; AVX2 path: VEX-encoded tail (issue #33)

        inc     r12d
        jmp     .outer
  .done:
        add     rsp, KA_FRAME
        ret
}

; pass_avx2 - the AVX2 path (PATH_AVX2), fused at the five sites #162 names.
; Its CPU requirement is unchanged by the fusion: cpu_paths_core cannot report
; CPU_AVX2 without FMA (cpuid.inc leaf 1 ecx bit 12), so no CPU that reaches
; this body lacks the unit, and no new feature bit is spent. The bits it
; produces MOVED on 2026-08-31 and the recorded parity envelope moved with
; them; nothing that reads those bits was left asserting the pre-fusion ones.
;   in:       rcx = arena, edx = first, r8d = last
;   out:      OUT[i].{x,y,vx,vy} integrated for i in [first, last)
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, rsi, rdi, rbx, r12-r15,
;             ymm0-15, flags (kernel tier; the seam saved the nonvolatiles)
;   MXCSR:    assumes the pinned seam MXCSR
;   align:    rsp = 0 mod 32 at entry (aligned ymm loads/spills)
pass_avx2:
        avx2_pass_body

; ------------------------------------------------------------------
; wrap_body - p -= floor(p); if p >= 1.0: p = 0.0. One body, two encodings
; (issue #33): the scalar path calls wrap_scalar (SSE, no AVX guarantee); the
; AVX2 tail calls wrap_scalar_avx (VEX.128, bit-identical arithmetic) so the
; once-per-particle wrap does not re-enter legacy SSE with dirty ymm upper
; halves. TAIL_VEX is set immediately before each body so the t_* helpers pick
; the right encoding.
; ------------------------------------------------------------------
macro wrap_body {
        t_round xmm1, xmm4, 1           ; floor (round toward -inf)
        t_sub   xmm4, xmm1              ; p - floor(p)
        t_copy  xmm3, xmm4              ; mask = (p < 1.0) ? all-ones : 0
        t_cmplt xmm3, dword [f_one]
        t_and   xmm4, xmm3              ; p >= 1.0 -> 0
        ret
}
; wrap_scalar - SSE-encoded wrap for the scalar reference path.
;   in/out:   xmm4 = p -> wrapped [0,1)
;   clobbers: xmm1, xmm3
wrap_scalar:
        TAIL_VEX = 0
        wrap_body
; wrap_scalar_avx - VEX.128-encoded wrap for the AVX2 tail (issue #33);
; bit-identical arithmetic to wrap_scalar, additionally zeroing the upper lanes
; of ymm1/3/4 (dead registers at this point in the pass).
;   in/out:   xmm4 = p -> wrapped [0,1)
;   clobbers: xmm1, xmm3
wrap_scalar_avx:
        TAIL_VEX = 1
        wrap_body

; ------------------------------------------------------------------
; step_core - n_steps x (build + pass over all n), advancing the frame.
;   in:       rcx = arena, edx = n_steps
;   out:      the world advanced n_steps; AH_FRAME += n_steps
;   clobbers: everything pass_core does (kernel tier; the seam saved it)
;   MXCSR:    pinned by the seam
; ------------------------------------------------------------------
step_core:
        test    edx, edx
        jz      .done
        ; pass_core clobbers every register, so the arena and the remaining
        ; count live on the stack across the calls, not in registers. The 24 is
        ; alignment-load-bearing: step_core is entered at rsp = 0 mod 32 (the
        ; seam lands the kernel there), and pass_core's contract wants a
        ; 0-mod-32 ENTRY. A `call` lands the callee at caller_rsp-8, so the
        ; caller must be at 8 mod 32 before it: 0 - 24 = 8 (mod 32). Harmless
        ; for the scalar movss here, mandatory once the AVX2 pass spills ymm.
        sub     rsp, 24
        mov     [rsp], rcx              ; arena
        mov     [rsp+8], edx            ; remaining steps
  .loop:
        mov     rcx, [rsp]
        call    build_core              ; OUT -> IN
        mov     rcx, [rsp]
        xor     edx, edx                ; first = 0
        mov     r8d, [rcx+AH_PARAMS+SP_N] ; last = n
        call    pass_core               ; IN -> OUT
        mov     rcx, [rsp]
        inc     qword [rcx+AH_FRAME]
        dec     dword [rsp+8]
        jnz     .loop
        add     rsp, 24
  .done:
        ret

align 4
f_one     dd 1.0

; AVX2 path constants. The abs mask is a full ymmword (vandps is packed). The
; tail window is a sliding LUT: loading 8 dwords at offset (8-k) yields k
; leading all-ones lanes, masking a partial final group down to its real j's.
align 32
f_absmask256 dd 8 dup(0x7FFFFFFF)
f_tailwin    dd 8 dup(0xFFFFFFFF), 8 dup(0)

; The compaction tables (#335), generated at assembly time from their
; definitions at CT_* so that no row is typed by hand; the sizes are asserted
; against the offsets the loop addresses them by. Constant data outside the
; kernel's mutable state, like the two LUTs above.
align 32
f_compress dd FG_LANES dup(0)           ; row 0: m = 0 sets no lane, all padding
repeat 255
  ct_m = %
  ct_c = 0
  repeat FG_LANES
    if ct_m and (1 shl (% - 1))
      dd % - 1                          ; the set lanes of m, in lane order
      ct_c = ct_c + 1
    end if
  end repeat
  if ct_c < FG_LANES
    repeat FG_LANES - ct_c
      dd 0                              ; padding; never read as a candidate
    end repeat
  end if
end repeat
if $ - f_compress <> CT_ROTATE
  err       ; the compress table is not the size CT_ROTATE assumes
end if
repeat FG_LANES
  ct_k = % - 1
  repeat FG_LANES
    dd ((% - 1) - ct_k) and (FG_LANES - 1) ; rotate up by k: lane p reads p - k
  end repeat
end repeat
if $ - f_compress <> CT_LTMASK
  err       ; the rotate table is not the size CT_LTMASK assumes
end if
repeat FG_LANES
  ct_k = % - 1
  repeat FG_LANES
    if (% - 1) < ct_k
      dd 0xFFFFFFFF                     ; lane < k
    else
      dd 0
    end if
  end repeat
end repeat
if $ - f_compress <> CT_POPCNT
  err       ; the lane mask table is not the size CT_POPCNT assumes
end if
repeat 256
  ct_m = % - 1
  ct_c = 0
  repeat FG_LANES
    if ct_m and (1 shl (% - 1))
      ct_c = ct_c + 1
    end if
  end repeat
  db ct_c                               ; popcount(m)
end repeat
if $ - f_compress <> CT_BYTES
  err       ; the tables do not end where CT_BYTES says
end if
