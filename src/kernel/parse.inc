; parse.inc — the preset parser (docs/MASTERPLAN.md, decision 10).
;
; Fail-closed two-phase commit: phase 1 tokenizes and validates everything
; into a stack-local staging image of SwarmParams; phase 2 is one rep movsq
; executed only on full success. Any error returns
; 0x80000000 or (PERR_* shl 20) or min(line, 0xFFFFF) with [out] untouched —
; [out] is dereferenced at exactly one code location (the commit).
;
; Bounds discipline over the NON-terminated (text, len) buffer: pp_next_line
; is the only routine that reads the outer buffer, through a rem==0-guarded
; single load; every other helper works on the line slice it is handed and
; reads through one guarded idiom (test len / jz before one movzx). No
; routine scans for a terminator.
;
; All routines are kernel tier (decision 4): args rcx/rdx/r8/r9, no shadow
; space, every register caller-owned. The driver keeps all durable state in
; its frame and reloads after every helper call.

; --- driver frame offsets (parse_preset_core, sub rsp, 352) ---------------
PPF_STAGING  = 0                        ; 304-byte SwarmParams image
PPF_SEEN     = 304                      ; u32 seen-bitmask (8 key bits)
PPF_ROW      = 308                      ; u32 matrix rows parsed
PPF_OUT      = 312                      ; spilled out pointer
PPF_CUR_P    = 320                      ; cursor: next unread byte
PPF_CUR_REM  = 328                      ; cursor: bytes remaining (u32)
PPF_CUR_LINE = 332                      ; cursor: 1-based fetched-line count
PPF_LINE_P   = 336                      ; current line slice: start
PPF_LINE_LEN = 344                      ; current line slice: length (u32)
PPF_SIZE     = 352

; --- key/range table row layout (const data, kr_table) --------------------
KR_NAME    = 0                          ; qword: name bytes pointer
KR_NAMELEN = 8                          ; dd
KR_KIND    = 12                         ; dd: 0 u32, 1 u64(hex ok), 2 f32
KR_OFF     = 16                         ; dd: SwarmParams offset
KR_BIT     = 20                         ; dd: seen-mask bit index
KR_LO      = 24                         ; dd: u32 bound or f32 bits
KR_HI      = 28                         ; dd
KR_FLAGS   = 32                         ; dd: bit0 lo-strict, bit1 hi-strict
KR_SIZE    = 40                         ; incl. one dd pad: keeps every row's
                                        ;   leading dq name pointer 8-aligned

KIND_U32 = 0
KIND_U64 = 1
KIND_F32 = 2

PP_MANT_CAP = 1000000000000000          ; 10^15: pre-multiply mantissa cap

