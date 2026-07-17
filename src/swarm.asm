; swarm.exe — the live engine: window, DIB framebuffer, and the render loop
; that steps the simulation and rasters it each frame.
;
; The kernel sources are included straight in (decision 5: the exe and the test
; DLL share the same kernel), reached through the same MXCSR/nonvolatile seam
; the DLL uses. `-smoke` on the command line runs a fixed number of real frames
; and exits 0 — that flag is what CI runs, because the smoke gate needs a
; terminating process.

format PE64 GUI 6.0
entry start

include 'win64a.inc'
include 'kernel/abi.inc'

FRAME_W      = 1024                     ; framebuffer and client size, 1:1 blit
FRAME_H      = 1024
DIB_RGB_COLORS = 0                      ; not in the bundled equates
SMOKE_FRAMES = 60                       ; frames rendered under -smoke
WINDOW_STYLE = WS_OVERLAPPED+WS_CAPTION+WS_SYSMENU+WS_MINIMIZEBOX   ; fixed size
; Live count: the largest that holds a real 60 fps single-threaded at the
; default preset (measured, docs/BENCHMARKS.md). 8,192 @ 60 fps waits on the
; M2 grid / M3 threads (brute-force AVX2 is ~53 ms/pass at 8k on one core).
SIM_N        = 3500
TARGET_FPS   = 60
VK_R         = 'R'                      ; WM_KEYDOWN gives the uppercase VK code
VK_M         = 'M'
MEM_COMMIT     = 0x1000                 ; VirtualAlloc flags (kernel64 equates
MEM_RESERVE    = 0x2000                 ;   omit these; define them locally)
PAGE_READWRITE = 0x04
CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002   ; not in the bundle
TIMER_ALL_ACCESS = 0x1F0003            ; MODIFY_STATE + SYNCHRONIZE + more

section '.text' code readable executable

