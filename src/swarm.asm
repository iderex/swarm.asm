; SPDX-License-Identifier: MIT
; swarm.exe - the live engine: window, DIB framebuffer, and the render loop
; that steps the simulation and rasters it each frame.
;
; The kernel sources are included straight in (decision 5: the exe and the test
; DLL share the same kernel), reached through the same MXCSR/nonvolatile seam
; the DLL uses. `-smoke` on the command line runs a fixed number of real frames
; and exits 0 - that flag is what CI runs, because the smoke gate needs a
; terminating process.
;
; `-capture` is the measurement instrument: the same paced live loop, timing the
; WORK WINDOW of every frame (step + plot + blit, never the pacing wait) and
; dumping the raw QPC deltas to swarm-frames.bin before exiting. A paced loop
; measured wall-to-wall reports the frame period by construction and proves
; nothing, which is why the wait is outside the window. Raw u64 samples rather
; than in-exe statistics: no sort, no formatting, no CRT-shaped number printing,
; and an artifact anyone can recompute from. It takes precedence over `-smoke`
; when both are given.
;
; `swarm.exe <preset.txt>` is the platform half of decision 10. The grammar and
; its two-phase commit are kernel code (parse.inc); reading a file is not, so
; the read lives here: the first command-line token that is not a flag is opened
; with CreateFileA, read under a byte cap, and handed to the parser. Every
; failure on that path ends the process with code 1 before a window exists -
; there is no partial apply and no fallback to the built-in preset, because a
; run that silently ignored the file it was given is worse than one that stops.
; FLAG_GRID is applied by the exe afterwards: grid mode is a platform choice,
; and the grammar deliberately has no key for it.

format PE64 GUI 6.0
entry start

include 'win64a.inc'
include 'kernel/abi.inc'

FRAME_W      = 1024                     ; framebuffer and client size, 1:1 blit
FRAME_H      = 1024
DIB_RGB_COLORS = 0                      ; not in the bundled equates
SMOKE_FRAMES = 60                       ; frames rendered under -smoke
CAPTURE_FRAMES = 3600                   ; work-window samples recorded under -capture
; swarm-frames.bin header: 'SWRMFRM1' (8) + qpc_freq (8) + count (8) + n (4)
; + flags (4) + seed (8). Every field is naturally aligned, so a reader can
; map the struct rather than parse it. The layout is checked below where the
; fields are laid out, so this constant cannot drift away from them.
CAP_HEADER_BYTES = 40
; Preset file limits. The grammar's worst case is one version line, eight key
; lines and an 8x8 matrix, which is under a kilobyte even with every number
; written at full width, so 8 KiB is room to spare rather than a guess at the
; format. It is a cap and not a buffer size: the read asks for one byte more,
; so a file at exactly the cap is accepted and the first byte past it is what
; rejects the file - a truncated preset must never be parsed as a whole one.
; PRESET_PATH_MAX is MAX_PATH including the terminator; a longer argument is
; rejected rather than truncated into a different file's name.
PRESET_MAX_BYTES = 8192
PRESET_PATH_MAX  = 260
PRESET_MSG_MAX   = 512                  ; formatted failure text, terminator incl.
WINDOW_STYLE = WS_OVERLAPPED+WS_CAPTION+WS_SYSMENU+WS_MINIMIZEBOX   ; fixed size
; Live count: the M1 acceptance count. It is reachable because the preset below
; sets FLAG_GRID - brute force at 8,192 is ~53 ms/pass on one core, the grid
; plus the pool is what closes the gap (docs/BENCHMARKS.md). The preset's rmax
; is part of the same statement: see the note above sim_params.
SIM_N        = 8192
TARGET_FPS   = 60
VK_R         = 'R'                      ; WM_KEYDOWN gives the uppercase VK code
VK_M         = 'M'
VK_H         = 'H'                      ; toggles the read-only matrix HUD
; Matrix HUD geometry, in client pixels. The grid is species_n x species_n
; cells of HUD_CELL pitch drawn HUD_GAP short, so the backdrop shows through
; as the separator and no second fill is needed per cell. At the species cap
; of 8 the whole panel is 8*24 + 2*6 = 204 px on a 1024 px client.
HUD_CELL     = 24                       ; cell pitch
HUD_GAP      = 2                        ; pitch not painted, i.e. the gridline
HUD_ORG      = 16                       ; top-left of the first cell
HUD_PAD      = 6                        ; backdrop margin around the grid
HUD_BACK     = 0x00202020               ; backdrop COLORREF (0x00BBGGRR)
; Per-cell matrix editing. A wheel notch, or EDIT_DRAG_PX of vertical drag,
; is one step of edit_step on the cell under the pointer; the sum is clamped
; back into the [-1, 1] the params contract declares for a matrix entry. The
; notches are counted as integers by WindowProc and turned into a float once,
; at the step boundary - so the message handler touches no simulation bit and
; no floating-point state at all.
EDIT_DRAG_PX = 8                        ; client pixels of drag per step
WHEEL_DELTA  = 120                      ; one wheel notch (not in the bundle)
MATRIX_CELLS = 64                       ; the 8x8 matrix block, stride 8
MEM_COMMIT     = 0x1000                 ; VirtualAlloc flags (kernel64 equates
MEM_RESERVE    = 0x2000                 ;   omit these; define them locally)
PAGE_READWRITE = 0x04
CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002   ; not in the bundle
TIMER_ALL_ACCESS = 0x1F0003            ; MODIFY_STATE + SYNCHRONIZE + more

section '.text' code readable executable