; ------------------------------------------------------------------
; parse_preset_core — parse a preset buffer into out, fail-closed.
;   in:       rcx = text (may be unterminated), edx = len, r8 = out
;             (SwarmParams*, 304 bytes, caller-owned)
;   out:      eax = 0 and *out fully written, or eax = packed negative
;             error (code shl 20 or line, bit 31 set, bit 30 zero) and
;             *out byte-untouched
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, rsi, rdi, xmm0, xmm1,
;             flags (kernel tier: every register caller-owned; rsi/rdi
;             feed rep stosq/movsq - the seam saves them for the ABI)
;   MXCSR:    assumes the pinned seam MXCSR (decision 2); the one divsd
;             per float token is IEEE-exact for every accepted mantissa
;   align:    rsp = 0 mod 32 at entry (seam contract)
; ------------------------------------------------------------------
parse_preset_core:
        sub     rsp, PPF_SIZE
        mov     [rsp+PPF_OUT], r8
        mov     [rsp+PPF_CUR_P], rcx
        mov     [rsp+PPF_CUR_REM], edx
        mov     dword [rsp+PPF_CUR_LINE], 0
        mov     dword [rsp+PPF_SEEN], 0
        mov     dword [rsp+PPF_ROW], 0
        cld
        lea     rdi, [rsp+PPF_STAGING]  ; zero staging: dead matrix slots,
        xor     eax, eax                ; force_path and flags stay 0
        mov     ecx, SP_SIZE/8
        rep     stosq

        ; --- version line: exactly the tokens `swarm`, `1` ---------------
        call    pp_fetch_content_line
        test    eax, eax
        jz      .eof
        lea     rcx, [rsp+PPF_LINE_P]
        call    pp_next_token
        mov     rcx, rax
        mov     r8, kw_swarm
        mov     r9d, 5
        call    pp_match_key
        test    eax, eax
        jz      .bad_version
        lea     rcx, [rsp+PPF_LINE_P]
        call    pp_next_token
        cmp     edx, 1
        jne     .bad_version
        cmp     byte [rax], '1'
        jne     .bad_version
        call    pp_line_must_end
        test    eax, eax
        jnz     .extra_token

        ; --- key lines until the `matrix` token ---------------------------
  .key_loop:
        call    pp_fetch_content_line
        test    eax, eax
        jz      .eof
        lea     rcx, [rsp+PPF_LINE_P]
        call    pp_next_token
        mov     r10, rax                ; key token
        mov     r11d, edx
        mov     rcx, rax
        mov     edx, r11d
        mov     r8, kw_matrix
        mov     r9d, 6
        call    pp_match_key
        test    eax, eax
        jnz     .matrix_begin
        xor     r9d, r9d                ; table row index
  .key_scan:
        imul    eax, r9d, KR_SIZE
        lea     r8, [kr_table+rax]
        mov     rcx, r10
        mov     edx, r11d
        push    r9                      ; helpers own every register
        push    r10
        push    r11
        mov     r9d, [r8+KR_NAMELEN]
        mov     r8, [r8+KR_NAME]
        call    pp_match_key
        pop     r11
        pop     r10
        pop     r9
        test    eax, eax
        jnz     .key_hit
        inc     r9d
        cmp     r9d, 8
        jb      .key_scan
        mov     r8d, PERR_UNKNOWN_KEY
        jmp     .fail_line

  .key_hit:
        imul    eax, r9d, KR_SIZE
        lea     r10, [kr_table+rax]     ; r10 = table row (held across calls
        mov     ecx, [r10+KR_BIT]       ;   via re-derive after each helper)
        bts     dword [rsp+PPF_SEEN], ecx
        jnc     @f
        mov     r8d, PERR_DUP_KEY
        jmp     .fail_line
    @@:
        mov     [rsp+PPF_ROW], r9d      ; stash row index (matrix ctr unused
        lea     rcx, [rsp+PPF_LINE_P]   ;   until the matrix phase)
        call    pp_next_token
        test    edx, edx
        jnz     @f
        mov     r8d, PERR_NUM_SYNTAX    ; missing value
        jmp     .fail_line
    @@:
        mov     r9d, [rsp+PPF_ROW]
        imul    r9d, r9d, KR_SIZE
        lea     r10, [kr_table+r9]
        mov     ecx, [r10+KR_KIND]
        cmp     ecx, KIND_F32
        je      .val_f32
        ; integer kinds: u32 ranged, or seed (u64, hex allowed, unranged)
        mov     r8d, 0
        cmp     ecx, KIND_U64
        jne     @f
        mov     r8d, 1
    @@:
        mov     rcx, rax
        call    pp_parse_u64
        test    eax, eax
        jnz     .fail_code_eax
        mov     r9d, [rsp+PPF_ROW]
        imul    r9d, r9d, KR_SIZE
        lea     r10, [kr_table+r9]
        cmp     dword [r10+KR_KIND], KIND_U64
        je      .store_u64
        mov     ecx, [r10+KR_LO]
        cmp     rdx, rcx
        jb      .range_fail
        mov     ecx, [r10+KR_HI]
        cmp     rdx, rcx
        ja      .range_fail
        mov     ecx, [r10+KR_OFF]
        mov     [rsp+PPF_STAGING+rcx], edx
        jmp     .key_done
  .store_u64:
        mov     ecx, [r10+KR_OFF]
        mov     [rsp+PPF_STAGING+rcx], rdx
        jmp     .key_done
  .val_f32:
        mov     rcx, rax
        call    pp_parse_f32
        test    eax, eax
        jnz     .fail_code_eax
        mov     r9d, [rsp+PPF_ROW]
        imul    r9d, r9d, KR_SIZE
        lea     rcx, [kr_table+r9]
        call    pp_check_range          ; xmm0 preserved by pp_check_range
        test    eax, eax
        jnz     .range_fail
        mov     r9d, [rsp+PPF_ROW]
        imul    r9d, r9d, KR_SIZE
        lea     r10, [kr_table+r9]
        mov     ecx, [r10+KR_OFF]
        movss   [rsp+PPF_STAGING+rcx], xmm0
  .key_done:
        call    pp_line_must_end
        test    eax, eax
        jnz     .extra_token
        jmp     .key_loop

        ; --- matrix block --------------------------------------------------
  .matrix_begin:
        call    pp_line_must_end
        test    eax, eax
        jnz     .extra_token
        cmp     dword [rsp+PPF_SEEN], 0xFF
        je      @f
        mov     r8d, PERR_MISSING_KEY   ; also enforces species-before-matrix
        jmp     .fail_line
    @@:
        mov     dword [rsp+PPF_ROW], 0
  .row_loop:
        call    pp_fetch_content_line
        test    eax, eax
        jz      .eof
        lea     rcx, [rsp+PPF_LINE_P]
        call    pp_next_token
        mov     r10, rax
        mov     r11d, edx
        mov     rcx, rax
        mov     edx, r11d
        mov     r8, kw_end
        mov     r9d, 3
        call    pp_match_key
        test    eax, eax
        jnz     .end_token
        mov     eax, [rsp+PPF_ROW]      ; a value row where `end` is due
        cmp     eax, [rsp+PPF_STAGING+SP_SPECIES_N]
        jb      @f
        mov     r8d, PERR_MATRIX_SHAPE
        jmp     .fail_line
    @@:
        xor     r9d, r9d                ; column index
  .col_loop:
        mov     rcx, r10
        mov     edx, r11d
        push    r9
        call    pp_parse_f32
        pop     r9
        test    eax, eax
        jnz     .fail_code_eax
        push    r9
        mov     rcx, kr_matrix
        call    pp_check_range
        pop     r9
        test    eax, eax
        jnz     .range_fail
        cmp     r9d, [rsp+PPF_STAGING+SP_SPECIES_N]
        jae     .shape_fail             ; too many columns
        mov     eax, [rsp+PPF_ROW]
        shl     eax, 3                  ; matrix stride is 8 f32 regardless
        add     eax, r9d                ;   of species_n
        movss   [rsp+PPF_STAGING+SP_MATRIX+rax*4], xmm0
        inc     r9d
        push    r9
        lea     rcx, [rsp+8+PPF_LINE_P]
        call    pp_next_token
        pop     r9
        mov     r10, rax
        mov     r11d, edx
        test    edx, edx
        jnz     .col_loop
        cmp     r9d, [rsp+PPF_STAGING+SP_SPECIES_N]
        jne     .shape_fail             ; too few columns
        inc     dword [rsp+PPF_ROW]
        jmp     .row_loop

  .end_token:
        mov     eax, [rsp+PPF_ROW]
        cmp     eax, [rsp+PPF_STAGING+SP_SPECIES_N]
        jne     .shape_fail             ; `end` before species_n rows
        call    pp_line_must_end
        test    eax, eax
        jnz     .extra_token

        ; --- only blank lines may follow `end` -----------------------------
  .trailing_loop:
        call    pp_fetch_content_line
        test    eax, eax
        jz      .commit
        mov     r8d, PERR_TRAILING
        jmp     .fail_line

        ; --- phase 2: the single [out] dereference -------------------------
  .commit:
        mov     dword [rsp+PPF_STAGING+SP_VERSION], 1
        cld
        lea     rsi, [rsp+PPF_STAGING]
        mov     rdi, [rsp+PPF_OUT]
        mov     ecx, SP_SIZE/8
        rep     movsq
        xor     eax, eax
        jmp     .ret

  .eof:
        mov     r8d, PERR_MISSING_END
        mov     r9d, [rsp+PPF_CUR_LINE]
        inc     r9d
        jmp     .fail
  .bad_version:
        mov     r8d, PERR_VERSION
        jmp     .fail_line
  .extra_token:
        mov     r8d, PERR_EXTRA_TOKEN
        jmp     .fail_line
  .range_fail:
        mov     r8d, PERR_RANGE
        jmp     .fail_line
  .shape_fail:
        mov     r8d, PERR_MATRIX_SHAPE
        jmp     .fail_line
  .fail_code_eax:
        mov     r8d, eax                ; a pp_parse_* PERR code
  .fail_line:
        mov     r9d, [rsp+PPF_CUR_LINE]
  .fail:
        cmp     r9d, 0xFFFFF
        jbe     @f
        mov     r9d, 0xFFFFF
    @@:
        mov     eax, r8d
        shl     eax, 20
        or      eax, r9d
        or      eax, 0x80000000
  .ret:
        add     rsp, PPF_SIZE
        ret

