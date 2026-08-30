; SPDX-License-Identifier: MIT
; init.inc - arena initialization (docs/MASTERPLAN.md, decisions 1, 7, 8).
;
; swarm_init validates the params, checks the caller's arena is large enough
; (fail-closed), resolves the SIMD code path against the CPU (fail-closed on an
; unsupportable request), writes the arena header, and seeds bank OUT from the
; deterministic RNG so the first frame is a pure function of the seed. It reads
; nothing back; state crosses out through swarm_read_state (a later slice).
;
; RNG init distribution (decision 1/8), per particle in this exact order so the
; C# oracle mirrors it draw-for-draw:
;   x = u01(next) ; y = u01(next) ; species = ((next>>32)*species_n)>>32 ; v = 0
; where u01(v) = (v >> 40) * 2^-24  (top 24 bits -> [0,1), exact in f32).
; Pads (i >= n) stay finite-zero with id = i, laid down by the initial memset
; and the identity fill.

; ------------------------------------------------------------------
; init_core - validate, size-check, resolve path, write header, seed OUT.
;   in:       rcx = arena, rdx = arena_bytes (u64), r8 = SwarmParams*
;   out:      eax = 0 on success (arena fully initialized), else IERR_*
;             (every failure is detected before the first arena write)
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, rsi, rdi, xmm0, xmm1, flags
;             (kernel tier; the seam saves the Win64 nonvolatiles + MXCSR.
;             xmm1 comes from the pp_check_range calls inside validate/layout.)
;   MXCSR:    assumes the pinned seam MXCSR; the u01 convert+multiply is exact
;   align:    rsp = 0 mod 32 at entry (seam contract)
; ------------------------------------------------------------------
init_core:
        sub     rsp, 48
        mov     [rsp], rcx              ; arena
        mov     [rsp+8], rdx            ; arena_bytes
        mov     [rsp+16], r8            ; params

        mov     rcx, r8                 ; 1. validate params
        call    pp_validate_params
        test    eax, eax
        jz      @f
        mov     eax, IERR_PARAMS
        jmp     .ret
    @@:
        mov     rcx, [rsp+16]           ; 2. size check (fail-closed)
        call    layout_bytes_core       ; rax = required bytes
        cmp     rax, [rsp+8]
        jbe     @f                      ; required <= arena_bytes -> ok
        mov     eax, IERR_ARENA_SMALL
        jmp     .ret
    @@:
        call    cpu_paths_core          ; 3. resolve the path (fail-closed)
        mov     r9d, eax                ; supported bits
        mov     r8, [rsp+16]
        ; CALLER MEMORY, RE-READ. pp_validate_params above read the caller's
        ; struct in place; this reads it again, and so do arena_dims_core and
        ; the rep movsq below. A caller mutating the struct between those reads
        ; puts unvalidated values into the arena header. Nothing here closes
        ; that window: it would need the struct copied to a private buffer
        ; before validation, which is an alignment-sensitive change to this
        ; routine's frame and is deliberately not made here (#203). The
        ; fail-closed claim in the banner is a different property and still
        ; holds - a rejected init writes no arena byte - and the window is only
        ; reachable by a caller mutating its own parameters concurrently with
        ; the call, which the seam contract does not permit.
        mov     r10d, [r8+SP_FORCE_PATH] ; a PATH_* id, not a CPU_* bit mask
        test    r10d, r10d              ; PATH_AUTO = 0
        jnz     .explicit
        test    r9d, CPU_AVX2           ; auto: AVX2 > fail. THE LADDER MAY ONLY
        jz      .no_path                ;   RESOLVE TO AN ID THAT HAS A BODY,
        mov     r10d, PATH_AVX2         ;   because AH_PATH names the body
        jmp     .path_ok                ;   pass_core will run (#316).
                                        ;   PATH_AVX512 has no body until
                                        ;   #123/#164 build the zmm
                                        ;   instantiation, and the rung that
                                        ;   used to sit above this one resolved
                                        ;   to it on any CPU reporting
                                        ;   CPU_AVX512 - writing a header word
                                        ;   that named AVX-512 while the ymm
                                        ;   body ran. PATH_AVX2_FMA is absent
                                        ;   for a different reason (#162): it
                                        ;   has a body, and is opt-in only,
                                        ;   like PATH_SCALAR, so no caller gets
                                        ;   different arithmetic by not asking.
  .explicit:
        cmp     r10d, PATH_SCALAR       ; scalar: accepted against any CPU (the
        je      .path_ok               ;   reference path; its own floor is
                                       ;   SSE4.1, see abi.inc's PATH_SCALAR)
        cmp     r10d, PATH_AVX2
        jne     .want_fma
        test    r9d, CPU_AVX2           ; AVX2 requested
        jz      .no_path
        jmp     .path_ok
  .want_fma:
        cmp     r10d, PATH_AVX2_FMA     ; the fused ymm body (#162). Gated on
        jne     .no_path                ;   CPU_AVX2 and on nothing else:
        test    r9d, CPU_AVX2           ;   cpu_paths_core cannot report that
        jz      .no_path                ;   bit without FMA, so a CPU that
        jmp     .path_ok                ;   passes here has the unit.
                                        ; PATH_AVX512 leaves through the jne
                                        ;   above, with every word that is not
                                        ;   an accepted id. That is deliberate
                                        ;   and it is the last arm: see the
                                        ;   note at .no_path.
  .path_ok:
        mov     [rsp+24], r10d          ; chosen path id

        ; --- 4. write the arena header (no earlier write: all checks passed)
        mov     r11, [rsp]              ; arena base
        lea     rdi, [r11]              ; zero the metadata region [0, AH_PARAMS)
        xor     eax, eax                ;   so no reserved header byte is ever
        mov     ecx, AH_PARAMS/8        ;   undefined (frame ends up 0 for free)
        cld
        rep     stosq
        mov     rax, ARENA_MAGIC
        mov     [r11+AH_MAGIC], rax
        mov     dword [r11+AH_ABI], SWARM_ABI_VERSION
        mov     eax, [rsp+24]
        mov     [r11+AH_PATH], eax
        mov     rcx, [rsp+16]
        call    arena_dims_core         ; eax = padded_n, edx = g
        mov     [r11+AH_PADDED], eax
        mov     [r11+AH_G], edx
        mov     r8, [rsp+16]
        mov     rax, [r8+SP_SEED]
        mov     [r11+AH_RNG], rax       ; live RNG state = seed

        ; The third read of caller memory; see the note at the SP_FORCE_PATH
        ; read above. "validated" here means validated as they were read then,
        ; not re-checked now.
        mov     rsi, r8                 ; copy the validated params into the header
        lea     rdi, [r11+AH_PARAMS]
        mov     ecx, SP_SIZE/8
        rep     movsq                   ; DF still clear from the cld above

        ; --- 5. seed bank OUT + its cell ids ------------------------------
        mov     rcx, r11                ; seed_bank_out reads the header it needs
        call    seed_bank_out
        mov     rcx, [rsp]              ; arena (r11 was consumed as the rng state)
        call    seed_cellids            ; valid keys for the first grid build
        xor     eax, eax
        jmp     .ret

  .no_path:
        ; PATH_AVX512 IS REFUSED HERE ON EVERY HOST, one whose CPU reports the
        ; feature included (#316). The id has no body: pass_core names
        ; PATH_AVX2 and PATH_AVX2_FMA, and sends everything else to
        ; pass_scalar, so accepting 2 would either write a header word naming a
        ; path the ymm body was about to run, or hand a caller who asked for
        ; sixteen lanes the reference path. A caller that asks for AVX-512 now
        ; gets IERR_PATH and no arena byte, which is a reading it can act on,
        ; where a success code and the AVX2 pass was not. #123/#164 build the
        ; zmm instantiation; the change that makes the claim true is the one
        ; that restores this id here and in the auto ladder above, CPU-gated on
        ; CPU_AVX512 in both places.
        mov     eax, IERR_PATH
  .ret:
        add     rsp, 48
        ret

; ------------------------------------------------------------------
; seed_bank_out - lay down bank OUT and the live RNG state from the header.
;   in:       rcx = arena base (header already written: AH_PADDED, AH_RNG,
;             AH_PARAMS.n / species_n valid)
;   out:      bank OUT filled (x,y,species drawn for i<n; v=0; id=i for all;
;             pads finite-zero); AH_RNG updated to the post-init state
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, xmm0, flags; saves+restores
;             rbx, rsi, rdi itself (arena base must survive rep stosd, which
;             clobbers rcx)
;   MXCSR:    assumes the pinned seam MXCSR (exact u01)
; ------------------------------------------------------------------
seed_bank_out:
        push    rbx
        push    rsi
        push    rdi
        mov     rbx, rcx                ; rbx = arena, survives the string ops
        mov     r8d, [rbx+AH_PADDED]    ; padded_n
        lea     rsi, [rbx+AH_SIZE]      ; bank OUT base (x_out)

        ; zero all six OUT arrays: x=y=vx=vy=species=0 (incl. every pad tail);
        ; id is overwritten by the identity fill next.
        mov     rdi, rsi
        mov     eax, r8d
        imul    eax, AR_ID_OUT+1        ; 6 arrays * padded_n dwords
        mov     ecx, eax
        xor     eax, eax
        cld
        rep     stosd                   ; clobbers rcx, rdi, rax

        ; id_out[i] = i for all i (identity; pads therefore get n, n+1, ...)
        mov     eax, r8d
        imul    eax, AR_ID_OUT          ; id array starts 5*padded_n dwords in
        lea     rdi, [rsi+rax*4]        ; &id_out[0]
        xor     eax, eax
        mov     ecx, r8d
  .id_fill:
        mov     [rdi], eax
        add     rdi, 4
        inc     eax
        dec     ecx
        jnz     .id_fill

        ; draw x, y, species for the first n particles
        mov     r9d, [rbx+AH_PARAMS+SP_N]
        test    r9d, r9d
        jz      .done
        mov     r10d, [rbx+AH_PARAMS+SP_SPECIES_N]
        mov     eax, r8d                ; stride = padded_n * 4
        shl     eax, 2
        lea     rdi, [rsi+rax]          ; &y_out[0]       = base + 1*stride
        lea     rdx, [rsi+rax*4]        ; &species_out[0] = base + 4*stride
        mov     r11, [rbx+AH_RNG]       ; live rng state
        ; loop: rsi=&x_out, rdi=&y_out, rdx=&sp_out, r11=rng, r9=remaining,
        ;   r10=species_n, r8=rng tmp / value scratch, rax=rng value
  .draw:
        rng_next r11, rax, r8           ; v1 -> x
        mov     r8, rax
        shr     r8, 40                  ; top 24 bits -> [0, 2^24)
        cvtsi2ss xmm0, r8d
        mulss   xmm0, [inv_2p24]
        movss   [rsi], xmm0
        add     rsi, 4

        rng_next r11, rax, r8           ; v2 -> y
        mov     r8, rax
        shr     r8, 40
        cvtsi2ss xmm0, r8d
        mulss   xmm0, [inv_2p24]
        movss   [rdi], xmm0
        add     rdi, 4

        rng_next r11, rax, r8           ; v3 -> species = ((v>>32)*species_n)>>32
        shr     rax, 32
        imul    rax, r10
        shr     rax, 32
        mov     [rdx], eax
        add     rdx, 4

        dec     r9d
        jnz     .draw

        mov     [rbx+AH_RNG], r11       ; write back the post-init rng state
  .done:
        pop     rdi
        pop     rsi
        pop     rbx
        ret

align 4
inv_2p24 dd 0x33800000                  ; 2^-24 as f32 (exact)