; ------------------------------------------------------------------
; start — process entry: init, message pump + render loop, clean exit.
;   in:       nothing (RSP = 8 mod 16 as delivered by the loader; the
;             sub rsp,8 below establishes the 16-alignment every invoke
;             in this routine relies on)
;   out:      does not return; process exit code 0 (1 on init failure)
;   clobbers: n/a (process ends here)
;   MXCSR:    pinned to 0x9FC0 at entry (decision 2, the exe main-thread pin) —
;             so every main-thread FP op (the matrix reroll) runs under the
;             same rounding/FTZ/DAZ policy the kernel does; the seam wrappers
;             re-pin around each kernel call, harmlessly idempotent
; ------------------------------------------------------------------
start:
        sub     rsp, 8
        ldmxcsr [mxcsr_pin]             ; decision 2: main-thread MXCSR = 0x9FC0

        invoke  GetModuleHandle, 0
        mov     [wc.hInstance], rax

        ; -smoke on the command line selects the terminating CI mode.
        invoke  GetCommandLine
        mov     rcx, rax
        call    scan_smoke_flag
        mov     [smoke_mode], eax

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
        ; alignment for the pixel buffer — the plot pass must check (or
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
        ; Apply pending keyboard edits at the step boundary (decision 11:
        ; edits commit between steps). WindowProc only sets these flags.
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
        cmp     dword [paused], 0
        jne     .plot                   ; paused: skip the step, keep drawing
        mov     rcx, [arena]            ; advance the simulation one step
        mov     edx, 1
        call    sim_step
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
        cmp     [smoke_mode], 0
        je      .pace
        cmp     [frame_count], SMOKE_FRAMES
        jb      .pace
        invoke  DestroyWindow, [hwnd]   ; -> WM_DESTROY -> PostQuitMessage(0)

  .pace:
        call    frame_pace              ; wait out the frame to the 60 fps deadline
        jmp     .pump

  .quit:
        invoke  CloseHandle, [htimer]
        invoke  SelectObject, [mem_dc], [old_bmp]
        invoke  DeleteDC, [mem_dc]
        invoke  ReleaseDC, [hwnd], [wnd_dc]
        invoke  DeleteObject, [hdib]
        invoke  ExitProcess, [msg.wParam]   ; 0, from PostQuitMessage

  .fail:
        invoke  ExitProcess, 1          ; fail closed: no window, no half-run

; ------------------------------------------------------------------
; scan_smoke_flag — detect "-smoke" as a whole argument token.
;   in:       rcx = zero-terminated ANSI command line in GetCommandLine
;             form: the program token comes first, possibly quoted
;   out:      eax = 1 when a whitespace-delimited argument equals
;             "-smoke" exactly, else 0
;   clobbers: rax, rcx, rdx, r8
;   MXCSR:    untouched
;   note:     the program token is skipped so a "-smoke" inside the exe
;             path never triggers; never reads past the terminator (a
;             match window can only extend over non-NUL needle bytes)
; ------------------------------------------------------------------
scan_smoke_flag:
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
        xor     edx, edx
  .compare:
        movzx   eax, byte [smoke_needle+rdx]
        movzx   r8d, byte [rcx+rdx]
        test    al, al
        jz      .needle_end
        cmp     al, r8b
        jne     .skip_token
        inc     edx
        jmp     .compare
  .needle_end:                          ; the token must end here too —
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
; WindowProc — window procedure (Win64 ABI callee, callback seam).
;   in:       rcx hwnd, edx message, r8 wparam, r9 lparam
;   out:      rax = message result
;   clobbers: volatile registers only
;   MXCSR:    untouched
; ------------------------------------------------------------------
; The arg names deliberately differ from the .data globals ([hwnd] et
; al.) — a proc arg shadows the global inside the body, and the named
; slots hold caller stack garbage (invoke never homes the registers).
proc WindowProc wnd, wmsg, wp, lp
        cmp     edx, WM_DESTROY
        je      .destroy
        cmp     edx, WM_KEYDOWN
        je      .key
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
  .destroy:
        invoke  PostQuitMessage, 0
        xor     eax, eax
  .finish:
        ret
endp

; --- the simulation kernel, shared verbatim with the test DLL (decision 5) ---
; Pure computation, no imports; the exe stays within kernel32/user32/gdi32.
include 'platform/seam.inc'
include 'kernel/rng.inc'
include 'kernel/parse.inc'
include 'kernel/layout.inc'
include 'kernel/cpuid.inc'
include 'kernel/init.inc'
include 'kernel/state.inc'
include 'kernel/step.inc'
include 'kernel/grid.inc'
include 'kernel/plot.inc'

; Seam wrappers: each pins MXCSR to 0x9FC0, saves the Win64 nonvolatiles, and
; lands the kernel core at rsp = 0 mod 32 — the same contract the DLL exports
; carry, so the exe drives the identical, gate-verified code paths. Each wrapper
; therefore exposes the uniform seam contract in its header: it clobbers only
; the Win64 volatiles; the seam saves and restores every nonvolatile and MXCSR.

; ------------------------------------------------------------------
; sim_layout — seam wrapper over layout_bytes_core.
;   in:       rcx = SwarmParams*
;   out:      rax = arena bytes (multiple of 64), or 0 when params invalid
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap sim_layout, layout_bytes_core
; ------------------------------------------------------------------
; sim_init — seam wrapper over init_core.
;   in:       rcx arena, rdx arena_bytes, r8 SwarmParams*
;   out:      eax = 0 on success, else IERR_*
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap sim_init, init_core
; ------------------------------------------------------------------
; sim_step — seam wrapper over step_core.
;   in:       rcx arena, edx n_steps
;   out:      nothing (void); the arena is advanced n_steps
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap sim_step, step_core
; ------------------------------------------------------------------
; sim_plot — seam wrapper over plot_core.
;   in:       rcx arena, rdx pixels, r8d w, r9d h
;   out:      nothing (void); the framebuffer at rdx is written
;   clobbers: volatile (caller-saved) registers per the Win64 ABI (rax, rcx,
;             rdx, r8-r11, xmm0-xmm5); the seam saves and restores every
;             nonvolatile
;   MXCSR:    saved, pinned 0x9FC0 across the core, restored on return (seam)
; ------------------------------------------------------------------
seam_wrap sim_plot, plot_core

; ------------------------------------------------------------------
; ui_reseed — draw a fresh world seed from the UI RNG stream.
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
; ui_reroll_matrix — refill the species_n x species_n attraction block with
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
; ui_reinit — re-seed the existing arena from the (edited) params.
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
; frame_pace — wait out the current frame to the 60 fps QPC deadline, then
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

section '.data' data readable writeable

  _title       TCHAR 'swarm.asm', 0
  _class       TCHAR 'SWARM', 0
  smoke_needle db '-smoke', 0

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

  ; Interactive state (written by WindowProc, consumed at the step boundary).
  paused      dd 0
  reseed_req  dd 0
  reroll_req  dd 0

  align 8
  ui_rng          dq 0x243F6A8885A308D3   ; UI RNG state (distinct from the sim seed)
  qpc_freq        dq ?                     ; QueryPerformanceFrequency ticks/s
  qpc_now         dq ?                     ; scratch LARGE_INTEGER
  qpc_deadline    dq ?                     ; next frame's QPC target
  ticks_per_frame dq ?                     ; qpc_freq / TARGET_FPS
  due_time        dq ?                     ; SetWaitableTimer relative due (100 ns)
  htimer          dq ?                     ; high-resolution waitable timer

  align 16
  mxcsr_pin   dd 0x9FC0                    ; decision 2: FTZ+DAZ, all masked, RN

  ; Default preset: a SwarmParams (abi.inc SP_*), 304 bytes, Pack=4. A
  ; four-species world with a varied attraction matrix; rmax/dt/friction
  ; tuned for visible swarming at the scalar preview count.
  align 16
  sim_params:
        dd 1                            ; version
        dd SIM_N                        ; n
        dd 4                            ; species_n
        dq 0x9E3779B97F4A7C15           ; seed
        dd 0.08                         ; rmax
        dd 0.3                          ; beta
        dd 0.02                         ; dt
        dd 0.71                         ; friction
        dd 10.0                         ; force_scale
        dd 0                            ; force_path (auto)
        dd 0                            ; flags
        dd  0.5,-0.2, 0.3,-0.5, 0,0,0,0 ; matrix row 0 (8 wide, first 4 used)
        dd -0.3, 0.6,-0.4, 0.2, 0,0,0,0 ; row 1
        dd  0.2, 0.3,-0.6, 0.4, 0,0,0,0 ; row 2
        dd -0.4, 0.1, 0.5, 0.3, 0,0,0,0 ; row 3
        dd 0,0,0,0,0,0,0,0              ; rows 4-7 unused
        dd 0,0,0,0,0,0,0,0
        dd 0,0,0,0,0,0,0,0
        dd 0,0,0,0,0,0,0,0

section '.idata' import data readable writeable

  ; kernel32ex is a second KERNEL32.DLL descriptor: the bundled api/kernel32.inc
  ; predates CreateWaitableTimerExW, so it is imported here. Same DLL name, so
  ; the import allowlist (kernel32/user32/gdi32) is unaffected.
  library kernel32,   'KERNEL32.DLL',\
          user32,     'USER32.DLL',\
          gdi32,      'GDI32.DLL',\
          kernel32ex, 'KERNEL32.DLL'

  include 'api\kernel32.inc'
  include 'api\user32.inc'
  include 'api\gdi32.inc'
  import kernel32ex, CreateWaitableTimerExW,'CreateWaitableTimerExW'