; ------------------------------------------------------------------
; pp_fetch_content_line — advance to the next line with at least one
; token, loading its slice into the frame; blank lines are skipped.
;   in:       implicit: the caller's frame at rsp+8 (called from
;             parse_preset_core only — reads/writes the cursor block
;             through rsp-relative addressing after the return slot)
;   out:      eax = 1 slice loaded (line_p/line_len rewound to the line
;             start), eax = 0 at end of input
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, flags
;   MXCSR:    untouched
; ------------------------------------------------------------------
pp_fetch_content_line:
  .next:
        lea     rcx, [rsp+8+PPF_CUR_P]
        call    pp_next_line
        test    eax, eax
        jz      .ret
        mov     r10, [rsp+8+PPF_LINE_P] ; remember the slice, probe a token
        mov     r11d, [rsp+8+PPF_LINE_LEN]
        lea     rcx, [rsp+8+PPF_LINE_P]
        call    pp_next_token
        test    edx, edx
        jz      .next                   ; blank line: skip
        mov     [rsp+8+PPF_LINE_P], r10 ; rewind: caller re-tokenizes
        mov     [rsp+8+PPF_LINE_LEN], r11d
        mov     eax, 1
  .ret:
        ret

; ------------------------------------------------------------------
; pp_line_must_end — assert the current line slice has no further token.
;   in:       implicit: the caller's frame at rsp+8 (parse_preset_core only)
;   out:      eax = 0 line exhausted, eax = 1 surplus token present
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, flags
;   MXCSR:    untouched
; ------------------------------------------------------------------
pp_line_must_end:
        lea     rcx, [rsp+8+PPF_LINE_P]
        call    pp_next_token
        xor     eax, eax
        test    edx, edx
        setnz   al
        ret