; ------------------------------------------------------------------
; start - process entry: init, message pump + render loop, clean exit.
;   in:       nothing (RSP = 8 mod 16 as delivered by the loader; the
;             sub rsp,8 below establishes the 16-alignment every invoke
;             in this routine relies on)
;   out:      does not return; process exit code 0 (1 on init failure)
;   clobbers: n/a (process ends here)
;   MXCSR:    pinned to 0x9FC0 at entry (decision 2, the exe main-thread pin) -
;             so every main-thread FP op (the matrix reroll) runs under the
;             same rounding/FTZ/DAZ policy the kernel does; the seam wrappers
;             re-pin around each kernel call, harmlessly idempotent
; ------------------------------------------------------------------
start:
        sub     rsp, 8
        ldmxcsr [mxcsr_pin]             ; decision 2: main-thread MXCSR = 0x9FC0

        invoke  GetModuleHandle, 0
        mov     [wc.hInstance], rax

        ; -smoke selects the terminating CI mode, -capture the measurement run.
        invoke  GetCommandLine
        mov     [cmd_line], rax
        mov     rcx, rax
        lea     rdx, [smoke_needle]
        call    scan_arg_flag
        mov     [smoke_mode], eax
        mov     rcx, [cmd_line]
        lea     rdx, [capture_needle]
        call    scan_arg_flag
        mov     [capture_mode], eax

        ; The sample buffer is committed in capture mode ONLY, so the shipped
        ; image carries no 28,800-byte block for a mode almost no run uses.
        ; Fail closed: a capture that cannot record must not reach the loop and
        ; exit 0, because a green run that produced no measurement would be
        ; read as a measurement.
        test    eax, eax
        jz      .args_done
        invoke  VirtualAlloc, 0, CAPTURE_FRAMES*8, MEM_COMMIT+MEM_RESERVE, PAGE_READWRITE
        test    rax, rax
        jz      .fail
        mov     [capture_buf], rax
  .args_done:

        ; A preset named on the command line replaces the built-in one before
        ; anything is sized from it. Deliberately ahead of the window and the
        ; arena: a rejected preset must not leave a window on screen, and the
        ; layout is computed from the params this may replace.
        mov     rcx, [cmd_line]
        call    scan_arg_path
        test    rax, rax
        jz      .preset_done
        mov     rcx, rax
        call    preset_apply            ; returns only on a clean parse
  .preset_done:

        invoke  LoadCursor, 0, IDC_ARROW
        mov     [wc.hCursor], rax
        invoke  RegisterClassEx, wc
        test    ax, ax                  ; ATOM return: only the low WORD
        jz      .fail                   ; of rax is defined

        ; Window sized so the CLIENT area is exactly the framebuffer.
        invoke  AdjustWindowRect, rect, WINDOW_STYLE, FALSE
        mov     eax, [rect.right]
        sub     eax, [rect.left]
        mov     [win_w], eax
        mov     eax, [rect.bottom]
        sub     eax, [rect.top]
        mov     [win_h], eax
        invoke  CreateWindowEx, 0, _class, _title, WINDOW_STYLE+WS_VISIBLE, \
                CW_USEDEFAULT, CW_USEDEFAULT, [win_w], [win_h], NULL, NULL, [wc.hInstance], NULL
        test    rax, rax
        jz      .fail
        mov     [hwnd], rax

        ; One 32-bit top-down DIB section is the whole render target
        ; (docs/MASTERPLAN.md, decision 9). Win32 only guarantees DWORD
        ; alignment for the pixel buffer - the plot pass must check (or
        ; not assume) anything wider.
        invoke  CreateDIBSection, 0, bmi, DIB_RGB_COLORS, pixels, 0, 0
        test    rax, rax
        jz      .fail
        mov     [hdib], rax

        ; A broken render path must never limp into a green smoke run:
        ; every GDI setup failure exits through .fail.
        invoke  GetDC, [hwnd]           ; CS_OWNDC: the DC is private and held
        test    rax, rax
        jz      .fail
        mov     [wnd_dc], rax
        invoke  CreateCompatibleDC, [wnd_dc]
        test    rax, rax
        jz      .fail
        mov     [mem_dc], rax
        invoke  SelectObject, [mem_dc], [hdib]
        test    rax, rax
        jz      .fail
        mov     [old_bmp], rax

        ; --- create and seed the simulation arena (fail closed) ----------
        ; The seam wrappers pin MXCSR (decision 2) and land each core at the
        ; 0-mod-32 kernel entry, exactly as the DLL exports do.
        lea     rcx, [sim_params]
        call    sim_layout              ; rax = arena bytes for these params
        mov     [arena_bytes], rax
        invoke  VirtualAlloc, 0, [arena_bytes], MEM_COMMIT+MEM_RESERVE, PAGE_READWRITE
        test    rax, rax
        jz      .fail                   ; page-aligned, so >= 64-aligned
        mov     [arena], rax
        mov     rcx, rax
        mov     rdx, [arena_bytes]
        lea     r8, [sim_params]
        call    sim_init
        test    eax, eax
        jnz     .fail                   ; invalid params / short arena / no path

        ; --- create the M3 worker pool once (decision 6): main is worker 0,
        ; T-1 threads are spawned parked and woken once per frame. 0 = auto
        ; (physical cores). Fail-closed: a machine that cannot spawn the pool
        ; exits rather than limping (eax < 1 on failure).
        xor     ecx, ecx
        call    pool_init
        cmp     eax, 1
        jl      .fail

        ; --- frame pacing: a high-resolution waitable timer to a QPC deadline
        ; (docs/MASTERPLAN.md, decision 11). One swarm_step per rendered frame,
        ; no accumulator, no catch-up: if the machine falls behind the animation
        ; slows and the state sequence is unchanged.
        invoke  QueryPerformanceFrequency, qpc_freq
        mov     rax, [qpc_freq]
        xor     edx, edx
        mov     ecx, TARGET_FPS
        div     rcx                     ; ticks per frame = freq / 60
        mov     [ticks_per_frame], rax
        ; The high-resolution timer needs Windows 10 1803+; on anything older
        ; the call returns NULL and the exe exits 1 (fail-closed, no window)
        ; rather than pacing coarsely. Acceptable for the Win11 target.
        invoke  CreateWaitableTimerExW, 0, 0, \
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS
        test    rax, rax
        jz      .fail                   ; the paced loop requires the hi-res timer
        mov     [htimer], rax
        invoke  QueryPerformanceCounter, qpc_now
        mov     rax, [qpc_now]
        add     rax, [ticks_per_frame]
        mov     [qpc_deadline], rax

  .pump:
        invoke  PeekMessage, msg, 0, 0, 0, PM_REMOVE
        test    eax, eax
        jz      .render
        cmp     [msg.message], WM_QUIT
        je      .quit
        invoke  TranslateMessage, msg
        invoke  DispatchMessage, msg
        jmp     .pump

  .render:
        ; Apply pending keyboard and mouse edits at the step boundary
        ; (decision 11: edits commit between steps). WindowProc only sets
        ; these flags. The per-cell matrix edits go first, so a cell the
        ; pointer was over is changed against the matrix that was on screen
        ; when the wheel turned, never against one a reroll replaced in the
        ; same frame.
        cmp     dword [edit_req], 0
        je      .chk_reroll
        mov     dword [edit_req], 0
        call    ui_apply_matrix_edits
  .chk_reroll:
        cmp     dword [reroll_req], 0
        je      .chk_reseed
        mov     dword [reroll_req], 0
        call    ui_reroll_matrix        ; new attraction values in [-1, 1]
        call    ui_reinit               ; new matrix + fresh positions
        jmp     .step
  .chk_reseed:
        cmp     dword [reseed_req], 0
        je      .step
        mov     dword [reseed_req], 0
        call    ui_reseed               ; new seed
        call    ui_reinit               ; fresh positions, same matrix

  .step:
        ; The work window opens here and closes just after BitBlt. Only the
        ; capture run pays the two QPC reads; a normal live frame is unchanged.
        cmp     dword [capture_mode], 0
        je      .work
        invoke  QueryPerformanceCounter, cap_t0
  .work:
        cmp     dword [paused], 0
        jne     .plot                   ; paused: skip the step, keep drawing
        mov     rcx, [arena]            ; advance the simulation one step across
        mov     edx, 1                  ;   the worker pool (build serial, pass
        call    pool_step               ;   parallel) - bit-identical to sim_step
  .plot:
        mov     rcx, [arena]            ; raster the state into the DIB
        mov     rdx, [pixels]           ; (plot_core clears then plots)
        mov     r8d, FRAME_W
        mov     r9d, FRAME_H
        call    sim_plot
        ; BitBlt's BOOL is deliberately unchecked: the smoke gate covers
        ; process viability and setup, not mid-run device loss.
        invoke  BitBlt, [wnd_dc], 0, 0, FRAME_W, FRAME_H, [mem_dc], 0, 0, SRCCOPY

        inc     [frame_count]
        cmp     dword [capture_mode], 0
        je      .chk_smoke              ; -capture takes precedence over -smoke
        call    capture_frame           ; closes the work window; returns 1 in
        test    eax, eax                ;   eax once the dump has been written
        jz      .hud
        invoke  DestroyWindow, [hwnd]   ; the same clean shutdown -smoke takes
        jmp     .pace
  .chk_smoke:
        cmp     [smoke_mode], 0
        je      .hud
        cmp     [frame_count], SMOKE_FRAMES
        jb      .hud
        invoke  DestroyWindow, [hwnd]   ; -> WM_DESTROY -> PostQuitMessage(0)
        jmp     .pace

  .hud:
        ; Deliberately AFTER capture_frame closed the work window: the HUD is
        ; an overlay on the already-blitted frame, so a capture run measures
        ; step + plot + blit whether the HUD is up or not. The two shutdown
        ; paths above skip it because their window is already destroyed.
        call    hud_draw                ; no-op unless the HUD is toggled on

  .pace:
        call    frame_pace              ; wait out the frame to the 60 fps deadline
        jmp     .pump

  .quit:
        call    pool_shutdown           ; join + close the worker threads
        invoke  CloseHandle, [htimer]
        invoke  SelectObject, [mem_dc], [old_bmp]
        invoke  DeleteDC, [mem_dc]
        invoke  ReleaseDC, [hwnd], [wnd_dc]
        invoke  DeleteObject, [hdib]
        invoke  ExitProcess, [msg.wParam]   ; 0, from PostQuitMessage

  .fail:
        invoke  ExitProcess, 1          ; fail closed: no window, no half-run

