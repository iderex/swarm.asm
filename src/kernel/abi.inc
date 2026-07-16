; ABI constants shared by every build target (docs/MASTERPLAN.md, decision 5).
;
; SWARM_ABI_VERSION versions the DLL seam: the C# harness hard-asserts it via
; swarm_version() before running any other test, so a stale build artifact
; fails loudly instead of producing confusing downstream mismatches.

SWARM_ABI_VERSION = 1