; ------------------------------------------------------------------
; pp_next_line — fetch the next line from the outer buffer.
;   in:       rcx = cursor block {p: u64, rem: u32, line_no: u32,
;             line_p: u64, line_len: u32} (frame memory)
;   out:      eax = 1 and the line slice stored (one trailing CR
;             stripped, the LF consumed, line_no incremented), or
;             eax = 0 at end of input (cursor untouched)
;   clobbers: rax, rcx, rdx, r8, r9, flags
;   MXCSR:    untouched
;   note:     the ONLY routine reading the outer buffer; the single
;             load is rem==0-guarded, so no input length can read
;             out of bounds
; ------------------------------------------------------------------
pp_next_line:
        mov     r8, [rcx]               ; p
        mov     r9d, [rcx+8]            ; rem
        test    r9d, r9d
        jz      .eof
        mov     [rcx+16], r8            ; line_p = p
  .scan:
        test    r9d, r9d
        jz      .no_lf
        movzx   eax, byte [r8]
        cmp     al, 10
        je      .lf
        inc     r8
        dec     r9d
        jmp     .scan
  .lf:
        mov     rdx, r8
        sub     rdx, [rcx+16]           ; line length excl. LF
        inc     r8                      ; consume the LF
        dec     r9d
        jmp     .store
  .no_lf:
        mov     rdx, r8
        sub     rdx, [rcx+16]           ; final line without newline
  .store:
        test    edx, edx
        jz      @f
        mov     rax, [rcx+16]
        cmp     byte [rax+rdx-1], 13    ; strip one trailing CR (in-slice:
        jne     @f                      ;   len > 0 was just checked)
        dec     edx
    @@:
        mov     [rcx], r8
        mov     [rcx+8], r9d
        mov     [rcx+24], edx           ; line_len
        inc     dword [rcx+12]          ; line_no
        mov     eax, 1
        ret
  .eof:
        xor     eax, eax
        ret

