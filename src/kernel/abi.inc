; ABI constants shared by every build target (docs/MASTERPLAN.md, decision 5).
;
; SWARM_ABI_VERSION versions the DLL seam: the C# harness hard-asserts it via
; swarm_version() before running any other test, so a stale build artifact
; fails loudly instead of producing confusing downstream mismatches.
; The layout formula in layout.inc is part of this contract: any change to
; swarm_layout_bytes' result for the same params is an ABI version bump.

SWARM_ABI_VERSION = 1

; SwarmParams — the seam struct, mirrored 1:1 in C# (sequential, Pack=4).
; 304 bytes total; matrix rows beyond species_n stay zero and are never read.

SP_VERSION     = 0                      ; u32, must be 1
SP_N           = 4                      ; u32, [1, 2^20]
SP_SPECIES_N   = 8                      ; u32, [1, 8]
SP_SEED        = 12                     ; u64 (unaligned by Pack=4 — by design)
SP_RMAX        = 20                     ; f32, (0, 0.25]
SP_BETA        = 24                     ; f32, [0.05, 0.95]
SP_DT          = 28                     ; f32, (0, 0.1]
SP_FRICTION    = 32                     ; f32, [0, 1]
SP_FORCE_SCALE = 36                     ; f32, (0, 100]
SP_FORCE_PATH  = 40                     ; u32, 0 auto / 1 AVX2 / 2 AVX-512
SP_FLAGS       = 44                     ; u32, reserved — must be 0
SP_MATRIX      = 48                     ; f32[8][8], row-major, each [-1, 1]
SP_SIZE        = 304

; swarm_parse_preset error encoding: success = 0; failure =
;   (i32)(0x80000000 or (code shl 20) or min(line, 0xFFFFF)).
; Bit 30 is reserved (always 0); codes are extendable, so tests pin exact
; codes only for crafted inputs — fuzz asserts the invariants alone.

PERR_VERSION      = 1                   ; first non-blank line is not: swarm 1
PERR_UNKNOWN_KEY  = 2                   ; token matches no key, nor matrix/end
PERR_DUP_KEY      = 3                   ; key seen twice
PERR_MISSING_KEY  = 4                   ; seen mask incomplete at the matrix line
PERR_NUM_SYNTAX   = 5                   ; malformed number token
PERR_NUM_OVERFLOW = 6                   ; u64 wrap / >16 hex digits / mantissa cap
PERR_NUM_FRAC     = 7                   ; more than 6 fraction digits
PERR_RANGE        = 8                   ; value outside the pinned range table
PERR_MATRIX_SHAPE = 9                   ; wrong row length or row count
PERR_MISSING_END  = 10                  ; EOF before the grammar completed
PERR_TRAILING     = 11                  ; non-blank content after end
PERR_EXTRA_TOKEN  = 12                  ; surplus token on a structural line
