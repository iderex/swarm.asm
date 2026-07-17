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
SP_FORCE_PATH  = 40                     ; u32, 0 auto / 1 AVX2 / 2 AVX-512 / 3 scalar
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

; --- arena layout (docs/MASTERPLAN.md, decisions 1 and 3) ------------------
; All state lives in the caller-owned arena. The 512-byte header carries the
; metadata + a copy of the validated SwarmParams (which the step pass reads);
; then twelve padded_n*4 particle arrays (bank OUT, then bank IN), the cell-id
; array, and the (g*g+1)*4 cell-start prefix. layout_bytes_core computes the
; total; init_core fills the header and bank OUT. A change to any offset or to
; swarm_layout_bytes' result for the same params is an ABI version bump.

AH_MAGIC   = 0                          ; u64, 'SWRM' arena stamp
AH_ABI     = 8                          ; u32, = SWARM_ABI_VERSION
AH_PATH    = 12                         ; u32, selected code path (1 AVX2 / 2 AVX-512 / 3 scalar)
AH_FRAME   = 16                         ; u64, step counter (0 at init)
AH_RNG     = 24                         ; u64, live splitmix64 state
AH_PADDED  = 32                         ; u32, cached padded_n
AH_G       = 36                         ; u32, cached grid dimension
AH_PARAMS  = 208                        ; SwarmParams copy (304 B) -> ends at 512
AH_SIZE    = 512

ARENA_MAGIC = 0x004D525753             ; 'SWRM\0\0\0' little-endian tag

CPU_AVX2   = 1                          ; swarm_cpu_paths bit0
CPU_AVX512 = 2                          ; swarm_cpu_paths bit1

; swarm_init return codes: 0 success, else a small negative i32. All three
; failures are detected before the first arena write (fail-closed: a rejected
; init leaves the arena untouched).
IERR_PARAMS      = -1                   ; params fail validation
IERR_ARENA_SMALL = -2                   ; arena_bytes < swarm_layout_bytes
IERR_PATH        = -3                   ; requested/available SIMD path absent

; Per-particle arrays, in arena order after the header; each is padded_n*4 B.
; Bank OUT is written by init and the step pass; bank IN is read by the pass.
AR_X_OUT   = 0                          ; f32
AR_Y_OUT   = 1
AR_VX_OUT  = 2
AR_VY_OUT  = 3
AR_SP_OUT  = 4                          ; u32 species
AR_ID_OUT  = 5                          ; u32 original id
AR_X_IN    = 6
AR_Y_IN    = 7
AR_VX_IN   = 8
AR_VY_IN   = 9
AR_SP_IN   = 10
AR_ID_IN   = 11
AR_CELLID  = 12                         ; u32 per-particle cell id (M2)
AR_ARRAYS  = 13                         ; array count before cell_start