; ------------------------------------------------------------------
; pp_next_token — take the next space/tab-delimited token off the
; current line slice.
;   in:       rcx = line slice {line_p: u64, line_len: u32} (frame
;             memory; consumed in place)
;   out:      rax = token start, edx = token length (0 = line exhausted)
;   clobbers: rax, rcx, rdx, r8, r9, flags
;   MXCSR:    untouched
; ------------------------------------------------------------------
pp_next_token:
        mov     r8, [rcx]               ; lp
        mov     r9d, [rcx+8]            ; ll
  .skip:
        test    r9d, r9d
        jz      .empty
        movzx   eax, byte [r8]
        cmp     al, ' '
        je      .blank
        cmp     al, 9
        je      .blank
        jmp     .tok
  .blank:
        inc     r8
        dec     r9d
        jmp     .skip
  .tok:
        mov     rax, r8                 ; token start
  .body:
        test    r9d, r9d
        jz      .fin
        movzx   edx, byte [r8]
        cmp     dl, ' '
        je      .fin
        cmp     dl, 9
        je      .fin
        inc     r8
        dec     r9d
        jmp     .body
  .fin:
        mov     rdx, r8
        sub     rdx, rax                ; token length
        mov     [rcx], r8
        mov     [rcx+8], r9d
        ret
  .empty:
        mov     [rcx], r8
        mov     [rcx+8], r9d
        xor     eax, eax
        xor     edx, edx
        ret

; ------------------------------------------------------------------
; pp_match_key — exact byte comparison of a token against a name.
;   in:       rcx = token ptr, edx = token len, r8 = name ptr,
;             r9d = name len
;   out:      eax = 1 exact match, else 0
;   clobbers: rax, r9, flags (rcx, rdx, r8, r10, r11 preserved — the
;             matrix/key loops hold the live token pointer in r10 across
;             this call, so this routine must not touch it; the index runs
;             in r9, whose namelen input is dead once the length gate passes)
;   MXCSR:    untouched
; ------------------------------------------------------------------
pp_match_key:
        cmp     edx, r9d
        jne     .no
        xor     r9d, r9d
  .cmp:
        cmp     r9d, edx
        je      .yes
        mov     al, [rcx+r9]
        cmp     al, [r8+r9]
        jne     .no
        inc     r9d
        jmp     .cmp
  .yes:
        mov     eax, 1
        ret
  .no:
        xor     eax, eax
        ret

