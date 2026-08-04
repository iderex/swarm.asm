# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions, once
tagged, are three-part `X.Y.Z` - see [docs/RELEASE-POLICY.md](docs/RELEASE-POLICY.md)
for what each digit means and how a release class picks it.

## Unreleased

The engine has not tagged a version yet - it is still in the milestone build
order (M0–M4, see [docs/MASTERPLAN.md](docs/MASTERPLAN.md)). The entries below
backfill the milestones delivered so far; each entry cites the issue or PR
that shipped it.

### Added

- **M0 - Foundation.** The twelve architecture decisions were settled and
  recorded in docs/MASTERPLAN.md before any kernel line (#6). The pinned,
  zero-dependency toolchain bootstrap landed - `tools/get-fasm.ps1` fetches
  FASM against a pinned SHA-256, with an HTTP fallback when the origin
  refuses the TLS handshake (#8) - alongside `build.ps1` and the C#/.NET 9
  xUnit test net, stood up end to end as a walking skeleton (#9), and the CI
  gate was extended with a binary size report, locked NuGet restore, and a
  Prettier formatting check (#10). The owned, seeded RNG (splitmix64) was
  pinned against a C# oracle (#12), and the fail-closed preset parser with
  arena sizing landed (#13). The kernel-purity and register-contract-header
  conformance scans were added (#28) and hardened against struct-init labels
  and uneven headers (#31, #36).
- **M1 - First light.** CPU feature detection and arena initialization landed
  (#14), then the id-ordered state read-back (#15), the scalar
  reference force+integrate kernel (#16), and the plot raster (#17);
  `swarm.exe` was wired to the live simulation (#18). The AVX2 gather force
  kernel and its CPUID-gated dispatch landed next (#20), with a force-kernel
  micro-benchmark and its recorded baseline (#21). Keyboard controls
  (Space/R/M/Esc) and real 60 fps frame pacing made the window interactive
  (#22).
- **M2 - Scale.** The spatial grid landed: cell binning and a stable
  counting-sort reorder (#24), then the 3×3 neighbourhood force pass that
  cuts per-step work from n² to the in-range neighbours (#30). The grid was
  measured at 50k and 500k particles against the brute-force cross-check and
  the baseline recorded (#49). The AVX2 force inner loop was profiled
  cycles/candidate, throughput vs. latency - isolating the divider-bound
  carried chain as the limiter, not the vector width (#65) - and the AVX2
  integrate/store tail was VEX-encoded to remove an SSE/AVX transition
  penalty (#73).

### Changed

- The README's AI-assisted development is disclosed up front, with a human
  signing off on every change that ships (#66).

### Fixed

- The M1/M2 README status lines were corrected to match what actually runs:
  the M1 live count and the M2 grid measurement (#43, #82).
