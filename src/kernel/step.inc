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
KP_FRAME   = 64                         ; frame size (16-mod-32 slack, scalar)

; ------------------------------------------------------------------
; build_core — copy bank OUT -> bank IN (brute-mode build).
;   in:       rcx = arena
;   out:      bank IN = bank OUT (all six arrays, incl. pads)
;   clobbers: rax, rcx, r10, flags; saves+restores rsi, rdi
;   MXCSR:    untouched (integer copy)
; ------------------------------------------------------------------
build_core:
        push    rsi
        push    rdi
        mov     eax, [rcx+AH_PADDED]    ; padded_n
        lea     rsi, [rcx+AH_SIZE]      ; bank OUT base
        imul    r10d, eax, AR_X_IN      ; 6*padded_n dwords = one bank
        lea     rdi, [rsi+r10*4]        ; bank IN base = OUT + 6*stride
        mov     ecx, r10d               ; copy the whole OUT bank -> IN
        cld
        rep     movsd
        pop     rdi
        pop     rsi
        ret

; ------------------------------------------------------------------
; pass_core — fused force+integrate for particles i in [first, last).
;   in:       rcx = arena, edx = first, r8d = last
;   out:      OUT[i].{x,y,vx,vy} integrated; OUT[i].{species,id} = IN[i].*
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, rsi, rdi, rbx, r12-r15,
;             xmm0-10, flags (kernel tier; the seam saved them)
;   MXCSR:    assumes the pinned seam MXCSR (round-nearest, FTZ/DAZ)
; ------------------------------------------------------------------
pass_core:
        sub     rsp, KP_FRAME
        mov     [rsp+KP_LAST], r8d
        mov     r12d, edx               ; i = first
        lea     rbx, [rcx+AH_SIZE]      ; bank base
        mov     r14d, [rcx+AH_PADDED]
        shl     r14, 2                  ; stride = padded_n*4
        mov     r15d, [rcx+AH_PARAMS+SP_N]
        lea     r13, [rcx+AH_PARAMS+SP_MATRIX]

        ; --- derive and cache the scalar constants -----------------------
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

  .outer:
        cmp     r12d, [rsp+KP_LAST]
        jae     .done

        elem_addr r10, AR_X_IN, r12     ; xi, yi = IN.x[i], IN.y[i]
        movss   xmm8, [r10]
        elem_addr r10, AR_Y_IN, r12
        movss   xmm9, [r10]
        xorps   xmm6, xmm6              ; fx
        xorps   xmm7, xmm7              ; fy

        ; inner-loop running source pointers into IN.x / IN.y / IN.species
        imul    rax, r14, AR_X_IN
        lea     rsi, [rbx+rax]
        imul    rax, r14, AR_Y_IN
        lea     rdi, [rbx+rax]
        imul    rax, r14, AR_SP_IN
        lea     r11, [rbx+rax]          ; ssp
        mov     r9d, r15d               ; j counter = n

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

        ; --- integrate particle i (semi-implicit Euler + clamp + wrap) ----
        elem_addr r10, AR_VX_IN, r12    ; vx = clamp(vx*fric + fx*dt, +/-vmax)
        movss   xmm0, [r10]
        mulss   xmm0, [rsp+KP_FRIC]
        movaps  xmm1, xmm6
        mulss   xmm1, [rsp+KP_DT]
        addss   xmm0, xmm1
        minss   xmm0, [rsp+KP_VMAX]
        maxss   xmm0, [rsp+KP_NEGVMAX]  ; xmm0 = vx'
        elem_addr r10, AR_VY_IN, r12
        movss   xmm2, [r10]
        mulss   xmm2, [rsp+KP_FRIC]
        movaps  xmm3, xmm7
        mulss   xmm3, [rsp+KP_DT]
        addss   xmm2, xmm3
        minss   xmm2, [rsp+KP_VMAX]
        maxss   xmm2, [rsp+KP_NEGVMAX]  ; xmm2 = vy'

        ; x' = wrap(xi + vx'*dt) ; y' = wrap(yi + vy'*dt)
        movaps  xmm4, xmm0
        mulss   xmm4, [rsp+KP_DT]
        addss   xmm4, xmm8
        call    wrap_scalar
        movaps  xmm10, xmm4             ; stash x' (wrap clobbers xmm1/3/4)
        movaps  xmm4, xmm2
        mulss   xmm4, [rsp+KP_DT]
        addss   xmm4, xmm9
        call    wrap_scalar             ; xmm4 = y'

        ; write OUT[i]: x'=xmm10, y'=xmm4, vx'=xmm0, vy'=xmm2
        elem_addr r10, AR_X_OUT, r12
        movss   [r10], xmm10
        elem_addr r10, AR_Y_OUT, r12
        movss   [r10], xmm4
        elem_addr r10, AR_VX_OUT, r12
        movss   [r10], xmm0
        elem_addr r10, AR_VY_OUT, r12
        movss   [r10], xmm2
        elem_addr r10, AR_SP_IN, r12    ; species, id pass through IN -> OUT
        mov     r9d, [r10]
        elem_addr r10, AR_SP_OUT, r12
        mov     [r10], r9d
        elem_addr r10, AR_ID_IN, r12
        mov     r9d, [r10]
        elem_addr r10, AR_ID_OUT, r12
        mov     [r10], r9d

        inc     r12d
        jmp     .outer
  .done:
        add     rsp, KP_FRAME
        ret

; ------------------------------------------------------------------
; wrap_scalar — p -= floor(p); if p >= 1.0: p = 0.0.
;   in/out:   xmm4 = p -> wrapped [0,1)
;   clobbers: xmm1, xmm3
; ------------------------------------------------------------------
wrap_scalar:
        roundss xmm1, xmm4, 1           ; floor (round toward -inf)
        subss   xmm4, xmm1              ; p - floor(p)
        movaps  xmm3, xmm4              ; mask = (p < 1.0) ? all-ones : 0
        cmpltss xmm3, [f_one]
        andps   xmm4, xmm3              ; p >= 1.0 -> 0
        ret

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