; ------------------------------------------------------------------
; scan_arg_flag - detect a needle as a whole argument token.
;   in:       rcx = zero-terminated ANSI command line in GetCommandLine
;             form: the program token comes first, possibly quoted
;             rdx = zero-terminated ANSI needle, e.g. "-smoke"
;   out:      eax = 1 when a whitespace-delimited argument equals the
;             needle exactly, else 0
;   clobbers: rax, rcx, r8, r9, flags (rdx is read, not written, so the
;             same needle can be scanned for twice without reloading it)
;   MXCSR:    untouched
;   note:     the program token is skipped so a needle inside the exe path
;             never triggers; never reads past the terminator (a match
;             window can only extend over non-NUL needle bytes)
; ------------------------------------------------------------------
scan_arg_flag:
        cmp     byte [rcx], '"'
        jne     .skip_program
  .skip_quoted:                         ; quoted program token: to the
        inc     rcx                     ; closing quote
        movzx   eax, byte [rcx]
        test    al, al
        jz      .absent
        cmp     al, '"'
        jne     .skip_quoted
        inc     rcx
        jmp     .next_arg
  .skip_program:                        ; bare program token: to the
        movzx   eax, byte [rcx]         ; first blank
        test    al, al
        jz      .absent
        cmp     al, ' '
        je      .next_arg
        cmp     al, 9
        je      .next_arg
        inc     rcx
        jmp     .skip_program
  .next_arg:
        movzx   eax, byte [rcx]
        test    al, al
        jz      .absent
        cmp     al, ' '
        je      .blank
        cmp     al, 9
        je      .blank
        xor     r9d, r9d
  .compare:
        movzx   eax, byte [rdx+r9]
        movzx   r8d, byte [rcx+r9]
        test    al, al
        jz      .needle_end
        cmp     al, r8b
        jne     .skip_token
        inc     r9d
        jmp     .compare
  .needle_end:                          ; the token must end here too -
        test    r8b, r8b                ; "-smokeless" is not "-smoke"
        jz      .present
        cmp     r8b, ' '
        je      .present
        cmp     r8b, 9
        je      .present
  .skip_token:
        movzx   eax, byte [rcx]
        test    al, al
        jz      .absent
        cmp     al, ' '
        je      .next_arg
        cmp     al, 9
        je      .next_arg
        inc     rcx
        jmp     .skip_token
  .blank:
        inc     rcx
        jmp     .next_arg
  .present:
        mov     eax, 1
        ret
  .absent:
        xor     eax, eax
        ret

; ------------------------------------------------------------------
; scan_arg_path - find the first argument that is not a flag: the preset path.
;   in:       rcx = zero-terminated ANSI command line in GetCommandLine
;             form: the program token comes first, possibly quoted
;   out:      rax = first byte of the token and edx = its length in bytes,
;             or rax = 0 and edx = 0 when there is no such argument
;   clobbers: rax, rcx, rdx, r8, flags
;   MXCSR:    untouched
;   note:     a token starting with '-' is a flag and is skipped, so -smoke
;             and -capture never read as a filename and a future flag needs
;             no change here. The cost is that a path beginning with '-' is
;             unreachable, which is the same trade every argv taker makes and
;             is written into the README rather than worked around.
;   note:     a quoted token yields the bytes between the quotes, so a path
;             with spaces arrives whole. An unterminated quote yields the rest
;             of the line, which then fails to open - fail-closed, and one
;             branch rather than a second error path
; ------------------------------------------------------------------
scan_arg_path:
        cmp     byte [rcx], '"'
        jne     .skip_program
  .skip_quoted:                         ; quoted program token: to the
        inc     rcx                     ; closing quote
        movzx   eax, byte [rcx]
        test    al, al
        jz      .absent
        cmp     al, '"'
        jne     .skip_quoted
        inc     rcx
        jmp     .next_arg
  .skip_program:                        ; bare program token: to the
        movzx   eax, byte [rcx]         ; first blank
        test    al, al
        jz      .absent
        cmp     al, ' '
        je      .next_arg
        cmp     al, 9
        je      .next_arg
        inc     rcx
        jmp     .skip_program
  .next_arg:
        movzx   eax, byte [rcx]
        test    al, al
        jz      .absent
        cmp     al, ' '
        je      .blank
        cmp     al, 9
        je      .blank
        cmp     al, '-'
        je      .skip_token             ; a flag, never a filename
        cmp     al, '"'
        je      .quoted
        mov     r8, rcx                 ; bare token: runs to the next blank
  .bare:
        movzx   eax, byte [rcx]
        test    al, al
        jz      .found
        cmp     al, ' '
        je      .found
        cmp     al, 9
        je      .found
        inc     rcx
        jmp     .bare
  .quoted:
        inc     rcx                     ; past the opening quote
        mov     r8, rcx
  .in_quote:
        movzx   eax, byte [rcx]
        test    al, al
        jz      .found
        cmp     al, '"'
        je      .found
        inc     rcx
        jmp     .in_quote
  .found:
        sub     rcx, r8
        mov     edx, ecx                ; a command line is far under 4 GiB
        mov     rax, r8
        ret
  .skip_token:
        movzx   eax, byte [rcx]
        test    al, al
        jz      .absent
        cmp     al, ' '
        je      .next_arg
        cmp     al, 9
        je      .next_arg
        inc     rcx
        jmp     .skip_token
  .blank:
        inc     rcx
        jmp     .next_arg
  .absent:
        xor     eax, eax
        xor     edx, edx
        ret

; ------------------------------------------------------------------
; preset_apply - read the named preset file and adopt it, or exit 1.
;   in:       rcx = first byte of the path token (not zero-terminated),
;             edx = its length in bytes; rsp = 8 mod 16 (call from start)
;   out:      returns only when the file parsed clean, with [sim_params]
;             replaced by the parsed preset and FLAG_GRID set on it. Every
;             failure reports through preset_fail and ends the process with
;             code 1, so no caller needs an error branch
;   clobbers: caller-saved, flags (sim_parse is seam-wrapped and preserves
;             every nonvolatile)
;   MXCSR:    pinned 0x9FC0 by start; sim_parse re-pins across the core
;   note:     [sim_params] is the parser's own output buffer, which is safe
;             because parse_preset_core is a two-phase commit: it writes the
;             output at exactly one place and only after the whole file has
;             validated. A rejected preset therefore leaves the built-in one
;             byte-untouched, and the exe exits rather than running it
; ------------------------------------------------------------------
preset_apply:
        sub     rsp, 8                  ; entry rsp = 8 mod 16 -> 0 for invoke

        ; Copy the token out of the command line so it is zero-terminated for
        ; CreateFileA. Over-long is truncated INTO THE MESSAGE ONLY: the length
        ; is judged below, on the original, so no truncated name is ever opened.
        mov     r11, rcx                ; the token bytes, inside the command line
        mov     r8d, edx                ; length as given, judged after the copy
        mov     r9d, edx                ; length actually copied
        cmp     r9d, PRESET_PATH_MAX-1
        jbe     .copy_len
        mov     r9d, PRESET_PATH_MAX-1
  .copy_len:
        lea     r10, [preset_path]
        xor     ecx, ecx
  .copy:
        cmp     ecx, r9d
        jae     .copied
        movzx   eax, byte [r11+rcx]
        mov     [r10+rcx], al
        inc     ecx
        jmp     .copy
  .copied:
        mov     byte [r10+rcx], 0
        cmp     r8d, PRESET_PATH_MAX-1
        jbe     .open
        invoke  wsprintfA, why_detail, fmt_path_long, PRESET_PATH_MAX-1
        lea     rcx, [why_detail]
        call    preset_fail

  .open:
        invoke  CreateFileA, preset_path, GENERIC_READ, FILE_SHARE_READ, 0, \
                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, 0
        cmp     rax, INVALID_HANDLE_VALUE
        jne     .opened
        lea     rcx, [why_open]
        call    preset_fail
  .opened:
        mov     [preset_file], rax

        ; One byte more than the cap is asked for, so an oversized file is
        ; detected by what came back rather than by a separate size query that
        ; could disagree with the read.
        invoke  VirtualAlloc, 0, PRESET_MAX_BYTES+1, MEM_COMMIT+MEM_RESERVE, PAGE_READWRITE
        test    rax, rax
        jnz     .alloced
        lea     rcx, [why_alloc]
        call    preset_fail
  .alloced:
        mov     [preset_buf], rax
        mov     dword [preset_len], 0
  .read:
        mov     eax, PRESET_MAX_BYTES+1
        sub     eax, [preset_len]
        jz      .read_done              ; the buffer is full: over the cap
        mov     [preset_want], eax
        mov     rax, [preset_buf]
        mov     ecx, [preset_len]
        add     rax, rcx
        mov     [preset_at], rax
        invoke  ReadFile, [preset_file], [preset_at], [preset_want], preset_read, 0
        test    eax, eax
        jnz     .read_ok
        invoke  CloseHandle, [preset_file]
        lea     rcx, [why_read]
        call    preset_fail
  .read_ok:
        mov     eax, [preset_read]
        test    eax, eax
        jz      .read_done              ; end of file
        add     [preset_len], eax
        jmp     .read
  .read_done:
        invoke  CloseHandle, [preset_file]
        cmp     dword [preset_len], PRESET_MAX_BYTES
        jbe     .parse
        invoke  wsprintfA, why_detail, fmt_too_big, PRESET_MAX_BYTES
        lea     rcx, [why_detail]
        call    preset_fail

  .parse:
        mov     rcx, [preset_buf]
        mov     edx, [preset_len]
        lea     r8, [sim_params]
        call    sim_parse
        test    eax, eax
        jz      .parsed
        ; Packed error: bit 31 set, PERR_* in bits 20..30, 1-based line in the
        ; low 20. Both halves are reported, plus the raw return, so a reader can
        ; check the split rather than trust it.
        mov     r10d, eax
        mov     ecx, eax
        and     ecx, 0xFFFFF
        mov     [preset_err_line], ecx
        mov     ecx, r10d
        shr     ecx, 20
        and     ecx, 0x7FF
        mov     [preset_err_code], ecx
        mov     [preset_err_raw], r10d
        invoke  wsprintfA, why_detail, fmt_parse, [preset_err_code], \
                [preset_err_line], [preset_err_raw]
        lea     rcx, [why_detail]
        call    preset_fail
  .parsed:
        ; Grid mode is the exe's decision, not the file's: decision 10 gives the
        ; grammar no flags key, and the parser leaves the field zero.
        or      dword [sim_params+SP_FLAGS], FLAG_GRID
        invoke  VirtualFree, [preset_buf], 0, MEM_RELEASE
        add     rsp, 8
        ret

