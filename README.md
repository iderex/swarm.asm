# swarm.asm

A Particle Life engine written entirely in hand-written x64 assembly.

**Goal: 1,000,000 interacting particles at 60 fps - no GPU, no dependencies,
one small `.exe`.**

> [!NOTE]
>
> ### 🤝 AI-assisted, human-owned
>
> Development here is AI-assisted. Claude (Anthropic) helps with individual process steps - generating and analysing code, running the adversarial security reviews, and translating documentation and comments into English. It never hands over finished, unreviewed work: each step is only a proposal. A human reviews, understands, edits where needed, and signs off on every one - the AI proposes, a person decides, and a human stays responsible for every line that ships, at all times. The review discipline is modelled, as far as is practical for a volunteer project, on the change-control expected of TÜV/BSI-certified software in a critical sector such as healthcare - with no claim to actual certification. In short: nothing lands because a tool suggested it; it lands because a person verified it.

> [!NOTE]
>
> ### Maturity: In-Development
>
> **In-Development** -> Alpha -> Beta -> Release Candidate -> Full Release.
> Nothing has been released: there is no tag, no published binary, and the
> milestone this stage ends with, M1, is not closed. Build it from source or
> read the code; do not depend on it. The stage advances when a milestone
> closes, and the mapping is fixed here rather than judged each time: M1 closed
> makes it Alpha, M2 Beta, M3 Release Candidate, and M4 with `v1.0.0` tagged is
> Full Release.

## What

Particle Life is a simple rule set with startlingly lifelike results: N
particle species, an N×N attraction/repulsion matrix, and out of nothing but
pairwise forces emerge cells, swarms, chasers, and self-assembling structures.
Every run with a different matrix is a different ecosystem.

Dozens of Particle Life implementations exist - in C++, Java, JavaScript,
Godot. This one is different in a single way: **the entire simulation kernel
is hand-written x64 assembly** (AVX2 today; an AVX-512 path is planned for M3),
and the whole program is one small Windows executable that imports nothing but
`kernel32`, `user32`, and `gdi32`. No CRT, no runtime, no framework. The
assembly is the product.

## Status

**It runs.** The engine simulates and draws a live particle world:
`build/swarm.exe` opens a window and steps a real multi-species swarm every
frame. Both force kernels are in: the **scalar reference** - the semantic
anchor, hand-written x64 that reproduces the pinned physics
([docs/MASTERPLAN.md](docs/MASTERPLAN.md)) and is checked against an
independent C# oracle every step - and the **AVX2 path**, auto-selected on
AVX2 CPUs and verified to match the scalar result within the oracle's epsilon.
The measured speedup and its honest caveats live in
[docs/BENCHMARKS.md](docs/BENCHMARKS.md): the brute-force AVX2 pass is ~1.85×
the scalar reference on Zen 3 (the vector loop is divider-bound; the scalar
path cheaply skips the out-of-range pairs the vector path still computes), and
the larger SIMD win waits on the M2 cell-sorted layout that shrinks the
candidate set from n² to the in-range neighbours.

**It's interactive.** The window is keyboard-driven - **Space** pauses,
**R** reseeds the world, **M** rerolls the attraction matrix, **H** shows or
hides the species matrix as a grid of coloured cells over the frame (green for
attraction, red for repulsion, brightness for strength), **Esc** quits -
with edits applied at step boundaries and the frame paced to a real 60 fps by a
high-resolution timer. The live count is the M1 acceptance count, 8,192, on the
cell-sorted grid across the worker pool; **8,192 @ 60 fps is measured, not
projected** - the worst p99 work window across six 3600-frame captures is
6.100 ms against the 16.67 ms budget
([docs/BENCHMARKS.md](docs/BENCHMARKS.md)), where one core on brute force was
~19 fps at the same count. A full per-cell matrix editor is a later increment.

The full architecture - force model, memory layout, SIMD strategy,
determinism contract - is recorded with rationale in the masterplan. Progress:

| Milestone        | Status | Deliverable                                                                      |
| ---------------- | ------ | -------------------------------------------------------------------------------- |
| M0 - Foundation  | done   | Design, pinned toolchain, CI, test harness                                       |
| M1 - First light | active | Brute-force AVX2 kernel + live window; 8,192 live, acceptance measured at 60 fps |
| M2 - Scale       | active | Spatial grid; 50k and 500k particles at 60 fps                                   |
| M3 - One million | -      | Multithreading + AVX-512 path, 1M particles at 60 fps                            |
| M4 - Launch      | -      | Benchmark suite vs. existing ports, presets, write-up                            |

What works today: the deterministic RNG, a fail-closed preset grammar, CPU
feature detection, arena allocation and seeded init, the scalar and AVX2
force+integrate kernels (build / pass / step, auto-selected and cross-checked
against the oracle), the id-ordered state read-back, the raster, and the live
interactive window - each landing behind a green CI gate with oracle-checked
tests. The M2 spatial grid (cell binning, stable counting sort, and the 3×3
neighbourhood force that cuts the per-step work from n² to the in-range
neighbours) is in the kernel, cross-checked against brute force, and measured at
50k / 500k ([docs/BENCHMARKS.md](docs/BENCHMARKS.md)); the live window runs on
it, across the M3 worker pool, at the M1 acceptance count. `swarm.exe -capture`
is the instrument that measured that: it dumps the raw per-frame samples the
recorded figure is recomputed from. A per-cell live matrix editor is still
open.

(M1 was originally 50k; brute force at 50k is arithmetically impossible at
60 fps - the reasoning lives in [docs/MASTERPLAN.md](docs/MASTERPLAN.md),
"M1 amendment". The grid delivers 50k in M2 with room to spare.)

## Principles

- **Zero dependencies, verifiably.** A conformance test parses the built
  executable's import table and fails the build if anything beyond
  kernel32/user32/gdi32 appears.
- **Deterministic.** Same seed, same universe - bit-exact per code path.
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
into `tools/fasm/` on first run - the download is verified against a pinned
SHA-256 before it is unpacked. Output lands in `build/swarm.exe` and
`build/swarm.kernel.dll` - `swarm.asm` (platform + kernel) assembles to the
shipped exe, `swarm_dll.asm` (kernel + seam shims) assembles to the DLL the
test harness P/Invokes; both include the same `src/kernel/*.inc`, so the
tested kernel is the shipped kernel.

The test harness (from M0 onward) needs the .NET 9 SDK. Run `.\build.ps1`
first - `dotnet test` loads the freshly built `swarm.kernel.dll`:

```powershell
dotnet test tests\Swarm.Tests\Swarm.Tests.csproj
```

## Contributing

Issue-driven: every change starts as an issue and lands as a gated PR - see
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)

See NOTICE.md for the intended-use notice.
