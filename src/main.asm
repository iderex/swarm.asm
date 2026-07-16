; swarm.asm — entry point.
;
; Walking skeleton: assembles, runs, exits 0. It exists so the toolchain, the
; build script, CI, and the smoke test are proven end to end before the first
; kernel line lands (M0). The window and the engine arrive with M1.

format PE64 GUI 6.0
entry start

include 'win64a.inc'

section '.text' code readable executable

; ------------------------------------------------------------------
; start — process entry.
;   in:       nothing (RSP ≡ 8 mod 16, as delivered by the loader)
;   out:      does not return; exits the process with code 0
;   clobbers: n/a (process ends here)
; ------------------------------------------------------------------
start:
        sub     rsp, 40                 ; 32 bytes shadow space + 8 to align
        xor     ecx, ecx                ; uExitCode = 0
        call    [ExitProcess]           ; never returns

section '.idata' import data readable writeable

        library kernel32, 'KERNEL32.DLL'
        import  kernel32, \
                ExitProcess, 'ExitProcess'
