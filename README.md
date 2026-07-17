# swarm.asm

A Particle Life engine written entirely in hand-written x64 assembly.

**Goal: 1,000,000 interacting particles at 60 fps — no GPU, no dependencies,
one small `.exe`.**

## What

Particle Life is a simple rule set with startlingly lifelike results: N
particle species, an N×N attraction/repulsion matrix, and out of nothing but
pairwise forces emerge cells, swarms, chasers, and self-assembling structures.
Every run with a different matrix is a different ecosystem.

Dozens of Particle Life implementations exist — in C++, Java, JavaScript,
Godot. This one is different in a single way: **the entire simulation kernel
is hand-written x64 assembly** (AVX2 baseline, AVX-512 where the CPU has it),
and the whole program is one small Windows executable that imports nothing but
`kernel32`, `user32`, and `gdi32`. No CRT, no runtime, no framework. The
assembly is the product.

## Status

**It runs.** The engine simulates and draws a live particle world:
`build/swarm.exe` opens a window and steps a real multi-species swarm every
frame. Both force kernels are in: the **scalar reference** — the semantic
anchor, hand-written x64 that reproduces the pinned physics
([docs/MASTERPLAN.md](docs/MASTERPLAN.md)) and is checked against an
independent C# oracle every step — and the **AVX2 path**, auto-selected on
AVX2 CPUs and verified to match the scalar result within the oracle's epsilon.
The measured speedup and its honest caveats live in
[docs/BENCHMARKS.md](docs/BENCHMARKS.md): the brute-force AVX2 pass is ~1.85×
the scalar reference on Zen 3 (the vector loop is divider-bound; the scalar
path cheaply skips the out-of-range pairs the vector path still computes), and
the larger SIMD win waits on the M2 cell-sorted layout that shrinks the
candidate set from n² to the in-range neighbours.

**It's interactive.** The window is keyboard-driven — **Space** pauses,
**R** reseeds the world, **M** rerolls the attraction matrix, **Esc** quits —
with edits applied at step boundaries and the frame paced to a real 60 fps by a
high-resolution timer. The live count is set to what one core holds at 60 fps
(brute-force AVX2); **8,192 @ 60 fps waits on the M2 grid / M3 threads** (one
core is ~19 fps at 8k, [docs/BENCHMARKS.md](docs/BENCHMARKS.md)). A full
per-cell matrix editor is a later increment.

The full architecture — force model, memory layout, SIMD strategy,
determinism contract — is recorded with rationale in the masterplan. Progress:

| Milestone        | Status | Deliverable                                            |
| ---------------- | ------ | ------------------------------------------------------ |
| M0 — Foundation  | done   | Design, pinned toolchain, CI, test harness             |
| M1 — First light | active | Brute-force AVX2 kernel + live window, 8,192 particles |
| M2 — Scale       | active | Spatial grid; 50k and 500k particles at 60 fps         |
| M3 — One million | —      | Multithreading + AVX-512 path, 1M particles at 60 fps  |
| M4 — Launch      | —      | Benchmark suite vs. existing ports, presets, write-up  |

What works today: the deterministic RNG, a fail-closed preset grammar, CPU
feature detection, arena allocation and seeded init, the scalar and AVX2
force+integrate kernels (build / pass / step, auto-selected and cross-checked
against the oracle), the id-ordered state read-back, the raster, and the live
interactive window — each landing behind a green CI gate with oracle-checked
tests. The M2 spatial grid (cell binning, stable counting sort, and the 3×3
neighbourhood force that cuts the per-step work from n² to the in-range
neighbours) is in the kernel and cross-checked against brute force; wiring it
into the live window and recording the 50k / 500k measurement are the open M2
steps. What is left before the 8,192 @ 60 fps headline is throughput — the M2
grid's live mode and the M3 worker threads — plus a per-cell live matrix editor.

(M1 was originally 50k; brute force at 50k is arithmetically impossible at
60 fps — the reasoning lives in [docs/MASTERPLAN.md](docs/MASTERPLAN.md),
"M1 amendment". The grid delivers 50k in M2 with room to spare.)

## Principles

- **Zero dependencies, verifiably.** A conformance test parses the built
  executable's import table and fails the build if anything beyond
  kernel32/user32/gdi32 appears.
- **Deterministic.** Same seed, same universe — bit-exact per code path.
- **Honest numbers.** Every performance claim ships with hardware, CPU
  features, particle count, and seed. Benchmarks live in the repo.
- **Readable assembly.** Every routine carries a register contract (inputs,
  outputs, clobbers, alignment); comments explain why, not what.

## Building

Windows 10/11 x64. PowerShell:

```powershell
.\build.ps1
```

The build script bootstraps the pinned assembler ([FASM](https://flatassembler.net/))
into `tools/fasm/` on first run — the download is verified against a pinned
SHA-256 before it is unpacked. Output lands in `build/swarm.exe` and
`build/swarm.kernel.dll` — `swarm.asm` (platform + kernel) assembles to the
shipped exe, `swarm_dll.asm` (kernel + seam shims) assembles to the DLL the
test harness P/Invokes; both include the same `src/kernel/*.inc`, so the
tested kernel is the shipped kernel.

The test harness (from M0 onward) needs the .NET 9 SDK. Run `.\build.ps1`
first — `dotnet test` loads the freshly built `swarm.kernel.dll`:

```powershell
dotnet test tests\Swarm.Tests\Swarm.Tests.csproj
```

## Contributing

Issue-driven: every change starts as an issue and lands as a gated PR — see
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