; ------------------------------------------------------------------
; preset_fail - say why the preset was refused, then exit 1. Never returns.
;   in:       rcx = zero-terminated ANSI reason; [preset_path] holds the name
;             as it was given; rsp = 8 mod 16 (reached by call)
;   out:      does not return; process exit code 1
;   clobbers: n/a (process ends here)
;   MXCSR:    untouched (integer and imports only)
;   note:     the modal box is skipped under -smoke and -capture. Both are
;             unattended modes by definition, and a dialog nobody can dismiss
;             turns a fail-closed exit into a hang, which is the worse failure.
;             The exit code carries the same information to a machine either
;             way, so nothing is lost that a script was reading
; ------------------------------------------------------------------
preset_fail:
        sub     rsp, 8                  ; entry rsp = 8 mod 16 -> 0 for invoke
        mov     [preset_reason], rcx
        invoke  wsprintfA, msg_buf, fmt_reason, preset_path, [preset_reason]
        cmp     dword [smoke_mode], 0
        jne     .quiet
        cmp     dword [capture_mode], 0
        jne     .quiet
        invoke  MessageBoxA, 0, msg_buf, _title, MB_ICONERROR+MB_SETFOREGROUND
  .quiet:
        invoke  ExitProcess, 1

; ------------------------------------------------------------------
; WindowProc - window procedure (Win64 ABI callee, callback seam).
;   in:       rcx hwnd, edx message, r8 wparam, r9 lparam
;   out:      rax = message result
;   clobbers: volatile registers only
;   MXCSR:    untouched
; ------------------------------------------------------------------
; The arg names deliberately differ from the .data globals ([hwnd] et
; al.) - a proc arg shadows the global inside the body, and the named
; slots hold caller stack garbage (invoke never homes the registers).
proc WindowProc wnd, wmsg, wp, lp
        cmp     edx, WM_DESTROY
        je      .destroy
        cmp     edx, WM_KEYDOWN
        je      .key
        cmp     edx, WM_MOUSEWHEEL
        je      .wheel
        cmp     edx, WM_LBUTTONDOWN
        je      .lbdown
        cmp     edx, WM_MOUSEMOVE
        je      .lbmove
        cmp     edx, WM_LBUTTONUP
        je      .lbup
  .defwndproc:
        invoke  DefWindowProc, rcx, rdx, r8, r9
        jmp     .finish
  .key:
        test    r9d, 0x40000000         ; lParam bit 30 = key was already down:
        jnz     .defwndproc             ;   ignore autorepeat, one action / press
        cmp     r8d, VK_ESCAPE
        je      .k_quit
        cmp     r8d, VK_SPACE
        je      .k_pause
        cmp     r8d, VK_R
        je      .k_reseed
        cmp     r8d, VK_M
        je      .k_reroll
        cmp     r8d, VK_H
        je      .k_hud
        jmp     .defwndproc
  .k_quit:
        invoke  DestroyWindow, rcx
        xor     eax, eax
        jmp     .finish
  .k_pause:                             ; the render loop reads these flags at
        xor     dword [paused], 1       ; the next step boundary (decision 11)
        xor     eax, eax
        jmp     .finish
  .k_reseed:
        mov     dword [reseed_req], 1
        xor     eax, eax
        jmp     .finish
  .k_reroll:
        mov     dword [reroll_req], 1
        xor     eax, eax
        jmp     .finish
  .k_hud:                               ; not a step-boundary edit: the HUD only
        xor     dword [hud_on], 1       ; reads, so it touches no simulation bit
        xor     eax, eax
        jmp     .finish

        ; --- per-cell matrix editing (decision 9) --------------------------
        ; Every arm below counts whole steps into edit_notch and raises
        ; edit_req. None of them writes a matrix byte, reads the arena or
        ; executes a floating-point instruction: the edit becomes a number
        ; in the matrix at the step boundary, in the render loop, and only
        ; there. That is the whole determinism argument, and it is one
        ; sentence because the handler is one side of it.
  .wheel:                               ; wheel: lParam is in SCREEN pixels
        cmp     dword [hud_on], 0
        je      .defwndproc             ; no grid drawn, nothing to point at
        mov     eax, r9d
        movsx   eax, ax                 ; LOWORD, signed: screen x
        mov     [edit_pt], eax
        mov     eax, r9d
        sar     eax, 16                 ; HIWORD, signed: screen y
        mov     [edit_pt+4], eax
        mov     eax, r8d
        sar     eax, 16                 ; HIWORD(wParam): the wheel delta
        cdq
        mov     r10d, WHEEL_DELTA
        idiv    r10d                    ; whole notches; a partial one is
        test    eax, eax                ;   dropped rather than accumulated
        jz      .mouse_done
        mov     [edit_steps], eax       ; staged: the invoke clobbers eax
        invoke  ScreenToClient, [hwnd], edit_pt
        test    eax, eax
        jz      .mouse_done             ; conversion failed: no edit, no guess
        mov     ecx, [edit_pt]
        mov     edx, [edit_pt+4]
        call    hud_hit_test
        test    eax, eax
        js      .mouse_done
        mov     ecx, [edit_steps]
        call    ui_queue_cell_steps
        jmp     .mouse_done
  .lbdown:                              ; drag: lParam is already client-relative
        cmp     dword [hud_on], 0
        je      .defwndproc
        mov     eax, r9d
        movsx   ecx, ax                 ; client x
        mov     eax, r9d
        sar     eax, 16                 ; client y
        mov     edx, eax
        mov     r11d, eax               ; kept for the anchor across the call
        call    hud_hit_test
        test    eax, eax
        js      .mouse_done             ; a press outside the grid starts nothing
        mov     [edit_cell], eax
        mov     [edit_anchor], r11d
        jmp     .mouse_done
  .lbmove:
        cmp     dword [edit_cell], 0
        jl      .defwndproc             ; no drag in flight: two instructions
        test    r8d, MK_LBUTTON         ; released off-window, so the button-up
        jz      .lbup                   ;   never arrived: end the drag here
        mov     eax, r9d
        sar     eax, 16                 ; client y
        mov     r10d, [edit_anchor]
        sub     r10d, eax               ; upward (smaller y) is a positive step
        mov     eax, r10d
        cdq
        mov     r10d, EDIT_DRAG_PX
        idiv    r10d                    ; whole steps only
        test    eax, eax
        jz      .mouse_done
        imul    r10d, eax               ; consume just the pixels that became
        sub     [edit_anchor], r10d     ;   steps, so the remainder is not lost
        mov     ecx, eax
        mov     eax, [edit_cell]
        call    ui_queue_cell_steps
        jmp     .mouse_done
  .lbup:
        mov     dword [edit_cell], -1
  .mouse_done:
        xor     eax, eax
        jmp     .finish
  .destroy:
        invoke  PostQuitMessage, 0
        xor     eax, eax
  .finish:
        ret
