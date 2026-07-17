; plot.inc — rasterize the current state into a BGRA framebuffer (decision 9).
;
; Pure raster, golden-testable: clear to the background, then plot each of the
; first n particles as a single per-species coloured pixel. Reads bank OUT
; (the current state). The (x,y) in [0,1) map is truncated to a pixel and
; clamped to [0, w-1] x [0, h-1] (decision 9). The clamp is a complete
; backstop, not just the expected boundary: wrap already keeps positions in
; [0,1), but the two-sided clamp also neutralizes a negative / NaN / out-of-
; range value (cvttss2si returns INT_MIN, sign set) so NO position can ever
; index outside the framebuffer, and species is masked into the 8-entry table.
;
; Framebuffer is 32-bit little-endian 0x00RRGGBB (the exe's BI_RGB DIB). No API
; calls: the caller owns the pixel buffer.

PLOT_BG = 0x001A1A22                     ; background (matches the exe clear)

; ------------------------------------------------------------------
; plot_core — raster the state into a w*h BGRA buffer.
;   in:       rcx = arena, rdx = bgra (w*h u32), r8d = w, r9d = h
;   out:      bgra cleared to PLOT_BG, then one coloured pixel per particle
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, rbx, rsi, rdi, r12-r15,
;             xmm0-4, flags (kernel tier; the seam saved them)
;   MXCSR:    assumes the pinned seam MXCSR (deterministic x*w)
; ------------------------------------------------------------------
plot_core:
        ; extract everything derived from the arena FIRST: the clear's rep
        ; stosd clobbers rcx, so reading [rcx+...] afterward would fault.
        lea     rbx, [rcx+AH_SIZE]     ; bank base
        mov     r14d, [rcx+AH_PADDED]
        shl     r14, 2                 ; stride
        mov     r15d, [rcx+AH_PARAMS+SP_N]
        mov     rsi, rdx               ; bgra base (survives the loop)
        lea     r13, [swarm_palette]   ; RIP-relative: ASLR-safe base
        cvtsi2ss xmm3, r8d             ; wf = (float)w
        cvtsi2ss xmm4, r9d             ; hf = (float)h

        ; clear the framebuffer to the background colour
        mov     rdi, rsi               ; bgra
        mov     eax, r8d
        imul    eax, r9d               ; w*h pixels
        mov     ecx, eax
        mov     eax, PLOT_BG
        cld
        rep     stosd

        xor     ecx, ecx               ; a constant 0 for the low clamp (rcx is
                                       ;   free after the clear; rbx holds base)
        xor     r12d, r12d             ; i

  .loop:
        cmp     r12d, r15d
        jae     .done
        ; px = clamp(trunc(x*wf), 0, w-1). Both belts are load-bearing: the
        ; high side catches a boundary 1.0, the low side catches a negative /
        ; NaN / out-of-range value (cvttss2si returns INT_MIN, sign set) so no
        ; particle position can ever index outside the framebuffer.
        elem_addr r10, AR_X_OUT, r12
        movss   xmm0, [r10]
        mulss   xmm0, xmm3
        cvttss2si eax, xmm0
        lea     r11d, [r8d-1]
        cmp     eax, r11d
        cmovg   eax, r11d              ; min against w-1
        test    eax, eax
        cmovs   eax, ecx               ; max against 0 (also neutralizes INT_MIN)
        ; py = clamp(trunc(y*hf), 0, h-1)
        elem_addr r10, AR_Y_OUT, r12
        movss   xmm1, [r10]
        mulss   xmm1, xmm4
        cvttss2si edx, xmm1
        lea     r11d, [r9d-1]
        cmp     edx, r11d
        cmovg   edx, r11d
        test    edx, edx
        cmovs   edx, ecx
        ; idx = py*w + px
        imul    edx, r8d
        add     edx, eax
        ; colour = palette[species & 7] (& 7 keeps a stray species in the
        ; 8-entry table; init guarantees species < species_n <= 8 anyway)
        elem_addr r10, AR_SP_OUT, r12
        mov     r11d, [r10]
        and     r11d, 7
        mov     eax, [r13+r11*4]
        mov     [rsi+rdx*4], eax       ; bgra[idx] = colour
        inc     r12d
        jmp     .loop
  .done:
        ret

align 16
swarm_palette:
        dd 0x00FF4040                  ; 0 red
        dd 0x0040FF40                  ; 1 green
        dd 0x004080FF                  ; 2 blue
        dd 0x00FFD040                  ; 3 amber
        dd 0x00FF40FF                  ; 4 magenta
        dd 0x0040FFFF                  ; 5 cyan
        dd 0x00FF8020                  ; 6 orange
        dd 0x00A060FF                  ; 7 violet