; ------------------------------------------------------------------
; pp_parse_u64 — unsigned integer token: decimal, or 0x-hex when allowed.
;   in:       rcx = token ptr, edx = token len, r8d = 1 when hex is
;             allowed (seed), else 0
;   out:      eax = 0 and rdx = value, or eax = PERR_NUM_SYNTAX /
;             PERR_NUM_OVERFLOW (rdx undefined)
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, flags
;   MXCSR:    untouched
; ------------------------------------------------------------------
pp_parse_u64:
        test    edx, edx
        jz      .syntax
        xor     r10, r10                ; accumulator
        test    r8d, r8d
        jz      .decimal
        cmp     edx, 2
        jbe     .decimal                ; "0x" alone is malformed anyway
        cmp     byte [rcx], '0'
        jne     .decimal
        cmp     byte [rcx+1], 'x'
        jne     .decimal
        add     rcx, 2
        sub     edx, 2
        cmp     edx, 16
        ja      .overflow               ; more than 16 hex digits
  .hex:
        movzx   eax, byte [rcx]
        sub     al, '0'
        cmp     al, 9
        jbe     .hex_ok
        movzx   eax, byte [rcx]
        or      al, 0x20                ; fold A-F to a-f
        sub     al, 'a'
        cmp     al, 5
        ja      .syntax
        add     al, 10
  .hex_ok:
        shl     r10, 4
        or      r10b, al
        inc     rcx
        dec     edx
        jnz     .hex
        jmp     .done
  .decimal:
        movzx   eax, byte [rcx]
        sub     al, '0'
        cmp     al, 9
        ja      .syntax
        mov     r9, 0x1999999999999999  ; (2^64-1)/10, pre-multiply guard
        cmp     r10, r9
        ja      .overflow
        lea     r10, [r10+r10*4]
        add     r10, r10                ; r10 *= 10
        movzx   r11d, al
        add     r10, r11
        jc      .overflow
        inc     rcx
        dec     edx
        jnz     .decimal
  .done:
        mov     rdx, r10
        xor     eax, eax
        ret
  .syntax:
        mov     eax, PERR_NUM_SYNTAX
        ret
  .overflow:
        mov     eax, PERR_NUM_OVERFLOW
        ret

; ------------------------------------------------------------------
; pp_parse_f32 — float token per the pinned grammar and conversion:
; [-]digits[.digits], 1..6 fraction digits when the dot is present,
; no exponent/inf/nan; value = mantissa-over-all-digits / 10^frac via
; one cvtsi2sd, one divsd, one cvtsd2ss.
;   in:       rcx = token ptr, edx = token len
;   out:      eax = 0 and xmm0 = f32 value, or eax = PERR_NUM_SYNTAX /
;             PERR_NUM_FRAC / PERR_NUM_OVERFLOW (xmm0 undefined)
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, xmm0, flags
;   MXCSR:    assumes the pinned seam MXCSR; accepted values carry
;             mantissa <= 10^8 < 2^53, so the convert and divide are
;             exact up to the one documented <= 1 ulp double rounding;
;             cap-passing larger mantissas (< 10^16) may convert
;             inexactly but are all out of every range and rejected
;             deterministically downstream
;   note:     the mantissa cap is value-based (reject only when the
;             accumulator reaches 10^15 before a multiply), so leading
;             zeros never reject
; ------------------------------------------------------------------
pp_parse_f32:
        test    edx, edx
        jz      .syntax
        xor     r11d, r11d              ; sign flag
        cmp     byte [rcx], '-'
        jne     @f
        mov     r11d, 1
        inc     rcx
        dec     edx
        jz      .syntax                 ; bare "-"
    @@:
        xor     r10, r10                ; mantissa
        xor     r8d, r8d                ; integer digits seen
  .int_loop:
        test    edx, edx
        jz      .convert
        movzx   eax, byte [rcx]
        cmp     al, '.'
        je      .dot
        sub     al, '0'
        cmp     al, 9
        ja      .syntax
        mov     r9, PP_MANT_CAP
        cmp     r10, r9
        jae     .overflow
        lea     r10, [r10+r10*4]
        add     r10, r10
        movzx   r9d, al
        add     r10, r9
        inc     r8d
        inc     rcx
        dec     edx
        jmp     .int_loop
  .dot:
        test    r8d, r8d
        jz      .syntax                 ; ".5" — no integer digits
        inc     rcx
        dec     edx
        jz      .syntax                 ; "5." — dot with no fraction
        xor     r9d, r9d                ; fraction digit count
  .frac_loop:
        test    edx, edx
        jz      .convert_frac
        movzx   eax, byte [rcx]
        sub     al, '0'
        cmp     al, 9
        ja      .syntax
        inc     r9d
        cmp     r9d, 6
        ja      .frac_err
        mov     rax, PP_MANT_CAP
        cmp     r10, rax
        jae     .overflow
        lea     r10, [r10+r10*4]
        add     r10, r10
        movzx   eax, byte [rcx]
        sub     al, '0'
        movzx   eax, al
        add     r10, rax
        inc     rcx
        dec     edx
        jmp     .frac_loop
  .convert:
        xor     r9d, r9d                ; no dot: zero fraction digits
        test    r8d, r8d
        jz      .syntax
  .convert_frac:
        test    r11d, r11d
        jz      @f
        neg     r10                     ; exact: |mantissa| < 2^63
    @@:
        cvtsi2sd xmm0, r10
        mov     eax, r9d
        divsd   xmm0, [pp_pow10+rax*8]
        cvtsd2ss xmm0, xmm0
        xor     eax, eax
        ret
  .syntax:
        mov     eax, PERR_NUM_SYNTAX
        ret
  .frac_err:
        mov     eax, PERR_NUM_FRAC
        ret
  .overflow:
        mov     eax, PERR_NUM_OVERFLOW
        ret

