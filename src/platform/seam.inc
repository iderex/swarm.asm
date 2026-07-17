; seam.inc — the DLL-export / thread-entry seam frame (platform layer:
; ldmxcsr/stmxcsr/vzeroupper are banned inside src/kernel/).
;
; Every kernel-calling export wears this pair uniformly — including exports
; that move no FP today — so a later FP addition cannot miss the MXCSR pin
; (docs/MASTERPLAN.md, decision 2). The frame provides the full seam tier of
; decision 4: the correct Win64 nonvolatile set (rbx, rsi, rdi, r12-r15,
; xmm6-15), MXCSR save / pin 0x9FC0 / restore, vzeroupper before return,
; and a 32-byte-realigned stack such that the kernel call's 8-byte return
; push lands the KERNEL ENTRY at rsp = 0 mod 32 exactly.
;
; Frame arithmetic (load-bearing — do not "fix" without re-deriving):
;   export entry        rsp = 8 mod 16   (Win64 call)
;   push rbp            rsp = 0 mod 16   (rbp = entry-8; a 5th+ stack arg would
;                                         sit at [rbp+48+8k]: +8 return, +32
;                                         shadow. No current export takes >4.)
;   7 pushes            rsp = 8 mod 16
;   and rsp, -32        rsp = 0 mod 32
;   sub rsp, 184        rsp = 8 mod 32   -> call pushes 8 -> callee 0 mod 32
;   [rsp+0]   caller MXCSR   [rsp+4]  pin scratch
;   [rsp+8+16k] xmm6+k saves (rsp+8 = 16 mod 32: vmovaps-legal)
;   [rsp+168..183]           padding
;
; seam_leave restores in reverse and returns; rbp-56 re-locates the seven
; GPR saves regardless of the realignment loss.

SEAM_MXCSR = 0x9FC0                     ; FTZ+DAZ, all masked, round-nearest

macro seam_enter {
        push    rbp
        mov     rbp, rsp
        push    rbx
        push    rsi
        push    rdi
        push    r12
        push    r13
        push    r14
        push    r15
        and     rsp, -32
        sub     rsp, 184
        vmovaps [rsp+8],    xmm6
        vmovaps [rsp+24],   xmm7
        vmovaps [rsp+40],   xmm8
        vmovaps [rsp+56],   xmm9
        vmovaps [rsp+72],   xmm10
        vmovaps [rsp+88],   xmm11
        vmovaps [rsp+104],  xmm12
        vmovaps [rsp+120],  xmm13
        vmovaps [rsp+136],  xmm14
        vmovaps [rsp+152],  xmm15
        stmxcsr [rsp]
        mov     dword [rsp+4], SEAM_MXCSR
        ldmxcsr [rsp+4]
}

macro seam_leave {
        ldmxcsr [rsp]
        vmovaps xmm6,  [rsp+8]
        vmovaps xmm7,  [rsp+24]
        vmovaps xmm8,  [rsp+40]
        vmovaps xmm9,  [rsp+56]
        vmovaps xmm10, [rsp+72]
        vmovaps xmm11, [rsp+88]
        vmovaps xmm12, [rsp+104]
        vmovaps xmm13, [rsp+120]
        vmovaps xmm14, [rsp+136]
        vmovaps xmm15, [rsp+152]
        vzeroupper
        lea     rsp, [rbp-56]
        pop     r15
        pop     r14
        pop     r13
        pop     r12
        pop     rdi
        pop     rsi
        pop     rbx
        pop     rbp
        ret
}

; seam_wrap export, core — the standard shape every FP-seam export takes:
; seam_enter / call <core> / seam_leave, verbatim, nothing in between. Not an
; indirection that hides the seam — grep for "seam_wrap" finds every site, and
; this macro IS the whole contract for each of them (its own register-contract
; header, above, already covers what seam_enter/seam_leave do). A wrapper that
; needs anything beyond "call the core" between enter and leave does not fit
; this shape and stays hand-written with its own seam_enter/call/seam_leave.
macro seam_wrap export, core {
  export:
        seam_enter
        call    core
        seam_leave
}
