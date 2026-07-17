; step.inc — the force + integrate pass (docs/MASTERPLAN.md force model).
;
; This is the scalar reference path: one particle, one neighbour at a time, in
; the exact operation order of the C# oracle (no FMA). Each op is IEEE-exact,
; so the two agree bit-for-bit EXCEPT where the pinned FTZ/DAZ MXCSR (decision
; 2) flushes a denormal the .NET oracle would keep — a &lt;=~1e-38 divergence the
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

; elem_addr — dst = &bank[comp][idxreg], i.e. base + comp*stride + idxreg*4.
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
KN_SCAL_FRAME  = 128                    ; 64 + 48, rounded to a 0-mod-32 frame

; Integrate-tail scalar-FP encoding switch (issue #33). The fused pass runs the
; per-particle integrate tail once, right after the force loop. In the AVX2 pass
; ymm8-15 carry dirty upper halves from that VEX loop, so running the tail in
; legacy-SSE encoding forces a VEX<->SSE transition on every particle. These
; helpers emit the VEX.128 form when TAIL_VEX=1 (bit-identical scalar arithmetic
; -- encoding only, so determinism per code path is preserved) and the legacy
; SSE form otherwise, keeping the AVX2 tail all-VEX while the scalar reference
; path stays SSE. The scalar path is guaranteed to run without AVX (init.inc:
; "scalar: always available; no CPU feature required"), so it MUST NOT emit VEX.
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

; integrate_and_store — semi-implicit Euler + vmax clamp + torus wrap + write
; OUT[i], shared by both code paths, and the OUT cell id for next frame's grid
; build (M2). Consumes the accumulated force for particle i as SCALARS in
; xmm6 (fx) / xmm7 (fy); xmm8/xmm9 hold xi/yi in their low lane; the KP_* const
; cache is at [rsp] (incl. KP_G/KP_GM1/KP_GF); r12 = i; rbx = bank base,
; r14 = stride. Clobbers xmm0-4, xmm10, r9, r10, and (via the wrap routine)
; xmm1/3/4. vex=1 VEX-encodes the tail (issue #33): the emitted arithmetic is
; identical, and the VEX.128 forms additionally zero the upper lanes of the
; xmm0-4/xmm10 temporaries -- harmless, as none carry live ymm state past the
; force loop (the persistent ymm6-15 are read but never written here).
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
        ; cell id for next frame's grid build (M2): cell = cy*g + cx,
        ; cx = int(x'*g) & (g-1), cy = int(y'*g) & (g-1). Power-of-two g makes
        ; int(coord*g) exact (coord in [0,1) from wrap_scalar, so already
        ; < g); the & (g-1) mask is the belt that keeps cell in [0, g*g)
        ; unconditionally, which is what bounds the histogram/scatter.
        t_copy  xmm0, xmm10             ; x' (still live from the store above)
        t_mul   xmm0, [rsp+KP_GF]
        t_cvtt  r9d, xmm0               ; int(x'*g), truncation = floor for x'>=0
        and     r9d, [rsp+KP_GM1]       ; cx
        t_copy  xmm0, xmm4              ; y'
        t_mul   xmm0, [rsp+KP_GF]
        t_cvtt  r10d, xmm0
        and     r10d, [rsp+KP_GM1]      ; cy
        imul    r10d, [rsp+KP_G]        ; cy*g
        add     r9d, r10d               ; cell id
        elem_addr r10, AR_CELLID, r12
        mov     [r10], r9d
}

; cache_pass_consts — derive the per-pass scalar constant cache from the params
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
; build_core — produce bank IN for the next pass. Always copies OUT -> IN
; (so the pads carry through unchanged); in grid mode (FLAG_GRID) it then
; tail-calls grid_sort to overlay IN[0..n) cell-sorted (decision 3).
;   in:       rcx = arena
;   out:      brute: IN = OUT (all six arrays, incl. pads). grid: additionally
;             IN[0..n) reordered by ascending cell id, cell_start filled.
;   clobbers: rax, rcx, r10, flags (grid mode additionally clobbers r11 via
;             grid_sort); saves+restores rbx, rsi, rdi (grid_sort preserves
;             the remaining nonvolatiles it uses)
;   MXCSR:    untouched (integer copy / sort)
; ------------------------------------------------------------------
build_core:
        push    rbx
        mov     rbx, rcx                ; arena survives the rep movsd (kills rcx)
        push    rsi
        push    rdi
        mov     eax, [rbx+AH_PADDED]    ; padded_n
        lea     rsi, [rbx+AH_SIZE]      ; bank OUT base
        imul    r10d, eax, AR_X_IN      ; 6*padded_n dwords = one bank
        lea     rdi, [rsi+r10*4]        ; bank IN base = OUT + 6*stride
        mov     ecx, r10d               ; copy the whole OUT bank -> IN
        cld
        rep     movsd
        pop     rdi
        pop     rsi
        mov     eax, [rbx+AH_PARAMS+SP_FLAGS]
        test    eax, FLAG_GRID          ; ZF survives the mov/pop below
        mov     rcx, rbx                ; grid_sort takes the arena in rcx
        pop     rbx
        jnz     grid_sort               ; grid: overlay IN[0..n); rets to caller
        ret

; ------------------------------------------------------------------
; pass_core — dispatch the fused force+integrate to the code path the arena
; resolved at init (AH_PATH: 1 AVX2, 2 AVX-512, 3 scalar). Tail jumps preserve
; the seam's 0-mod-32 entry, which the SIMD path relies on for aligned spills.
;   in:       rcx = arena, edx = first, r8d = last
;   clobbers: everything pass_scalar/pass_avx2 do (kernel tier; the seam saved it)
; ------------------------------------------------------------------
pass_core:
        mov     eax, [rcx+AH_PATH]
        cmp     eax, 3
        je      pass_scalar
        jmp     pass_avx2               ; 1 AVX2, and 2 AVX-512 until a real
                                        ;   512 path exists

; ------------------------------------------------------------------
; pass_scalar — the reference path: fused force+integrate for i in
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
        mov     eax, [rbx-AH_SIZE+AH_PARAMS+SP_FLAGS]  ; arena = bank base - header
        test    eax, FLAG_GRID
        jz      .single_run
        movaps  xmm0, xmm8             ; cx = int(xi*g) & (g-1) (reproduces the
        mulss   xmm0, [rsp+KP_GF]      ;   cell id the sort used, bit-exact)
        cvttss2si eax, xmm0
        and     eax, [rsp+KP_GM1]
        mov     ecx, eax
        movaps  xmm0, xmm9             ; cy = int(yi*g) & (g-1)
        mulss   xmm0, [rsp+KP_GF]
        cvttss2si eax, xmm0
        and     eax, [rsp+KP_GM1]
        mov     edx, eax
        mov     r8d, [rsp+KP_G]
        mov     r9d, [rsp+KP_GM1]
        imul    rax, r14, AR_ARRAYS    ; cell_start base = bank base + 13*stride
        lea     r10, [rbx+rax]
        lea     r11, [rsp+KN_SCAL_RUNBUF]
        call    neighbour_runs         ; eax = run count, buf filled
        mov     ecx, eax
        jmp     .runs
  .single_run:
        mov     dword [rsp+KN_SCAL_RUNBUF], 0   ; base = 0
        mov     [rsp+KN_SCAL_RUNBUF+4], r15d    ; count = n
        mov     ecx, 1
  .runs:
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

; pass_avx2 frame: the scalar const cache (KP_*), two 32-aligned ymm spill slots
; for dx/dy (register pressure relief across the force formula), and the
; neighbour-run buffer (up to 6 runs of {base, count}).
KA_DX     = KP_FRAME                    ; first slot after the const cache
KA_DY     = KA_DX + 32
KA_RUNBUF = KA_DY + 32                   ; 6 * 8 bytes
KA_FRAME  = 192                         ; 128 + 48, rounded to a 0-mod-32 frame

; avx2_force_group — one group of 8 neighbours j at [base+r9]; accumulates into
; ymm6/ymm7. tail=1 ANDs the mask in ymm5 into `valid`. dx/dy are spilled so
; the force formula has room; ymm0-5 are the working set, ymm8-15 the persistent
; broadcasts. Pinned masking: blend r2 to 1.0 on invalid lanes BEFORE the sqrt.
; The three neighbour loads are vmovups: a run starts at cell_start[c], which is
; not 8-aligned, so the group base is unaligned (bit-identical to vmovaps when it
; happens to land aligned, e.g. the brute single run). The dx/dy spill slots are
; fixed 0-mod-32 frame offsets, so those stay vmovaps.
macro avx2_force_group tail {
        vmovups ymm0, [rsi+r9]          ; dx = x[j] - xi ; min-image
        vsubps  ymm0, ymm0, ymm8
        vroundps ymm4, ymm0, 0
        vsubps  ymm0, ymm0, ymm4
        vmovups ymm1, [rdi+r9]          ; dy
        vsubps  ymm1, ymm1, ymm9
        vroundps ymm4, ymm1, 0
        vsubps  ymm1, ymm1, ymm4
        vmovaps [rsp+KA_DX], ymm0       ; spill dx, dy
        vmovaps [rsp+KA_DY], ymm1
        vmulps  ymm2, ymm0, ymm0        ; r2 = dx*dx + dy*dy
        vmulps  ymm4, ymm1, ymm1
        vaddps  ymm2, ymm2, ymm4
        vxorps  ymm4, ymm4, ymm4
        vcmpps  ymm3, ymm2, ymm4, 0x0E  ; r2 > 0
        vcmpps  ymm4, ymm2, ymm11, 0x11 ; r2 < rmax2
        vandps  ymm3, ymm3, ymm4        ; valid
      if tail
        vandps  ymm3, ymm3, ymm5        ; & tail mask (exclude j >= n / pads)
      end if
        vblendvps ymm2, ymm12, ymm2, ymm3  ; r2' = valid ? r2 : 1.0 (pre-sqrt)
        vsqrtps ymm2, ymm2              ; r
        vmulps  ymm4, ymm2, ymm15       ; xn = r * invrmax
        vbroadcastss ymm0, [rsp+KP_INVBETA]
        vmulps  ymm0, ymm4, ymm0        ; rep = xn*invbeta - 1
        vsubps  ymm0, ymm0, ymm12
        vaddps  ymm1, ymm4, ymm4        ; att: 2xn - 1 - beta
        vsubps  ymm1, ymm1, ymm12
        vsubps  ymm1, ymm1, ymm13
        vandps  ymm1, ymm1, ymm14       ; abs
        vbroadcastss ymm5, [rsp+KP_INV1MB]
        vmulps  ymm1, ymm1, ymm5
        vsubps  ymm1, ymm12, ymm1       ; tt = 1 - |..|*inv1mb
        vmovups ymm5, [r11+r9]          ; sj (species indices)
        vpermps ymm5, ymm5, ymm10       ; a[k] = matrow[sj[k]]
        vmulps  ymm1, ymm1, ymm5        ; att = tt * a
        vcmpps  ymm5, ymm4, ymm13, 0x11 ; xn < beta
        vblendvps ymm0, ymm1, ymm0, ymm5   ; f = (xn<beta) ? rep : att
        vandps  ymm0, ymm0, ymm3        ; f &= valid (invalid -> 0)
        vbroadcastss ymm5, [rsp+KP_FS]
        vmulps  ymm0, ymm0, ymm5        ; q = f*fs / r  (invalid: f=0, r=1 -> 0)
        vdivps  ymm0, ymm0, ymm2
        vmovaps ymm5, [rsp+KA_DX]       ; fx += q*dx ; fy += q*dy
        vmulps  ymm5, ymm0, ymm5
        vaddps  ymm6, ymm6, ymm5
        vmovaps ymm5, [rsp+KA_DY]
        vmulps  ymm0, ymm0, ymm5
        vaddps  ymm7, ymm7, ymm0
}

; ------------------------------------------------------------------
; pass_avx2 — the 8-wide gather force+integrate (masterplan decision 3).
; For each particle i (scalar), broadcast xi/yi and load its matrix row, then
; sweep its neighbour runs eight j at a time: grid mode consumes the 3x3 cell
; runs from neighbour_runs (the O(n*k) path), brute mode the degenerate single
; run [0, n). Every run's final vector applies a count-derived tail mask so the
; unaligned over-read past the run end cannot double-count the next cell. The
; pinned lane-hygiene order (blend r2 to 1.0 on the invalid lanes BEFORE the
; sqrt) makes a NaN structurally impossible. The 8 partial forces reduce with a
; fixed shuffle order across all runs; integration is the shared scalar tail.
; Matches the oracle within the epsilon tier (its lane reduction order differs
; from the scalar path — that is the per-code-path golden's whole reason to exist).
;   in:       rcx = arena, edx = first, r8d = last
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, rsi, rdi, rbx, r12-r15,
;             ymm0-15, flags (kernel tier; the seam saved the nonvolatiles)
;   MXCSR:    assumes the pinned seam MXCSR
;   align:    rsp = 0 mod 32 at entry (aligned ymm loads/spills)
; ------------------------------------------------------------------
pass_avx2:
        sub     rsp, KA_FRAME
        mov     [rsp+KP_LAST], r8d
        mov     r12d, edx               ; i = first
        lea     rbx, [rcx+AH_SIZE]      ; bank base
        mov     r14d, [rcx+AH_PADDED]
        shl     r14, 2                  ; stride
        mov     r15d, [rcx+AH_PARAMS+SP_N]

        cache_pass_consts               ; same KP_* layout as pass_scalar

        ; --- ymm constants that live the whole routine -------------------
        vbroadcastss ymm11, [rsp+KP_RMAX2]
        vbroadcastss ymm12, [f_one]
        vbroadcastss ymm13, [rsp+KP_BETA]
        vmovaps ymm14, yword [f_absmask256]
        vbroadcastss ymm15, [rsp+KP_INVRMAX]

  .outer:
        cmp     r12d, [rsp+KP_LAST]
        jae     .done

        elem_addr r10, AR_X_IN, r12     ; xi, yi broadcast
        vbroadcastss ymm8, [r10]
        elem_addr r10, AR_Y_IN, r12
        vbroadcastss ymm9, [r10]
        elem_addr r10, AR_SP_IN, r12    ; matrow = matrix[si*8 .. +7]
        mov     eax, [r10]
        and     eax, 7                  ; species < species_n <= 8; keep in-table
        shl     eax, 5                  ; si*8 floats = si*32 bytes
        lea     r10, [rbx-AH_SIZE+AH_PARAMS+SP_MATRIX]  ; arena = bank base - header
        vmovups ymm10, [r10+rax]
        vxorps  ymm6, ymm6, ymm6        ; fx partials
        vxorps  ymm7, ymm7, ymm7        ; fy partials

        ; --- resolve the neighbour run set (decision 3): grid = the 3x3 cell
        ;     runs, brute = the degenerate single run [0, n) ------------------
        mov     eax, [rbx-AH_SIZE+AH_PARAMS+SP_FLAGS]
        test    eax, FLAG_GRID
        jz      .single_run
        vmulss  xmm0, xmm8, [rsp+KP_GF]  ; cx = int(xi*g) & (g-1) (reproduces the
        vcvttss2si eax, xmm0             ;   cell id the sort used, bit-exact)
        and     eax, [rsp+KP_GM1]
        mov     ecx, eax
        vmulss  xmm0, xmm9, [rsp+KP_GF]  ; cy = int(yi*g) & (g-1)
        vcvttss2si eax, xmm0
        and     eax, [rsp+KP_GM1]
        mov     edx, eax
        mov     r8d, [rsp+KP_G]
        mov     r9d, [rsp+KP_GM1]
        imul    rax, r14, AR_ARRAYS      ; cell_start base = bank base + 13*stride
        lea     r10, [rbx+rax]
        lea     r11, [rsp+KA_RUNBUF]
        call    neighbour_runs           ; eax = run count, buf filled
        mov     r13d, eax
        jmp     .have_runs
  .single_run:
        lea     r11, [rsp+KA_RUNBUF]
        mov     dword [r11], 0           ; base = 0
        mov     [r11+4], r15d            ; count = n
        mov     r13d, 1
  .have_runs:
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
        mov     eax, [rcx+4]             ; count
        mov     edx, eax
        and     edx, -8                  ; nfull = count & ~7 (full groups)
        lea     r8, [r9+rdx*4]           ; tail group byte offset = base + nfull*4
  .g8:
        cmp     r9, r8
        jae     .g8done
        avx2_force_group 0
        add     r9, 32
        jmp     .g8
  .g8done:
        and     eax, 7                   ; tail lanes = count & 7
        jz      .run_next                ; run length a lane multiple -> no tail
        mov     r10d, 8                  ; tailmask = tailwin[(8-tail) .. +7]; masks
        sub     r10d, eax                ;   the over-read past the run end, which
        lea     r8, [f_tailwin]          ;   would double-count the next cell's
        vmovups ymm5, [r8+r10*4]         ;   particles (a genuine wrapped neighbour)
        avx2_force_group 1               ; r9 already at the tail group offset
  .run_next:
        add     rcx, 8
        dec     r13d
        jnz     .run_loop
  .reduce:
        vextractf128 xmm0, ymm6, 1      ; fixed shuffle-reduce: fx -> xmm6[0]
        vaddps  xmm6, xmm6, xmm0
        vhaddps xmm6, xmm6, xmm6
        vhaddps xmm6, xmm6, xmm6
        vextractf128 xmm0, ymm7, 1      ; fy -> xmm7[0]
        vaddps  xmm7, xmm7, xmm0
        vhaddps xmm7, xmm7, xmm7
        vhaddps xmm7, xmm7, xmm7
        integrate_and_store 1           ; AVX2 path: VEX-encoded tail (issue #33)

        inc     r12d
        jmp     .outer
  .done:
        add     rsp, KA_FRAME
        ret

; ------------------------------------------------------------------
; wrap_body — p -= floor(p); if p >= 1.0: p = 0.0. One body, two encodings
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
; wrap_scalar — SSE-encoded wrap for the scalar reference path.
;   in/out:   xmm4 = p -> wrapped [0,1)
;   clobbers: xmm1, xmm3
wrap_scalar:
        TAIL_VEX = 0
        wrap_body
; wrap_scalar_avx — VEX.128-encoded wrap for the AVX2 tail (issue #33);
; bit-identical arithmetic to wrap_scalar, additionally zeroing the upper lanes
; of ymm1/3/4 (dead registers at this point in the pass).
;   in/out:   xmm4 = p -> wrapped [0,1)
;   clobbers: xmm1, xmm3
wrap_scalar_avx:
        TAIL_VEX = 1
        wrap_body

; ------------------------------------------------------------------
; step_core — n_steps x (build + pass over all n), advancing the frame.
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