; ------------------------------------------------------------------
; pp_check_range — bounds-check an f32 against a table row.
;   in:       rcx = table row (KR_LO/KR_HI/KR_FLAGS used), xmm0 = value
;   out:      eax = 0 in range, else PERR_RANGE; xmm0 preserved
;   clobbers: rax, rcx, rdx, xmm1, flags
;   MXCSR:    assumes the pinned seam MXCSR (DAZ: a denormal value
;             compares as zero and falls to whichever side zero is on —
;             fail-closed for every open-at-zero bound)
;   note:     comiss unordered (NaN) rejects via PF
; ------------------------------------------------------------------
pp_check_range:
        mov     edx, [rcx+KR_FLAGS]
        ; Low bound. The comiss flags must be consumed BEFORE any `test`,
        ; which would clobber CF/ZF: branch on the comparison first, and only
        ; inspect the strict bit in the equal case (comiss result already used).
        movss   xmm1, [rcx+KR_LO]
        comiss  xmm0, xmm1
        jp      .bad                    ; unordered (NaN) rejects
        jb      .bad                    ; value < lo rejects on either bound
        jne     .hi                     ; value > lo: low bound satisfied
        test    edx, 1                  ; value == lo: reject only if lo-strict
        jnz     .bad
  .hi:
        movss   xmm1, [rcx+KR_HI]
        comiss  xmm0, xmm1
        jp      .bad
        ja      .bad                    ; value > hi rejects on either bound
        jne     .ok                     ; value < hi: high bound satisfied
        test    edx, 2                  ; value == hi: reject only if hi-strict
        jnz     .bad
  .ok:
        xor     eax, eax
        ret
  .bad:
        mov     eax, PERR_RANGE
        ret

