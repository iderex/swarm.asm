; swarm.exe — the engine shell: window, DIB framebuffer, blit loop.
;
; Walking skeleton (M1 slice 1): opens a fixed 1024x1024 window, clears the
; framebuffer, blits every frame, exits cleanly. The simulation kernel plugs
; into the render loop in later slices. `-smoke` on the command line renders
; a fixed number of frames and exits 0 — that flag is what CI runs, because
; the smoke gate needs a terminating process.

format PE64 GUI 6.0
entry start

include 'win64a.inc'
include 'kernel/abi.inc'

FRAME_W      = 1024                     ; framebuffer and client size, 1:1 blit
FRAME_H      = 1024
DIB_RGB_COLORS = 0                      ; not in the bundled equates
CLEAR_COLOR  = 0x001A1A22               ; 0RGB: near-black blue-grey
SMOKE_FRAMES = 60                       ; frames rendered under -smoke
WINDOW_STYLE = WS_OVERLAPPED+WS_CAPTION+WS_SYSMENU+WS_MINIMIZEBOX   ; fixed size

section '.text' code readable executable

; ------------------------------------------------------------------
; start — process entry: init, message pump + render loop, clean exit.
;   in:       nothing (RSP = 8 mod 16 as delivered by the loader; the
;             sub rsp,8 below establishes the 16-alignment every invoke
;             in this routine relies on)
;   out:      does not return; process exit code 0 (1 on init failure)
;   clobbers: n/a (process ends here)
;   MXCSR:    untouched — the 0x9FC0 main-thread pin (decision 2) lands
;             with the first FP slice
; ------------------------------------------------------------------
start:
        sub     rsp, 8

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
        mov     rdi, [pixels]           ; clear: the frame starts from a known
        mov     eax, CLEAR_COLOR        ; state every time (no accumulation)
        mov     ecx, FRAME_W*FRAME_H
        rep     stosd
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
        invoke  Sleep, 15               ; crude pacing; the waitable timer
        jmp     .pump                   ; arrives with the plot/blit slice

  .quit:
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
        cmp     r8d, VK_ESCAPE
        jne     .defwndproc
        invoke  DestroyWindow, rcx
        xor     eax, eax
        jmp     .finish
  .destroy:
        invoke  PostQuitMessage, 0
        xor     eax, eax
  .finish:
        ret
endp

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
  win_w       dd ?
  win_h       dd ?
  frame_count dd 0
  smoke_mode  dd 0

section '.idata' import data readable writeable

  library kernel32, 'KERNEL32.DLL',\
          user32,   'USER32.DLL',\
          gdi32,    'GDI32.DLL'

  include 'api\kernel32.inc'
  include 'api\user32.inc'
  include 'api\gdi32.inc'