endp

; --- the simulation kernel, shared with the test DLL (decision 5) ---
; Pure computation, no imports; the exe stays within kernel32/user32/gdi32.
; One slice is deliberately absent here: kernel/state.inc holds the id-ordered
; copy-out that backs the DLL's swarm_read_state export, and the exe has no
; equivalent. FASM emits every assembled label, so including it would put a
; routine nothing calls into the shipped .text (#80).
include 'platform/seam.inc'
include 'kernel/rng.inc'
include 'kernel/parse.inc'
include 'kernel/layout.inc'
include 'kernel/cpuid.inc'
include 'kernel/init.inc'
include 'kernel/step.inc'
include 'kernel/grid.inc'
include 'kernel/plot.inc'

; The M3 worker pool (platform layer). Included after the kernel so pass_core /
; build_core are defined; the workers cross the same thread-entry seam the
; exports do (pool_pass) and call only the pure per-range pass. All its imports
; are kernel32 (CreateThread / CreateEventW / event waits), so the exe stays
; within kernel32/user32/gdi32.
include 'platform/pool.inc'

; Seam wrappers: each pins MXCSR to 0x9FC0, saves the Win64 nonvolatiles, and
; lands the kernel core at rsp = 0 mod 32 - the same contract the DLL exports
; carry, so the exe drives the identical, gate-verified code paths. Each wrapper
; therefore exposes the uniform seam contract in its header: it clobbers only
; the Win64 volatiles; the seam saves and restores every nonvolatile and MXCSR.

; ------------------------------------------------------------------
; sim_layout - seam wrapper over layout_bytes_core.
;   in:       rcx = SwarmParams*
;   out:      rax = arena bytes (multiple of 64), or 0 when params invalid
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap sim_layout, layout_bytes_core
; ------------------------------------------------------------------
; sim_init - seam wrapper over init_core.
;   in:       rcx arena, rdx arena_bytes, r8 SwarmParams*
;   out:      eax = 0 on success, else IERR_*
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap sim_init, init_core
; ------------------------------------------------------------------
; sim_step - seam wrapper over step_core.
;   in:       rcx arena, edx n_steps
;   out:      nothing (void); the arena is advanced n_steps
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap sim_step, step_core
; ------------------------------------------------------------------
; sim_plot - seam wrapper over plot_core.
;   in:       rcx arena, rdx pixels, r8d w, r9d h
;   out:      nothing (void); the framebuffer at rdx is written
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap sim_plot, plot_core
; ------------------------------------------------------------------
; sim_parse - seam wrapper over parse_preset_core.
;   in:       rcx text, edx len, r8 SwarmParams* out
;   out:      eax = 0 and *out written, else the packed negative parse error
;             with *out byte-untouched
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile, which the core needs here because it drives rsi/rdi
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap sim_parse, parse_preset_core

; ------------------------------------------------------------------
; ui_reseed - draw a fresh world seed from the UI RNG stream.
;   in/out:   mutates [ui_rng] and [sim_params+SP_SEED]
;   clobbers: rax, r9, r10, flags
;   MXCSR:    untouched (integer only)
; ------------------------------------------------------------------
ui_reseed:
        mov     r10, [ui_rng]
        rng_next r10, rax, r9           ; r10 advances, rax = new draw
        mov     [ui_rng], r10
        mov     [sim_params+SP_SEED], rax
        ret

; ------------------------------------------------------------------
; ui_reroll_matrix - refill the species_n x species_n attraction block with
; fresh values a = 2*u01 - 1 in [-1, 1) (decision 8), from the UI RNG stream.
;   in/out:   mutates [ui_rng] and the matrix in [sim_params]
;   clobbers: rax, rcx, rdx, r8, r9, r10, r11, xmm0, flags
;   MXCSR:    pinned 0x9FC0 (set at start); round-nearest, no denormals arise,
;             so the stored f32 is deterministic
; ------------------------------------------------------------------
ui_reroll_matrix:
        mov     r10, [ui_rng]
        mov     r8d, [sim_params+SP_SPECIES_N]
        lea     r11, [sim_params+SP_MATRIX]
        xor     ecx, ecx                ; i (row)
  .row:
        cmp     ecx, r8d
        jae     .done
        xor     edx, edx                ; j (column)
  .col:
        cmp     edx, r8d
        jae     .next
        rng_next r10, rax, r9           ; rax = draw
        shr     rax, 40                 ; top 24 bits -> [0, 2^24)
        cvtsi2ss xmm0, rax
        mulss   xmm0, [inv_2p24]        ; u01 in [0, 1)
        addss   xmm0, xmm0              ; 2*u01
        subss   xmm0, [f_one]           ; -> [-1, 1)
        mov     eax, ecx
        shl     eax, 3                  ; matrix stride is 8 f32 (i*8 + j)
        add     eax, edx
        movss   [r11+rax*4], xmm0
        inc     edx
        jmp     .col
  .next:
        inc     ecx
        jmp     .row
  .done:
        mov     [ui_rng], r10
        ret

; ------------------------------------------------------------------
; hud_hit_test - the matrix cell under a client-area point.
;   in:       ecx = client x, edx = client y (signed; either may be negative)
;   out:      eax = i*8 + j, the cell's index in the 8-wide matrix block, or
;             -1 when the point misses: outside the grid, past species_n in
;             either axis, or inside the HUD_GAP the cell is drawn short of
;   clobbers: rax, rcx, rdx, r8, r9, r10, flags
;   MXCSR:    untouched (integer only)
;   note:     the arithmetic is hud_draw's geometry read backwards, so a hit
;             is a hit on a cell that is actually painted. Whether the HUD is
;             up at all is the caller's check, not this one's
; ------------------------------------------------------------------
hud_hit_test:
        mov     r8d, [sim_params+SP_SPECIES_N]
        sub     ecx, HUD_ORG            ; client -> grid-relative
        js      .miss                   ; left of the first cell
        sub     edx, HUD_ORG
        js      .miss                   ; above the first row
        mov     r9d, edx                ; y, parked: div needs edx
        mov     eax, ecx
        xor     edx, edx
        mov     r10d, HUD_CELL
        div     r10d                    ; eax = column, edx = pixel within it
        cmp     eax, r8d
        jae     .miss                   ; past the last species column
        cmp     edx, HUD_CELL-HUD_GAP
        jae     .miss                   ; the unpainted gap, i.e. a gridline
        mov     ecx, eax                ; column
        mov     eax, r9d
        xor     edx, edx
        div     r10d                    ; eax = row, edx = pixel within it
        cmp     eax, r8d
        jae     .miss
        cmp     edx, HUD_CELL-HUD_GAP
        jae     .miss
        shl     eax, 3                  ; the matrix stride is 8 f32 (i*8 + j)
        add     eax, ecx
        ret
  .miss:
        mov     eax, -1
        ret

; ------------------------------------------------------------------
; ui_queue_cell_steps - record whole steps against one matrix cell.
;   in:       eax = cell index in [0, MATRIX_CELLS), ecx = signed step count
;   out:      the count is added to that cell's pending total and edit_req
;             is raised; no matrix byte is written here
;   clobbers: r9, flags
;   MXCSR:    untouched (integer only)
;   note:     the counts accumulate rather than replace, so several notches
;             inside one frame all land, and they land together
; ------------------------------------------------------------------
ui_queue_cell_steps:
        lea     r9, [edit_notch]
        add     [r9+rax*4], ecx
        mov     dword [edit_req], 1
        ret

; ------------------------------------------------------------------
; ui_apply_matrix_edits - fold the pending steps into the matrix.
;   in:       [edit_notch], [arena]; called only from the render loop's
;             step-boundary chain
;   out:      every non-zero count becomes count * edit_step added to its
;             cell, clamped into the [-1, 1] the params contract declares,
;             and the count is reset to 0
;   clobbers: rax, rcx, r9, r10, r11, xmm0, flags
;   MXCSR:    pinned 0x9FC0 (set at start); every input is a normal in
;             [-1, 1] and the step is a normal, so no denormal arises
;   note:     BOTH copies of the matrix are written - the params block, which
;             the HUD paints and a reinit re-seeds from, and the validated
;             copy inside the arena header (abi.inc AH_PARAMS), which is what
;             the force pass actually reads. Writing one and not the other
;             would either show an edit that never reached the simulation or
;             run one that never appeared on screen
;   note:     the arena copy is written between steps, never during one. That
;             is what makes an edited session a replay of its edit log: the
;             state after any frame is a function of the seed and of which
;             steps the edits landed between, and of nothing else
; ------------------------------------------------------------------
ui_apply_matrix_edits:
        lea     r9, [edit_notch]
        lea     r10, [sim_params+SP_MATRIX]
        mov     r11, [arena]
        add     r11, AH_PARAMS+SP_MATRIX
        xor     ecx, ecx
  .cell:
        mov     eax, [r9+rcx*4]
        test    eax, eax
        jz      .next
        mov     dword [r9+rcx*4], 0
        cvtsi2ss xmm0, eax
        mulss   xmm0, [edit_step]
        addss   xmm0, [r10+rcx*4]
        maxss   xmm0, [edit_neg_one]    ; clamp before either store, so the
        minss   xmm0, [f_one]           ;   two copies cannot disagree
        movss   [r10+rcx*4], xmm0
        movss   [r11+rcx*4], xmm0
  .next:
        inc     ecx
        cmp     ecx, MATRIX_CELLS
        jb      .cell
        ret

; ------------------------------------------------------------------
; ui_reinit - re-seed the existing arena from the (edited) params.
;   in:       [arena], [arena_bytes], [sim_params] (n/species_n unchanged, so
;             the layout is identical and the buffer is reused)
;   out:      the arena is fully re-initialized; eax = 0 by construction
;             (the params stay valid, so init_core cannot reject them)
;   clobbers: caller-saved (sim_init is seam-wrapped and self-aligns)
;   MXCSR:    re-pinned inside the seam
; ------------------------------------------------------------------
ui_reinit:
        mov     rcx, [arena]
        mov     rdx, [arena_bytes]
        lea     r8, [sim_params]
        call    sim_init
        ret

; ------------------------------------------------------------------
; hud_draw - paint the species matrix over the blitted frame (read-only).
;   in:       nothing; reads [hud_on], [wnd_dc] and the matrix block in
;             [sim_params] (SP_SPECIES_N, SP_MATRIX)
;   out:      nothing; the window DC is painted when [hud_on] is non-zero,
;             and the routine returns without touching a register or the
;             device when it is zero
;   clobbers: caller-saved, flags, xmm0; rbx/rsi/rdi/r12 are saved and
;             restored (the loop indices have to survive the GDI calls)
;   MXCSR:    read, not written - the arithmetic here is one multiply and a
;             truncating convert, so the pinned rounding mode does not
;             reach it and no denormal can arise from a [-1, 1] input
;   note:     creates no GDI object. SetBkColor plus an empty ExtTextOut with
;             ETO_OPAQUE fills the rectangle with the background colour, so
;             there is no brush to select, restore or leak - a handle leak in
;             a per-frame overlay is the failure this shape removes rather
;             than guards against. It also explains the absence of any
;             DeleteObject below, which would otherwise read as a bug.
;   note:     nothing here reads or writes the arena, so the HUD is outside
;             the determinism surface: the same seed produces the same state
;             whether it is up or not
; ------------------------------------------------------------------
hud_draw:
        cmp     dword [hud_on], 0
        jz      .off
        push    rbx
        push    rsi
        push    rdi
        push    r12
        sub     rsp, 8                  ; entry rsp = 8 mod 16, +4 pushes = 8
        mov     r12d, [sim_params+SP_SPECIES_N]   ; -> 0 mod 16 for invoke

        ; Backdrop first: one fill behind the grid, HUD_PAD wider on every
        ; side, so the unpainted HUD_GAP of each cell reads as a gridline and
        ; the panel stays legible over any particle colour.
        mov     eax, r12d
        imul    eax, HUD_CELL
        sub     eax, HUD_GAP
        add     eax, HUD_PAD*2          ; panel side in pixels
        mov     ecx, HUD_ORG-HUD_PAD
        mov     [hud_rect.left], ecx
        mov     [hud_rect.top], ecx
        add     eax, ecx
        mov     [hud_rect.right], eax
        mov     [hud_rect.bottom], eax
        invoke  SetBkColor, [wnd_dc], HUD_BACK
        invoke  ExtTextOutA, [wnd_dc], 0, 0, ETO_OPAQUE, hud_rect, 0, 0, 0

        lea     rdi, [sim_params+SP_MATRIX]
        xor     ebx, ebx                ; i = row = the species being acted on
  .row:
        cmp     ebx, r12d
        jae     .rows_done
        xor     esi, esi                ; j = column = the species acting
  .col:
        cmp     esi, r12d
        jae     .row_next
        mov     eax, esi                ; cell rectangle, HUD_GAP short of the
        imul    eax, HUD_CELL           ;   pitch on the right and the bottom
        add     eax, HUD_ORG
        mov     [hud_rect.left], eax
        add     eax, HUD_CELL-HUD_GAP
        mov     [hud_rect.right], eax
        mov     eax, ebx
        imul    eax, HUD_CELL
        add     eax, HUD_ORG
        mov     [hud_rect.top], eax
        add     eax, HUD_CELL-HUD_GAP
        mov     [hud_rect.bottom], eax

        ; Colour = the coefficient: green for attraction, red for repulsion,
        ; intensity for magnitude. The sign is read off the stored bit pattern
        ; rather than a compare, so -0.0 takes the repulsion branch at zero
        ; intensity and paints black either way.
        mov     eax, ebx
        shl     eax, 3                  ; the matrix stride is 8 f32 (i*8 + j)
        add     eax, esi
        mov     ecx, [rdi+rax*4]        ; the f32 bit pattern of a
        mov     edx, ecx
        and     edx, 0x7FFFFFFF         ; |a|, by clearing the sign bit
        movd    xmm0, edx
        minss   xmm0, [f_one]           ; init_core validates every coefficient
                                        ;   into [-1, 1]; this holds the byte
                                        ;   range even if that ever stops being
                                        ;   true, because 256 is not a colour
        mulss   xmm0, [hud_255]
        cvttss2si eax, xmm0             ; v in [0, 255], truncating
        test    ecx, ecx
        js      .repulsion
        shl     eax, 8                  ; attraction -> the green byte
  .repulsion:                           ; repulsion -> the red byte, already in
        mov     [hud_color], eax        ;   place (COLORREF is 0x00BBGGRR)
        invoke  SetBkColor, [wnd_dc], [hud_color]
        invoke  ExtTextOutA, [wnd_dc], 0, 0, ETO_OPAQUE, hud_rect, 0, 0, 0
        inc     esi
        jmp     .col
  .row_next:
        inc     ebx
        jmp     .row
  .rows_done:
        add     rsp, 8
        pop     r12
        pop     rdi
        pop     rsi
        pop     rbx
  .off:
        ret

; ------------------------------------------------------------------
; frame_pace - wait out the current frame to the 60 fps QPC deadline, then
; advance the deadline (no catch-up: resync if the frame overran; decision 11).
;   in/out:   reads [qpc_freq]/[ticks_per_frame]/[htimer], updates [qpc_deadline]
;   clobbers: caller-saved, flags
;   MXCSR:    untouched (integer only)
; ------------------------------------------------------------------
frame_pace:
        sub     rsp, 8                  ; entry rsp = 8 mod 16 -> 0 for invoke
        invoke  QueryPerformanceCounter, qpc_now
        mov     rax, [qpc_deadline]
        sub     rax, [qpc_now]          ; remaining ticks (signed)
        jle     .advance                ; deadline already passed: no wait
        mov     rcx, 10000000           ; ticks -> 100 ns units: *1e7 / freq
        mul     rcx                     ; rdx:rax (rax < freq/60, no overflow)
        div     qword [qpc_freq]        ; rax = 100 ns units to wait
        neg     rax                     ; negative => relative due time
        mov     [due_time], rax
        invoke  SetWaitableTimer, [htimer], due_time, 0, 0, 0, 0
        invoke  WaitForSingleObject, [htimer], -1   ; INFINITE
  .advance:
        invoke  QueryPerformanceCounter, qpc_now
        mov     rax, [qpc_deadline]
        add     rax, [ticks_per_frame]
        mov     rcx, [qpc_now]
        cmp     rax, rcx
        jae     .store                  ; next deadline still ahead
        mov     rax, rcx                ; fell behind: resync, no catch-up
        add     rax, [ticks_per_frame]
  .store:
        mov     [qpc_deadline], rax
        add     rsp, 8
        ret

; ------------------------------------------------------------------
; capture_frame - close the frame's work window and record its length.
;   in:       [cap_t0] = QPC at the top of .step, [capture_buf], [capture_count]
;   out:      eax = 0 while the run continues; 1 once CAPTURE_FRAMES samples
;             have been recorded AND swarm-frames.bin has been written
;   clobbers: caller-saved, flags
;   MXCSR:    untouched (integer only)
;   note:     the window closes a handful of instructions after BitBlt returns
;             (the count check and this call), not at the exact BitBlt return -
;             nanoseconds against a millisecond-scale frame, and disclosed
;             rather than rounded away
; ------------------------------------------------------------------
capture_frame:
        sub     rsp, 8                  ; entry rsp = 8 mod 16 -> 0 for invoke
        ; Bounds guard: the sample index can never run past the buffer. The
        ; pump cannot reach another frame once WM_QUIT is posted, so this
        ; branch is unreachable by construction and correspondingly unproven -
        ; it is here because the cost of being wrong about that is a write past
        ; the end, and the cost of the guard is two instructions.
        mov     ecx, [capture_count]
        cmp     ecx, CAPTURE_FRAMES
        jae     .complete
        invoke  QueryPerformanceCounter, cap_t1
        mov     rax, [cap_t1]
        sub     rax, [cap_t0]           ; work-window ticks, pacing excluded
        mov     ecx, [capture_count]
        mov     rdx, [capture_buf]
        mov     [rdx+rcx*8], rax
        inc     ecx
        mov     [capture_count], ecx
        cmp     ecx, CAPTURE_FRAMES
        jb      .more
        call    capture_write           ; never returns unless the dump landed
  .complete:
        mov     eax, 1
        add     rsp, 8
        ret
  .more:
        xor     eax, eax
        add     rsp, 8
        ret

; ------------------------------------------------------------------
; capture_write - dump the header and the raw samples to swarm-frames.bin.
;   in:       [capture_buf], [capture_count], [qpc_freq], [sim_params]
;   out:      nothing on success; exits the process with code 1 on any failure
;   clobbers: caller-saved, flags
;   MXCSR:    untouched (integer only)
;   note:     fail-closed on all three write paths - a create that failed, a
;             WriteFile that failed, and a WriteFile that reported fewer bytes
;             than asked for. A capture run that exits 0 has a complete file,
;             so an exit code can be trusted to mean a measurement exists
; ------------------------------------------------------------------
capture_write:
        sub     rsp, 8                  ; entry rsp = 8 mod 16 -> 0 for invoke
        mov     rax, [qpc_freq]         ; the header is the run's disclosure:
        mov     [cap_freq], rax         ;   the analysis needs the tick rate,
        mov     eax, [capture_count]    ;   and the scene needs n/flags/seed to
        mov     [cap_samples], rax      ;   be recomputable from the file alone
        shl     rax, 3                  ; u64 per sample
        mov     [cap_bytes], rax
        mov     eax, [sim_params+SP_N]
        mov     [cap_n], eax
        mov     eax, [sim_params+SP_FLAGS]
        mov     [cap_flags], eax
        mov     rax, [sim_params+SP_SEED]
        mov     [cap_seed], rax

        invoke  CreateFileW, cap_path, GENERIC_WRITE, 0, 0, CREATE_ALWAYS, \
                FILE_ATTRIBUTE_NORMAL, 0
        cmp     rax, INVALID_HANDLE_VALUE
        je      .fail                   ; e.g. the name is taken by a directory
        mov     [cap_file], rax
        invoke  WriteFile, [cap_file], cap_header, CAP_HEADER_BYTES, cap_written, 0
        test    eax, eax
        jz      .fail_close
        cmp     dword [cap_written], CAP_HEADER_BYTES
        jne     .fail_close             ; a short header is not a header
        invoke  WriteFile, [cap_file], [capture_buf], [cap_bytes], cap_written, 0
        test    eax, eax
        jz      .fail_close
        mov     eax, [cap_written]
        cmp     rax, [cap_bytes]
        jne     .fail_close             ; a short dump is not a measurement
        invoke  CloseHandle, [cap_file]
        add     rsp, 8
        ret
  .fail_close:
        invoke  CloseHandle, [cap_file]
  .fail:
        invoke  ExitProcess, 1

section '.data' data readable writeable

  _title         TCHAR 'swarm.asm', 0
  _class         TCHAR 'SWARM', 0
  smoke_needle   db '-smoke', 0
  capture_needle db '-capture', 0
  cap_path       du 'swarm-frames.bin', 0    ; CreateFileW takes UTF-16

  ; Preset failure text. Every cap that appears in a message is formatted from
  ; the constant that enforces it, so the sentence a reader sees cannot drift
  ; away from the check that produced it.
  fmt_reason    db 'swarm.asm could not use the preset "%s".', 13, 10, 13, 10, \
                   '%s', 13, 10, 13, 10, \
                   'Nothing was applied and nothing was drawn.', 0
  fmt_parse     db 'The preset grammar rejected the file: error %u on line %u ', \
                   '(the parser returned %d).', 0
  fmt_too_big   db 'The file is larger than the %u-byte cap this reader accepts.', 0
  fmt_path_long db 'The path is longer than the %u bytes this reader accepts, ', \
                   'so no file was opened.', 0
  why_open      db 'The file could not be opened for reading.', 0
  why_alloc     db 'The read buffer could not be committed.', 0
  why_read      db 'The file could not be read to its end.', 0

  wc   WNDCLASSEX sizeof.WNDCLASSEX, CS_OWNDC, WindowProc, 0, 0, NULL, NULL, NULL, NULL, NULL, _class, NULL
  rect RECT 0, 0, FRAME_W, FRAME_H

  ; 32-bit top-down DIB: negative height puts row 0 at the top, matching the
  ; framebuffer layout the plot pass will assume.
  bmi  BITMAPINFOHEADER sizeof.BITMAPINFOHEADER, FRAME_W, -FRAME_H, 1, 32, BI_RGB, 0, 0, 0, 0, 0

  msg  MSG

  hwnd        dq ?
  hdib        dq ?
  wnd_dc      dq ?
  mem_dc      dq ?
  old_bmp     dq ?
  pixels      dq ?
  arena       dq ?
  arena_bytes dq ?
  win_w       dd ?
  win_h       dd ?
  frame_count dd 0
  smoke_mode  dd 0

  ; -capture state. capture_buf stays 0 outside capture mode - the buffer is
  ; committed at startup only when the flag is present, so the shipped image
  ; carries none of it.
  capture_mode  dd 0
  capture_count dd 0                    ; samples recorded so far
  align 8
  cmd_line    dq ?                      ; GetCommandLine result, scanned twice
  capture_buf dq 0                      ; CAPTURE_FRAMES u64 work-window ticks
  cap_t0      dq ?                      ; work window open (top of .step)
  cap_t1      dq ?                      ; work window close (after BitBlt)
  cap_file    dq ?                      ; swarm-frames.bin handle
  cap_bytes   dq ?                      ; sample bytes asked of WriteFile
  cap_written dd ?                      ; WriteFile's LPDWORD out-parameter

  ; The swarm-frames.bin header, laid out in place so it is written with one
  ; WriteFile and no marshalling. Field order and widths are the file format;
  ; the check below refuses a layout that has drifted from CAP_HEADER_BYTES.
  align 8
  cap_header  db 'SWRMFRM1'             ; magic, and the format version in it
  cap_freq    dq ?                      ; QueryPerformanceFrequency, ticks/s
  cap_samples dq ?                      ; u64 samples following the header
  cap_n       dd ?                      ; particle count of the captured run
  cap_flags   dd ?                      ; SwarmParams flags (FLAG_GRID et al.)
  cap_seed    dq ?                      ; world seed
  if $ - cap_header <> CAP_HEADER_BYTES
        err     ; the header fields and CAP_HEADER_BYTES disagree
  end if

  ; -preset state. All of it is startup-only: nothing here is read once the
  ; render loop begins, and preset_buf is released as soon as the parse commits.
  align 8
  preset_file   dq ?                    ; the open preset handle
  preset_buf    dq 0                    ; PRESET_MAX_BYTES+1 read buffer
  preset_at     dq ?                    ; ReadFile destination, staged for invoke
  preset_reason dq ?                    ; the failure text preset_fail formats in
  preset_len    dd 0                    ; bytes read so far
  preset_want   dd ?                    ; bytes asked of the current ReadFile
  preset_read   dd ?                    ; ReadFile's LPDWORD out-parameter
  preset_err_code dd ?                  ; PERR_* out of the packed parse error
  preset_err_line dd ?                  ; 1-based line out of the same word
  preset_err_raw  dd ?                  ; the return itself, so the split is checkable

  ; Interactive state (written by WindowProc, consumed at the step boundary).
  paused      dd 0
  reseed_req  dd 0
  reroll_req  dd 0
  ; The HUD toggle is the exception: it is not a step-boundary edit, because
  ; the HUD reads the matrix and writes nothing the simulation can see. Off by
  ; default, so an unattended run - the smoke gate, a capture - never draws it.
  hud_on      dd 0

  ; Per-cell matrix editing. WindowProc writes only these, in whole steps;
  ; ui_apply_matrix_edits at the step boundary is the only thing that turns
  ; them into a matrix value.
  edit_req    dd 0                      ; steps are pending
  edit_cell   dd -1                     ; cell under an in-flight drag, else -1
  edit_anchor dd 0                      ; client y the drag has counted up to
  edit_steps  dd 0                      ; wheel steps, staged across an invoke
  edit_pt     dd ?, ?                   ; POINT, for ScreenToClient
  edit_notch  dd MATRIX_CELLS dup (0)   ; pending steps, one per matrix cell

  hud_rect    RECT ?                    ; rebuilt per fill; ExtTextOut reads it
  hud_color   dd ?                      ; the cell COLORREF, staged for invoke
  hud_255     dd 255.0
  edit_step   dd 0.02                   ; matrix units per wheel notch / drag step
  edit_neg_one dd -1.0                  ; the lower clamp; f_one is the upper

  align 8
  ui_rng          dq 0x243F6A8885A308D3   ; UI RNG state (distinct from the sim seed)
  qpc_freq        dq ?                     ; QueryPerformanceFrequency ticks/s
  qpc_now         dq ?                     ; scratch LARGE_INTEGER
  qpc_deadline    dq ?                     ; next frame's QPC target
  ticks_per_frame dq ?                     ; qpc_freq / TARGET_FPS
  due_time        dq ?                     ; SetWaitableTimer relative due (100 ns)
  htimer          dq ?                     ; high-resolution waitable timer

  align 16
  mxcsr_pin   dd SEAM_MXCSR                ; decision 2: FTZ+DAZ, all masked, RN
                                           ;   (one source of truth: seam.inc)

  ; Default preset: a SwarmParams (abi.inc SP_*), 304 bytes, Pack=4. A
  ; four-species world with a varied attraction matrix; dt/friction tuned for
  ; visible swarming.
  ;
  ; This is the M1 acceptance configuration, and three of its fields carry that
  ; claim rather than taste. n is the acceptance count. FLAG_GRID is what makes
  ; the count reachable. rmax = 0.05 is the masterplan's M1 amendment bound: the
  ; cost is rmax-dependent, and the layout g rule (1/g >= rmax, layout.inc)
  ; turns 0.05 into g = 16, so the 3x3 neighbourhood spans 9 of 256 cells
  ; instead of the whole population. ExePresetTests reads these three back out
  ; of the assembled image, so drifting any of them fails the suite.
  align 16
  sim_params:
        dd 1                            ; version
        dd SIM_N                        ; n
        dd 4                            ; species_n
        dq 0x9E3779B97F4A7C15           ; seed
        dd 0.05                         ; rmax
        dd 0.3                          ; beta
        dd 0.02                         ; dt
        dd 0.71                         ; friction
        dd 10.0                         ; force_scale
        dd 0                            ; force_path (auto)
        dd FLAG_GRID                    ; flags
        dd  0.5,-0.2, 0.3,-0.5, 0,0,0,0 ; matrix row 0 (8 wide, first 4 used)
        dd -0.3, 0.6,-0.4, 0.2, 0,0,0,0 ; row 1
        dd  0.2, 0.3,-0.6, 0.4, 0,0,0,0 ; row 2
        dd -0.4, 0.1, 0.5, 0.3, 0,0,0,0 ; row 3
        dd 0,0,0,0,0,0,0,0              ; rows 4-7 unused
        dd 0,0,0,0,0,0,0,0
        dd 0,0,0,0,0,0,0,0
        dd 0,0,0,0,0,0,0,0

  ; Startup-only buffers, last in the section because they are the only bulk
  ; here: the path as given, the formatted detail sentence, and the message the
  ; box shows. None of them is touched after the render loop starts.
  preset_path   rb PRESET_PATH_MAX
  why_detail    rb PRESET_MSG_MAX
  msg_buf       rb PRESET_MSG_MAX

  ; The M3 worker pool's mutable platform state (handles, ranges, publish slot).
  pool_storage

section '.idata' import data readable writeable

  ; kernel32ex is a second KERNEL32.DLL descriptor: the bundled api/kernel32.inc
  ; predates CreateWaitableTimerExW and GetLogicalProcessorInformation, so they
  ; are imported here. Same DLL name, so the import allowlist
  ; (kernel32/user32/gdi32) is unaffected. The pool's other primitives
  ; (CreateThread, CreateEventW, SetEvent, the waits, CloseHandle, GetSystemInfo)
  ; are already in the bundle.
  library kernel32,   'KERNEL32.DLL',\
          user32,     'USER32.DLL',\
          gdi32,      'GDI32.DLL',\
          kernel32ex, 'KERNEL32.DLL'

  include 'api\kernel32.inc'
  include 'api\user32.inc'
  include 'api\gdi32.inc'
  import kernel32ex, CreateWaitableTimerExW,'CreateWaitableTimerExW',\
                     GetLogicalProcessorInformation,'GetLogicalProcessorInformation'
