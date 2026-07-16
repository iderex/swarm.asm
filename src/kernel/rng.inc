; rng.inc — splitmix64, the engine's only RNG (docs/MASTERPLAN.md, decision 8).
;
; One u64 of state, ~10 instructions, no warm-up. The macro form inlines into
; init loops (slice 3); rng_fill_core exposes the raw stream so the harness
; asserts asm = C# u64-for-u64 before any physics test runs. The u01 and
; bounded-int mappings land with swarm_init.

RNG_GAMMA = 0x9E3779B97F4A7C15
RNG_MIX1  = 0xBF58476D1CE4E5B9
RNG_MIX2  = 0x94D049BB133111EB

; rng_next — advance the stream one draw.
;   state: register holding the current state (updated in place)
;   value: register receiving the output draw
;   tmp:   scratch register (clobbered)
;   All three must be distinct 64-bit GPRs; flags clobbered; no memory.
macro rng_next state, value, tmp {
        mov     tmp, RNG_GAMMA
        add     state, tmp
        mov     value, state
        mov     tmp, value
        shr     tmp, 30
        xor     value, tmp
        mov     tmp, RNG_MIX1
        imul    value, tmp
        mov     tmp, value
        shr     tmp, 27
        xor     value, tmp
        mov     tmp, RNG_MIX2
        imul    value, tmp
        mov     tmp, value
        shr     tmp, 31
        xor     value, tmp
}

; ------------------------------------------------------------------
; rng_fill_core — write the first count draws of the seed's stream.
;   in:       rcx = seed, rdx = out (u64*, caller-owned, non-null when
;             count > 0), r8d = count
;   out:      out[0..count) filled; count = 0 writes nothing
;   clobbers: rax, rcx, rdx, r8, r9, flags (kernel tier: all caller-owned)
;   MXCSR:    untouched (integer only)
;   align:    none required
; ------------------------------------------------------------------
rng_fill_core:
        test    r8d, r8d
        jz      .done
  .fill:
        rng_next rcx, rax, r9
        mov     [rdx], rax
        add     rdx, 8
        dec     r8d
        jnz     .fill
  .done:
        ret
