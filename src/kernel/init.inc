; init.inc — arena initialization (docs/MASTERPLAN.md, decisions 1, 7, 8).
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
; init_core — validate, size-check, resolve path, write header, seed OUT.
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
        mov     r10d, [r8+SP_FORCE_PATH] ; 0 auto / 1 AVX2 / 2 AVX-512 / 3 scalar
        test    r10d, r10d
        jnz     .explicit
        test    r9d, CPU_AVX512         ; auto: AVX-512 > AVX2 > fail
        jz      .try_avx2
        mov     r10d, 2
        jmp     .path_ok
  .try_avx2:
        test    r9d, CPU_AVX2
        jz      .no_path
        mov     r10d, 1
        jmp     .path_ok
  .explicit:
        cmp     r10d, 3                 ; scalar: always available (the reference
        je      .path_ok               ;   path; no CPU feature required)
        cmp     r10d, 1
        jne     .want512
        test    r9d, CPU_AVX2           ; AVX2 requested
        jz      .no_path
        jmp     .path_ok
  .want512:
        test    r9d, CPU_AVX512         ; AVX-512 requested
        jz      .no_path
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
        mov     eax, IERR_PATH
  .ret:
        add     rsp, 48
        ret

; ------------------------------------------------------------------
; seed_bank_out — lay down bank OUT and the live RNG state from the header.
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