; ------------------------------------------------------------------
; pp_validate_params — full fail-closed check of an in-memory struct
; (the layout_bytes entry validation; shares the parser's range table).
;   in:       rcx = SwarmParams*
;   out:      eax = 0 valid, 1 invalid
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, xmm0, xmm1, flags
;   MXCSR:    assumes the pinned seam MXCSR (denormal rmax compares as
;             0 under DAZ and is rejected by the strict lower bound)
; ------------------------------------------------------------------
pp_validate_params:
        mov     r10, rcx
        cmp     dword [r10+SP_VERSION], 1
        jne     .bad
        test    dword [r10+SP_FLAGS], not SP_FLAGS_VALID   ; any reserved bit set?
        jnz     .bad
        cmp     dword [r10+SP_FORCE_PATH], 3
        ja      .bad                    ; 0 auto / 1 AVX2 / 2 AVX-512 / 3 scalar
        xor     r11d, r11d              ; table row index
  .row:
        imul    eax, r11d, KR_SIZE
        lea     rcx, [kr_table+rax]
        cmp     dword [rcx+KR_KIND], KIND_U64
        je      .next                   ; seed: any u64
        mov     r9d, [rcx+KR_OFF]
        cmp     dword [rcx+KR_KIND], KIND_U32
        jne     .row_f32
        mov     edx, [r10+r9]
        cmp     edx, [rcx+KR_LO]
        jb      .bad
        cmp     edx, [rcx+KR_HI]
        ja      .bad
        jmp     .next
  .row_f32:
        movss   xmm0, [r10+r9]
        call    pp_check_range
        test    eax, eax
        jnz     .bad
  .next:
        inc     r11d
        cmp     r11d, 8
        jb      .row
        ; matrix: species_n x species_n entries in [-1, 1]
        mov     r11d, [r10+SP_SPECIES_N]
        imul    r11d, r11d              ; total entries to walk row-major
        xor     r9d, r9d                ; flat index i*species_n+j
        mov     r8d, [r10+SP_SPECIES_N]
  .mat:
        cmp     r9d, r11d
        jae     .ok
        mov     eax, r9d
        xor     edx, edx
        div     r8d                     ; eax = row, edx = col
        shl     eax, 3
        add     eax, edx
        movss   xmm0, [r10+SP_MATRIX+rax*4]
        push    r8
        push    r9
        push    r10
        push    r11
        mov     rcx, kr_matrix
        call    pp_check_range
        pop     r11
        pop     r10
        pop     r9
        pop     r8
        test    eax, eax
        jnz     .bad
        inc     r9d
        jmp     .mat
  .ok:
        xor     eax, eax
        ret
  .bad:
        mov     eax, 1
        ret

; --- const data (readable, non-writable: lives in the code section) -------

align 8
pp_pow10        dq 1.0, 10.0, 100.0, 1000.0, 10000.0, 100000.0, 1000000.0

kw_swarm        db 'swarm'
kw_matrix       db 'matrix'
kw_end          db 'end'
kn_n            db 'n'
kn_species      db 'species'
kn_seed         db 'seed'
kn_rmax         db 'rmax'
kn_beta         db 'beta'
kn_dt           db 'dt'
kn_friction     db 'friction'
kn_force        db 'force'

; The range table (docs/MASTERPLAN.md parameter table). Float bounds are
; f32 literals; comparisons happen post-conversion, so the boundary
; semantics equal the C# mirror's.
; Each row is KR_SIZE = 40 bytes: dq name (8) + 7 dd fields (28) + one dd pad
; (4) so the next row's dq pointer stays 8-aligned and the stride matches the
; imul in every table walk.
align 8
kr_table:
  dq kn_n
  dd 1, KIND_U32, SP_N,           0, 1,          1048576,    0,    0
  dq kn_species
  dd 7, KIND_U32, SP_SPECIES_N,   1, 1,          8,          0,    0
  dq kn_seed
  dd 4, KIND_U64, SP_SEED,        2, 0,          0,          0,    0
  dq kn_rmax
  dd 4, KIND_F32, SP_RMAX,        3, 0.0,        0.25,       1,    0
  dq kn_beta
  dd 4, KIND_F32, SP_BETA,        4, 0.05,       0.95,       0,    0
  dq kn_dt
  dd 2, KIND_F32, SP_DT,          5, 0.0,        0.1,        1,    0
  dq kn_friction
  dd 8, KIND_F32, SP_FRICTION,    6, 0.0,        1.0,        0,    0
  dq kn_force
  dd 5, KIND_F32, SP_FORCE_SCALE, 7, 0.0,        100.0,      1,    0

kr_matrix:
  dq 0
  dd 0, KIND_F32, 0,              0, -1.0,       1.0,        0,    0
