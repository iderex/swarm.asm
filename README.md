# swarm.asm

[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/iderex/swarm.asm/badge)](https://scorecard.dev/viewer/?uri=github.com/iderex/swarm.asm)

A Particle Life engine written entirely in hand-written x64 assembly.

**Goal: 1,000,000 interacting particles at 60 fps - no GPU, no dependencies,
one small `.exe`.**

New here? [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) is the one document that
covers it end to end: why the kernel is assembly, how the engine is built, what
the measurements say, and which two popular optimisations were measured and
dropped.

> [!NOTE]
>
> ### 🤝 AI-assisted, human-owned
>
> Development here is AI-assisted. Claude (Anthropic) helps with individual process steps - generating and analysing code, running the adversarial security reviews, and translating documentation and comments into English. It never hands over finished, unreviewed work: each step is only a proposal. A human reviews, understands, edits where needed, and signs off on every one - the AI proposes, a person decides, and a human stays responsible for every line that ships, at all times. The review discipline is modelled, as far as is practical for a volunteer project, on the change-control expected of TÜV/BSI-certified software in a critical sector such as healthcare - with no claim to actual certification. In short: nothing lands because a tool suggested it; it lands because a person verified it.

> [!NOTE]
>
> ### Maturity: Alpha
>
> In-Development -> **Alpha** -> Beta -> Release Candidate -> Full Release.
> The stage advances when a milestone closes, and the mapping is fixed here
> rather than judged each time: M1 closed makes it Alpha, M2 Beta, M3 Release
> Candidate, and M4 with `v1.0.0` tagged is Full Release. M1 is closed on a
> measurement ([docs/MASTERPLAN.md](docs/MASTERPLAN.md), "M1 closing note"), so
> the stage is Alpha. Nothing has been released even so: there is no tag and no
> published binary, and the benchmark suite is still open - no comparison
> against any other engine exists here or in
> [docs/BENCHMARKS.md](docs/BENCHMARKS.md). Build it from source or read the
> code; do not depend on it.

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
high-resolution timer. With the matrix up, the **mouse wheel** over a cell
raises or lowers that one coefficient, and **dragging** a cell up or down does
the same continuously; the value is clamped to the [-1, 1] the parameters
allow, so you can steer one species pair while the rest of the ecosystem keeps
running. The live count is the M1 acceptance count, 8,192, on the
cell-sorted grid across the worker pool; **8,192 @ 60 fps is measured, not
projected** - the worst p99 work window across six 3600-frame captures is
6.100 ms against the 16.67 ms budget
([docs/BENCHMARKS.md](docs/BENCHMARKS.md)), where one core on brute force was
~19 fps at the same count.

The full architecture - force model, memory layout, SIMD strategy,
determinism contract - is recorded with rationale in the masterplan. Progress:

| Milestone        | Status | Deliverable                                                                      |
| ---------------- | ------ | -------------------------------------------------------------------------------- |
| M0 - Foundation  | done   | Design, pinned toolchain, CI, test harness                                       |
| M1 - First light | done   | Brute-force AVX2 kernel + live window; 8,192 live, acceptance measured at 60 fps |
| M2 - Scale       | active | Spatial grid; 50k and 500k particles at 60 fps                                   |
| M3 - One million | active | Multithreading + AVX-512 path, 1M particles at 60 fps                            |
| M4 - Launch      | active | Benchmark suite vs. existing ports, presets, write-up                            |

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
recorded figure is recomputed from. The 1,000,000-particle line at the top of
this file is still a goal rather than a measured claim; the count recorded at
60 fps is the 8,192 one above.

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

## Running

```powershell
.\build\swarm.exe                   # the built-in preset
.\build\swarm.exe presets\cells.txt # a preset from a file
.\build\swarm.exe -splat            # 2x2 particles instead of 1 pixel
```

With no filename the exe runs the preset compiled into it. The first argument
that does not start with `-` is read as a preset path, so `-smoke`, `-capture`
and `-splat` can be given alongside one. A bare path that starts with `-` reads
as a flag and is skipped; quote it and it loads, because the quote is what the
scan dispatches on.

`-splat` draws each particle as a 2x2 block. On a dense display a 1-pixel
particle nearly vanishes at large counts.

It changes nothing the simulation computes, and it does change what `-capture`
records. The plot is inside the timed work window and the dump header carries
the flags word, so a capture taken with `-splat` is a different measurement of
a different raster, not the same one. Its cost has not been measured, so no
number for it is stated here or in [docs/BENCHMARKS.md](docs/BENCHMARKS.md);
every row recorded there so far is the 1-pixel raster, and each one now says
so.

[presets/](presets/) holds the committed scenes and describes each one:
`headline.txt` and `dense.txt` are the two the numbers in
[docs/BENCHMARKS.md](docs/BENCHMARKS.md) are quoted against, and `cells.txt`,
`chasers.txt`, `knots.txt` and `rosettes.txt` are four ecologies to watch. Each
carries a pinned seed, so a run of one is the same run anywhere.

A preset is the grammar in
[docs/MASTERPLAN.md](docs/MASTERPLAN.md) decision 10 -
`tests/fixtures/preset/accepted.txt` is a complete example. The file names the
scene only; grid mode and the plot mode are the exe's choices and have no key,
so a preset does not fully describe how a scene looks.

Nothing is applied partially. The file is read under an 8192-byte cap and
handed to the same fail-closed parser the harness tests; if any of that fails,
the exe says which error code and which line refused the file, and exits 1
without opening a window. Under `-smoke` and `-capture` the exit code is the
whole report - a modal box in an unattended run is a hang, not a message.

## Contributing

Issue-driven: every change starts as an issue and lands as a gated PR - see
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)

See NOTICE.md for the intended-use notice.
